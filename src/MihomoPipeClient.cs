using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

public interface IMihomoClient
{
    string[] GetChoices(string groupName);
    string GetSelected(string groupName);
    void Select(string groupName, string proxyName);
    int GetDelay(string proxyName, string url, int timeoutMilliseconds);
    bool IsRuntimeIpv6Enabled();
    bool IsAvailable();
}

public sealed class PipeHttpResponse
{
    public PipeHttpResponse(int statusCode, string body)
    {
        StatusCode = statusCode;
        Body = body;
    }

    public int StatusCode { get; private set; }
    public string Body { get; private set; }
}

public static class PipeHttpCodec
{
    public static PipeHttpResponse Decode(byte[] response)
    {
        if (response == null) throw new ArgumentNullException("response");
        int headerEnd = IndexOf(response, new byte[] { 13, 10, 13, 10 }, 0);
        if (headerEnd < 0) throw new InvalidDataException("HTTP response headers are incomplete.");
        string headersText = Encoding.ASCII.GetString(response, 0, headerEnd);
        string[] lines = headersText.Split(new[] { "\r\n" }, StringSplitOptions.None);
        string[] status = lines[0].Split(' ');
        int statusCode;
        if (status.Length < 2 || !int.TryParse(status[1], out statusCode)) throw new InvalidDataException("Invalid HTTP status line.");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon > 0) headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
        }

        int bodyOffset = headerEnd + 4;
        byte[] body;
        string transferEncoding;
        if (headers.TryGetValue("Transfer-Encoding", out transferEncoding) && transferEncoding.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
            body = DecodeChunks(response, bodyOffset);
        else
        {
            int length = response.Length - bodyOffset;
            string contentLength;
            int declared;
            if (headers.TryGetValue("Content-Length", out contentLength) && int.TryParse(contentLength, out declared)) length = Math.Min(length, declared);
            body = new byte[Math.Max(0, length)];
            Buffer.BlockCopy(response, bodyOffset, body, 0, body.Length);
        }
        return new PipeHttpResponse(statusCode, Encoding.UTF8.GetString(body));
    }

    private static byte[] DecodeChunks(byte[] source, int offset)
    {
        using (var output = new MemoryStream())
        {
            int position = offset;
            while (position < source.Length)
            {
                int lineEnd = IndexOf(source, new byte[] { 13, 10 }, position);
                if (lineEnd < 0) throw new InvalidDataException("Invalid chunk length line.");
                string sizeText = Encoding.ASCII.GetString(source, position, lineEnd - position).Split(';')[0];
                int size;
                if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out size)) throw new InvalidDataException("Invalid chunk size.");
                position = lineEnd + 2;
                if (size == 0) break;
                if (position + size > source.Length) throw new InvalidDataException("Chunk body is incomplete.");
                output.Write(source, position, size);
                position += size;
                if (position + 1 >= source.Length || source[position] != 13 || source[position + 1] != 10) throw new InvalidDataException("Chunk terminator is missing.");
                position += 2;
            }
            return output.ToArray();
        }
    }

    private static int IndexOf(byte[] source, byte[] pattern, int start)
    {
        for (int i = start; i <= source.Length - pattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++) if (source[i + j] != pattern[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }
}

public sealed class MihomoPipeClient : IMihomoClient, IRecoveryConnectivityClient, IProxyTopologyClient
{
    private string pipeName;
    private readonly string secret;
    private readonly bool discoverPipe;
    private readonly JavaScriptSerializer json = new JavaScriptSerializer();

    public MihomoPipeClient(string pipeName, string secret)
    {
        this.pipeName = pipeName;
        this.secret = secret ?? "";
    }

    private MihomoPipeClient(string pipeName, string secret, bool discoverPipe)
        : this(pipeName, secret)
    {
        this.discoverPipe = discoverPipe;
    }

    public static string SelectAvailablePipeName(IEnumerable<string> names, string preferred)
    {
        string[] available = (names ?? Enumerable.Empty<string>()).Where(x => x != null)
            .Select(Path.GetFileName).ToArray();
        if (available.Contains(preferred, StringComparer.OrdinalIgnoreCase)) return preferred;
        if (available.Contains("verge-mihomo", StringComparer.OrdinalIgnoreCase)) return "verge-mihomo";
        return available.Where(x => x.StartsWith("verge-mihomo-production-",
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? preferred;
    }

    private static string DiscoverPipeName(string preferred)
    {
        try { return SelectAvailablePipeName(Directory.GetFiles(@"\\.\pipe\"), preferred); }
        catch (IOException) { return preferred; }
        catch (UnauthorizedAccessException) { return preferred; }
        catch (ArgumentException) { return preferred; }
    }

    private bool RefreshPipeName(string attempted)
    {
        if (!discoverPipe) return false;
        string current = System.Threading.Volatile.Read(ref pipeName);
        string found = DiscoverPipeName(current);
        if (String.Equals(found, attempted, StringComparison.OrdinalIgnoreCase)) return false;
        System.Threading.Volatile.Write(ref pipeName, found);
        return true;
    }

    public static MihomoPipeClient FromConfig(string configPath)
    {
        string found = "";
        foreach (string rawLine in File.ReadAllLines(configPath))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith("secret:", StringComparison.OrdinalIgnoreCase)) continue;
            found = line.Substring(line.IndexOf(':') + 1).Trim().Trim('\'', '"');
            break;
        }
        return new MihomoPipeClient(DiscoverPipeName("verge-mihomo"), found, true);
    }

    public string[] GetChoices(string groupName)
    {
        var group = GetGroup(groupName);
        object all;
        if (!group.TryGetValue("all", out all)) return new string[0];
        var values = all as object[];
        if (values == null) return new string[0];
        var result = new string[values.Length];
        for (int i = 0; i < values.Length; i++) result[i] = Convert.ToString(values[i], CultureInfo.InvariantCulture);
        return result;
    }

    public IDictionary<string, string> GetProxyTypes()
    {
        PipeHttpResponse response = Request("GET", "/proxies", null);
        if (response.StatusCode != 200) throw new IOException("Mihomo proxy query failed with HTTP " + response.StatusCode + ".");
        var root = json.DeserializeObject(response.Body) as Dictionary<string, object>;
        object proxiesObject;
        var proxies = root != null && root.TryGetValue("proxies", out proxiesObject)
            ? proxiesObject as Dictionary<string, object> : null;
        if (proxies == null) throw new InvalidDataException("Mihomo proxy catalog is missing.");
        var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in proxies)
        {
            var proxy = entry.Value as Dictionary<string, object>;
            object kind;
            object choices;
            if (proxy != null && proxy.TryGetValue("all", out choices) && choices is object[])
                kinds[entry.Key] = "Selector";
            else if (proxy != null && proxy.TryGetValue("type", out kind) && kind != null)
                kinds[entry.Key] = Convert.ToString(kind, CultureInfo.InvariantCulture);
        }
        return kinds;
    }

    public string GetSelected(string groupName)
    {
        object selected;
        return GetGroup(groupName).TryGetValue("now", out selected) ? Convert.ToString(selected, CultureInfo.InvariantCulture) : null;
    }

    public void Select(string groupName, string proxyName)
    {
        string body = json.Serialize(new Dictionary<string, object> { { "name", proxyName } });
        PipeHttpResponse response = Request("PUT", "/proxies/" + Uri.EscapeDataString(groupName), body);
        if (response.StatusCode < 200 || response.StatusCode >= 300) throw new IOException("Mihomo rejected selector update with HTTP " + response.StatusCode + ".");
    }

    public int GetDelay(string proxyName, string url, int timeoutMilliseconds)
    {
        try
        {
            string path = "/proxies/" + Uri.EscapeDataString(proxyName) + "/delay?timeout=" +
                timeoutMilliseconds.ToString(CultureInfo.InvariantCulture) + "&url=" + Uri.EscapeDataString(url);
            PipeHttpResponse response = Request("GET", path, null,
                Math.Min(1000, Math.Max(250, timeoutMilliseconds)),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(Math.Max(1000, timeoutMilliseconds + 500)));
            if (response.StatusCode < 200 || response.StatusCode >= 300) return Int32.MaxValue;
            var value = json.DeserializeObject(response.Body) as Dictionary<string, object>;
            object delay;
            return value != null && value.TryGetValue("delay", out delay)
                ? Convert.ToInt32(delay, CultureInfo.InvariantCulture) : Int32.MaxValue;
        }
        catch (IOException) { return Int32.MaxValue; }
        catch (TimeoutException) { return Int32.MaxValue; }
        catch (FormatException) { return Int32.MaxValue; }
        catch (InvalidCastException) { return Int32.MaxValue; }
        catch (OverflowException) { return Int32.MaxValue; }
    }

    public IDictionary<string, ProxyTopologyEntry> ReadProxyTopology()
    {
        PipeHttpResponse response = Request("GET", "/proxies", null);
        if (response.StatusCode != 200) throw new IOException("Cannot read proxy topology.");
        var root = new JavaScriptSerializer().DeserializeObject(response.Body) as Dictionary<string, object>;
        object raw;
        var proxies = root != null && root.TryGetValue("proxies", out raw) ? raw as Dictionary<string, object> : null;
        if (proxies == null) throw new InvalidDataException("Proxy topology is missing.");
        var result = new Dictionary<string, ProxyTopologyEntry>(StringComparer.Ordinal);
        foreach (var pair in proxies)
        {
            var item = pair.Value as Dictionary<string, object>;
            if (item == null) continue;
            object type, selected, choices;
            item.TryGetValue("type", out type); item.TryGetValue("now", out selected); item.TryGetValue("all", out choices);
            result[pair.Key] = new ProxyTopologyEntry { Type = Convert.ToString(type), Selected = Convert.ToString(selected),
                Choices = (choices as object[] ?? new object[0]).Select(Convert.ToString).ToArray() };
        }
        return result;
    }

    public string GetRoutingMode()
    {
        PipeHttpResponse response = Request("GET", "/configs", null);
        if (response.StatusCode != 200) throw new IOException("Cannot read routing mode.");
        var root = new JavaScriptSerializer().DeserializeObject(response.Body) as Dictionary<string, object>;
        object mode;
        return root != null && root.TryGetValue("mode", out mode) ? Convert.ToString(mode) : "rule";
    }

    public RecoveryEvidence CheckConnectivity(string node, string url, int timeoutMilliseconds, out int milliseconds)
    {
        milliseconds = 0;
        try
        {
            string path = "/proxies/" + Uri.EscapeDataString(node) + "/delay?timeout=" +
                timeoutMilliseconds.ToString(CultureInfo.InvariantCulture) + "&url=" + Uri.EscapeDataString(url);
            PipeHttpResponse response = Request("GET", path, null, 1000, TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(timeoutMilliseconds + 500));
            // A transport/API error is not evidence that the selected node failed.
            if (response.StatusCode == 504) return RecoveryEvidence.Failed;
            if (response.StatusCode != 200) return RecoveryEvidence.Unknown;
            var value = new JavaScriptSerializer().DeserializeObject(response.Body) as Dictionary<string, object>;
            object delay;
            if (value == null || !value.TryGetValue("delay", out delay)) return RecoveryEvidence.Unknown;
            int measured = Convert.ToInt32(delay, CultureInfo.InvariantCulture);
            if (measured < 0 || measured == Int32.MaxValue) return RecoveryEvidence.Unknown;
            milliseconds = measured;
            return RecoveryEvidence.Healthy;
        }
        catch (Exception ex)
        {
            if (!(ex is IOException || ex is TimeoutException || ex is ArgumentException ||
                ex is InvalidOperationException || ex is FormatException || ex is InvalidCastException || ex is OverflowException)) throw;
            return RecoveryEvidence.Unknown;
        }
    }

    public bool IsRuntimeIpv6Enabled()
    {
        PipeHttpResponse response = Request("GET", "/configs", null);
        if (response.StatusCode != 200) throw new IOException("Mihomo runtime configuration query failed with HTTP " + response.StatusCode + ".");
        var root = json.DeserializeObject(response.Body) as Dictionary<string, object>;
        object value;
        return root != null && root.TryGetValue("ipv6", out value) && Convert.ToBoolean(value, CultureInfo.InvariantCulture);
    }

    public bool IsAvailable()
    {
        try
        {
            PipeHttpResponse response = Request("GET", "/version", null);
            return response.StatusCode >= 200 && response.StatusCode < 300;
        }
        catch (IOException) { return false; }
        catch (TimeoutException) { return false; }
    }

    private Dictionary<string, object> GetGroup(string groupName)
    {
        PipeHttpResponse response = Request("GET", "/proxies", null);
        if (response.StatusCode != 200) throw new IOException("Mihomo proxy query failed with HTTP " + response.StatusCode + ".");
        var root = json.DeserializeObject(response.Body) as Dictionary<string, object>;
        object proxiesObject;
        var proxies = root != null && root.TryGetValue("proxies", out proxiesObject) ? proxiesObject as Dictionary<string, object> : null;
        object groupObject;
        var group = proxies != null && proxies.TryGetValue(groupName, out groupObject) ? groupObject as Dictionary<string, object> : null;
        if (group == null) throw new KeyNotFoundException("Mihomo selector group was not found: " + groupName);
        return group;
    }

    private PipeHttpResponse Request(string method, string path, string body)
    {
        return Request(method, path, body, 3000, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10));
    }

    private PipeHttpResponse Request(string method, string path, string body,
        int connectTimeoutMilliseconds, TimeSpan writeTimeout, TimeSpan readTimeout)
    {
        string attempted = System.Threading.Volatile.Read(ref pipeName);
        try { return RequestOnPipe(attempted, method, path, body,
            connectTimeoutMilliseconds, writeTimeout, readTimeout); }
        catch (IOException)
        {
            if (!RefreshPipeName(attempted)) throw;
        }
        catch (TimeoutException)
        {
            if (!RefreshPipeName(attempted)) throw;
        }
        return RequestOnPipe(System.Threading.Volatile.Read(ref pipeName), method, path, body,
            connectTimeoutMilliseconds, writeTimeout, readTimeout);
    }

    private PipeHttpResponse RequestOnPipe(string selectedPipe, string method, string path, string body,
        int connectTimeoutMilliseconds, TimeSpan writeTimeout, TimeSpan readTimeout)
    {
        byte[] bodyBytes = body == null ? new byte[0] : Encoding.UTF8.GetBytes(body);
        var request = new StringBuilder();
        request.Append(method).Append(' ').Append(path).Append(" HTTP/1.1\r\nHost: mihomo\r\nConnection: close\r\n");
        if (secret.Length > 0) request.Append("Authorization: Bearer ").Append(secret).Append("\r\n");
        if (bodyBytes.Length > 0) request.Append("Content-Type: application/json\r\n");
        request.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n\r\n");
        byte[] headerBytes = Encoding.ASCII.GetBytes(request.ToString());

        using (var pipe = new NamedPipeClientStream(".", selectedPipe, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            pipe.Connect(connectTimeoutMilliseconds);
            BoundedPipeIo.WriteAll(pipe, headerBytes, bodyBytes, writeTimeout);
            return PipeHttpCodec.Decode(BoundedPipeIo.ReadAll(pipe, readTimeout, 1024 * 1024));
        }
    }
}
