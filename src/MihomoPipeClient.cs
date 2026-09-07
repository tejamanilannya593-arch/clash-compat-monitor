using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.RegularExpressions;
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

public sealed class MihomoPipeClient : IMihomoClient
{
    private readonly string pipeName;
    private readonly string secret;
    private readonly JavaScriptSerializer json = new JavaScriptSerializer();

    public MihomoPipeClient(string pipeName, string secret)
    {
        this.pipeName = pipeName;
        this.secret = secret ?? "";
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
        return new MihomoPipeClient("verge-mihomo", found);
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
            PipeHttpResponse response = Request("GET", path, null);
            if (response.StatusCode < 200 || response.StatusCode >= 300) return Int32.MaxValue;
            var value = json.DeserializeObject(response.Body) as Dictionary<string, object>;
            object delay;
            return value != null && value.TryGetValue("delay", out delay)
                ? Convert.ToInt32(delay, CultureInfo.InvariantCulture) : Int32.MaxValue;
        }
        catch (IOException) { return Int32.MaxValue; }
        catch (TimeoutException) { return Int32.MaxValue; }
    }

    public bool EnsureIpv4Compatibility(string configPath)
    {
        string original = File.ReadAllText(configPath, Encoding.UTF8);
        var top = new Regex(@"(?m)^ipv6:\s*(?:true|false)\s*$");
        var dns = new Regex(@"(?ms)(^dns:\s*$.*?^\s+)ipv6:\s*(?:true|false)\s*$");
        if (!top.IsMatch(original) || !dns.IsMatch(original))
            throw new InvalidDataException("Required IPv6 settings were not found in the generated configuration.");
        string payload = top.Replace(original, "ipv6: false", 1);
        payload = dns.Replace(payload, "${1}ipv6: false", 1);
        string body = json.Serialize(new Dictionary<string, object> { { "path", "" }, { "payload", payload } });
        PipeHttpResponse response = Request("PUT", "/configs?force=true", body);
        return response.StatusCode >= 200 && response.StatusCode < 300;
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
        byte[] bodyBytes = body == null ? new byte[0] : Encoding.UTF8.GetBytes(body);
        var request = new StringBuilder();
        request.Append(method).Append(' ').Append(path).Append(" HTTP/1.1\r\nHost: mihomo\r\nConnection: close\r\n");
        if (secret.Length > 0) request.Append("Authorization: Bearer ").Append(secret).Append("\r\n");
        if (bodyBytes.Length > 0) request.Append("Content-Type: application/json\r\n");
        request.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n\r\n");
        byte[] headerBytes = Encoding.ASCII.GetBytes(request.ToString());

        using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None))
        {
            pipe.Connect(3000);
            pipe.Write(headerBytes, 0, headerBytes.Length);
            if (bodyBytes.Length > 0) pipe.Write(bodyBytes, 0, bodyBytes.Length);
            pipe.Flush();
            using (var response = new MemoryStream())
            {
                var buffer = new byte[4096];
                int read;
                while ((read = pipe.Read(buffer, 0, buffer.Length)) > 0) response.Write(buffer, 0, read);
                return PipeHttpCodec.Decode(response.ToArray());
            }
        }
    }
}
