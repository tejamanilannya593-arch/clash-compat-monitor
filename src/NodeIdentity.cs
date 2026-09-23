using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

public enum NodeIdentityStrength
{
    SessionOnly,
    Strong
}

public sealed class NodeIdentityMaterial
{
    private const int MaximumScalarCharacters = 8192;
    private const int MaximumParameters = 64;

    public NodeIdentityMaterial(string source, string protocol, string server, int port,
        IDictionary<string, string> parameters)
    {
        Source = Normalize(source, false);
        Protocol = Normalize(protocol, true);
        Server = Normalize(server, true);
        Port = port;
        var normalized = new SortedDictionary<string, string>(StringComparer.Ordinal);
        bool valid = parameters == null || parameters.Count <= MaximumParameters;
        foreach (KeyValuePair<string, string> item in parameters ?? new Dictionary<string, string>())
        {
            string key = Normalize(item.Key, true);
            string value = Normalize(item.Value, false);
            if (String.IsNullOrEmpty(key) || normalized.ContainsKey(key)) { valid = false; continue; }
            normalized[key] = value;
        }
        Parameters = normalized;
        IsValid = valid && !String.IsNullOrEmpty(Source) && !String.IsNullOrEmpty(Protocol) &&
            !String.IsNullOrEmpty(Server) && Port > 0 && Port <= 65535;
    }

    public string Source { get; private set; }
    public string Protocol { get; private set; }
    public string Server { get; private set; }
    public int Port { get; private set; }
    public IDictionary<string, string> Parameters { get; private set; }
    internal bool IsValid { get; private set; }

    private static string Normalize(string value, bool lower)
    {
        string normalized = (value ?? "").Trim();
        if (normalized.Length > MaximumScalarCharacters) return "";
        return lower ? normalized.ToLowerInvariant() : normalized;
    }
}

public static class NodeIdentity
{
    public static string Create(NodeIdentityMaterial material, byte[] key)
    {
        if (material == null || !material.IsValid || key == null || key.Length < 16) return "";
        byte[] bytes = Encoding.UTF8.GetBytes(Canonicalize(material));
        using (var hmac = new HMACSHA256(key))
        {
            return "node-v1-" + Convert.ToBase64String(hmac.ComputeHash(bytes))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }

    public static bool IsStrong(string nodeId)
    {
        return !String.IsNullOrEmpty(nodeId) && nodeId.StartsWith("node-v1-", StringComparison.Ordinal) &&
            nodeId.Length == 51;
    }

    private static string Canonicalize(NodeIdentityMaterial material)
    {
        var result = new StringBuilder();
        Append(result, material.Source);
        Append(result, material.Protocol);
        Append(result, material.Server);
        Append(result, material.Port.ToString(CultureInfo.InvariantCulture));
        foreach (KeyValuePair<string, string> item in material.Parameters.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            Append(result, item.Key);
            Append(result, item.Value);
        }
        return result.ToString();
    }

    private static void Append(StringBuilder target, string value)
    {
        string safe = value ?? "";
        target.Append(safe.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(safe);
    }
}

public sealed class ResolvedNodeIdentity
{
    public ResolvedNodeIdentity(string nodeId, NodeIdentityStrength strength, string reason)
    {
        NodeId = nodeId ?? "";
        Strength = strength;
        Reason = reason ?? "";
    }

    public string NodeId { get; private set; }
    public NodeIdentityStrength Strength { get; private set; }
    public string Reason { get; private set; }
}

public interface INodeIdentitySource
{
    IDictionary<string, ResolvedNodeIdentity> Resolve(IEnumerable<string> names);
}

public sealed class ClashNodeIdentitySource : INodeIdentitySource
{
    private const long MaximumFileBytes = 8L * 1024L * 1024L;
    private const int MaximumProxies = 4096;
    private static readonly HashSet<string> IdentityParameters = new HashSet<string>(
        new[] { "uuid", "password", "cipher", "network", "tls", "servername", "sni", "plugin" },
        StringComparer.OrdinalIgnoreCase);

    private readonly string configPath;
    private readonly string profilesPath;
    private readonly byte[] key;

    public ClashNodeIdentitySource(string configPath, string profilesPath, byte[] key)
    {
        this.configPath = configPath;
        this.profilesPath = profilesPath;
        this.key = key == null ? null : (byte[])key.Clone();
    }

    public IDictionary<string, ResolvedNodeIdentity> Resolve(IEnumerable<string> names)
    {
        var requested = new HashSet<string>(names ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        var result = requested.ToDictionary(x => x, x => SessionOnly("metadata-missing"), StringComparer.Ordinal);
        string profiles;
        string config;
        if (!TryReadStable(profilesPath, out profiles) || !TryReadStable(configPath, out config))
            return Fill(requested, "metadata-unavailable");

        string source = ReadCurrentSource(profiles);
        if (String.IsNullOrEmpty(source) || key == null || key.Length < 16)
            return Fill(requested, "identity-unavailable");

        IDictionary<string, List<IDictionary<string, string>>> proxies;
        if (!TryParseProxies(config, out proxies)) return Fill(requested, "metadata-invalid");
        foreach (string name in requested)
        {
            List<IDictionary<string, string>> matches;
            if (!proxies.TryGetValue(name, out matches)) continue;
            var identities = new HashSet<string>(StringComparer.Ordinal);
            bool incomplete = false;
            foreach (IDictionary<string, string> fields in matches)
            {
                int port;
                string protocol;
                string server;
                if (!fields.TryGetValue("type", out protocol) || !fields.TryGetValue("server", out server) ||
                    !fields.ContainsKey("port") || !Int32.TryParse(fields["port"], NumberStyles.None,
                        CultureInfo.InvariantCulture, out port))
                {
                    incomplete = true;
                    continue;
                }
                var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, string> field in fields)
                    if (IdentityParameters.Contains(field.Key)) parameters[field.Key] = field.Value;
                string nodeId = NodeIdentity.Create(
                    new NodeIdentityMaterial(source, protocol, server, port, parameters), key);
                if (String.IsNullOrEmpty(nodeId)) incomplete = true;
                else identities.Add(nodeId);
            }
            if (identities.Count > 1)
                result[name] = SessionOnly("duplicate-conflict");
            else if (identities.Count == 1 && !incomplete)
                result[name] = new ResolvedNodeIdentity(identities.First(), NodeIdentityStrength.Strong, "");
            else
                result[name] = SessionOnly("metadata-missing");
        }
        return result;
    }

    private static IDictionary<string, ResolvedNodeIdentity> Fill(IEnumerable<string> names, string reason)
    {
        return names.ToDictionary(x => x, x => SessionOnly(reason), StringComparer.Ordinal);
    }

    private static ResolvedNodeIdentity SessionOnly(string reason)
    {
        return new ResolvedNodeIdentity("", NodeIdentityStrength.SessionOnly, reason);
    }

    private static bool TryReadStable(string path, out string content)
    {
        content = "";
        try
        {
            var before = new FileInfo(path);
            if (!before.Exists || before.Length > MaximumFileBytes) return false;
            long length = before.Length;
            DateTime written = before.LastWriteTimeUtc;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                content = reader.ReadToEnd();
            var after = new FileInfo(path);
            return after.Exists && after.Length == length && after.LastWriteTimeUtc == written &&
                Encoding.UTF8.GetByteCount(content) <= MaximumFileBytes;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static string ReadCurrentSource(string yaml)
    {
        foreach (string line in SplitLines(yaml))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("current:", StringComparison.OrdinalIgnoreCase)) continue;
            return Scalar(trimmed.Substring("current:".Length));
        }
        return "";
    }

    private static bool TryParseProxies(string yaml,
        out IDictionary<string, List<IDictionary<string, string>>> proxies)
    {
        proxies = new Dictionary<string, List<IDictionary<string, string>>>(StringComparer.Ordinal);
        IDictionary<string, string> current = null;
        bool inProxies = false;
        int count = 0;
        foreach (string rawLine in SplitLines(yaml))
        {
            string trimmed = rawLine.Trim();
            if (!inProxies)
            {
                if (trimmed.Equals("proxies:", StringComparison.OrdinalIgnoreCase)) inProxies = true;
                continue;
            }
            if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal)) continue;
            if (!Char.IsWhiteSpace(rawLine[0]) && !trimmed.StartsWith("-", StringComparison.Ordinal)) break;
            if (trimmed.StartsWith("-", StringComparison.Ordinal))
            {
                if (current != null) AddProxy(proxies, current);
                if (++count > MaximumProxies) return false;
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string body = trimmed.Substring(1).Trim();
                if (body.StartsWith("{", StringComparison.Ordinal) && body.EndsWith("}", StringComparison.Ordinal))
                {
                    foreach (string part in SplitFlow(body.Substring(1, body.Length - 2)))
                        AddScalar(current, part);
                }
                else if (body.Length > 0) AddScalar(current, body);
            }
            else if (current != null)
            {
                AddScalar(current, trimmed);
            }
        }
        if (current != null) AddProxy(proxies, current);
        return true;
    }

