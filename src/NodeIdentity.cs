using System;
using System.Collections.Generic;
using System.Globalization;
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