    private static void AddProxy(IDictionary<string, List<IDictionary<string, string>>> proxies,
        IDictionary<string, string> fields)
    {
        string name;
        if (!fields.TryGetValue("name", out name) || String.IsNullOrEmpty(name)) return;
        List<IDictionary<string, string>> entries;
        if (!proxies.TryGetValue(name, out entries))
        {
            entries = new List<IDictionary<string, string>>();
            proxies[name] = entries;
        }
        entries.Add(fields);
    }

    private static void AddScalar(IDictionary<string, string> fields, string text)
    {
        int colon = FindSeparator(text, ':');
        if (colon <= 0) return;
        string name = Scalar(text.Substring(0, colon)).ToLowerInvariant();
        string value = Scalar(text.Substring(colon + 1));
        if (name.Length > 0 && value.Length <= 8192 && !fields.ContainsKey(name)) fields[name] = value;
    }

    private static IEnumerable<string> SplitFlow(string text)
    {
        int start = 0;
        char quote = '\0';
        int depth = 0;
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (quote != '\0')
            {
                if (character == quote && (index == 0 || text[index - 1] != '\\')) quote = '\0';
                continue;
            }
            if (character == '\'' || character == '"') quote = character;
            else if (character == '{' || character == '[') depth++;
            else if (character == '}' || character == ']') depth--;
            else if (character == ',' && depth == 0)
            {
                yield return text.Substring(start, index - start);
                start = index + 1;
            }
        }
        yield return text.Substring(start);
    }

    private static int FindSeparator(string text, char separator)
    {
        char quote = '\0';
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (quote != '\0')
            {
                if (character == quote && (index == 0 || text[index - 1] != '\\')) quote = '\0';
            }
            else if (character == '\'' || character == '"') quote = character;
            else if (character == separator) return index;
        }
        return -1;
    }

    private static string Scalar(string text)
    {
        string value = (text ?? "").Trim();
        int comment = FindSeparator(value, '#');
        if (comment >= 0) value = value.Substring(0, comment).TrimEnd();
        if (value.Length >= 2 && ((value[0] == '"' && value[value.Length - 1] == '"') ||
            (value[0] == '\'' && value[value.Length - 1] == '\'')))
            value = value.Substring(1, value.Length - 2);
        return value.Length <= 8192 ? value : "";
    }

    private static IEnumerable<string> SplitLines(string content)
    {
        return (content ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }
}
