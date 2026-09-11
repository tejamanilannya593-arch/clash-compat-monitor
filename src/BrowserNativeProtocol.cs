using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

public sealed class BrowserBridgeMessage
{
    public string Type { get; set; }
    public int ProtocolVersion { get; set; }
    public string RequestId { get; set; }
    public string Browser { get; set; }
    public string ExtensionVersion { get; set; }
    public string TaskId { get; set; }
    public string Service { get; set; }
    public string Challenge { get; set; }
    public string Outcome { get; set; }
    public bool MessageSent { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public string Node { get; set; }
    public string ExitFingerprint { get; set; }
    public string Url { get; set; }
    public string Prompt { get; set; }
    public string ExpiresUtc { get; set; }
    public string Error { get; set; }
}

public static class BrowserNativeProtocol
{
    public const int MaxFrameBytes = 64 * 1024;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    public static BrowserBridgeMessage Read(Stream input)
    {
        if (input == null) throw new ArgumentNullException("input");
        var prefix = new byte[4];
        int first = input.Read(prefix, 0, prefix.Length);
        if (first == 0) return null;
        ReadExactly(input, prefix, first, prefix.Length - first);
        int length = prefix[0] | (prefix[1] << 8) | (prefix[2] << 16) | (prefix[3] << 24);
        if (length <= 0 || length > MaxFrameBytes)
            throw new InvalidDataException("Native message length is outside the allowed range.");

        var payload = new byte[length];
        ReadExactly(input, payload, 0, length);
        try
        {
            string json = Utf8.GetString(payload);
            var serializer = new JavaScriptSerializer { MaxJsonLength = MaxFrameBytes, RecursionLimit = 16 };
            BrowserBridgeMessage message = serializer.Deserialize<BrowserBridgeMessage>(json);
            if (message == null) throw new InvalidDataException("Native message is empty.");
            return message;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (!(ex is ArgumentException || ex is InvalidOperationException || ex is DecoderFallbackException))
                throw;
            throw new InvalidDataException("Native message JSON is invalid.", ex);
        }
    }

    public static void Write(Stream output, BrowserBridgeMessage message)
    {
        if (output == null) throw new ArgumentNullException("output");
        if (message == null) throw new ArgumentNullException("message");
        var serializer = new JavaScriptSerializer { MaxJsonLength = MaxFrameBytes, RecursionLimit = 16 };
        byte[] payload = Utf8.GetBytes(serializer.Serialize(message));
        if (payload.Length == 0 || payload.Length > MaxFrameBytes)
            throw new InvalidDataException("Native message length is outside the allowed range.");
        byte[] prefix = {
            (byte)payload.Length,
            (byte)(payload.Length >> 8),
            (byte)(payload.Length >> 16),
            (byte)(payload.Length >> 24)
        };
        output.Write(prefix, 0, prefix.Length);
        output.Write(payload, 0, payload.Length);
        output.Flush();
    }

    private static void ReadExactly(Stream input, byte[] buffer, int offset, int count)
    {
        while (count > 0)
        {
            int read = input.Read(buffer, offset, count);
            if (read <= 0) throw new EndOfStreamException("Native message ended unexpectedly.");
            offset += read;
            count -= read;
        }
    }
}

public static class BrowserOriginPolicy
{
    public const string ExtensionId = "micoadiomajggfdfbnhjbpkbccjoldlg";
    public const string AllowedOrigin = "chrome-extension://" + ExtensionId + "/";

    public static bool IsAllowed(string origin)
    {
        return String.Equals(origin, AllowedOrigin, StringComparison.Ordinal);
    }
}

public static class BrowserMessageValidator
{
    private static readonly string[] AllowedTypes = { "hello", "poll", "result", "cancel" };
    private static readonly string[] AllowedOutcomes = Enum.GetNames(typeof(BrowserVerificationOutcome));

    public static bool IsValidRequest(BrowserBridgeMessage message)
    {
        if (message == null || message.ProtocolVersion != BrowserConversationProof.CurrentProtocolVersion ||
            !AllowedTypes.Contains(message.Type, StringComparer.Ordinal) || !IsToken(message.RequestId, 64) ||
            !IsBrowser(message.Browser) || !IsVersion(message.ExtensionVersion))
            return false;

        if (String.Equals(message.Type, "result", StringComparison.Ordinal))
            return IsTaskId(message.TaskId) && IsAiService(message.Service) && IsChallenge(message.Challenge) &&
                AllowedOutcomes.Contains(message.Outcome, StringComparer.Ordinal) &&
                message.ElapsedMilliseconds >= 0 && message.ElapsedMilliseconds <= 5 * 60 * 1000 &&
                IsOptionalText(message.Url, 2048) && IsOptionalText(message.Error, 512);
        if (String.Equals(message.Type, "cancel", StringComparison.Ordinal))
            return IsTaskId(message.TaskId);
        return true;
    }

    private static bool IsBrowser(string value)
    {
        return String.Equals(value, "Chrome", StringComparison.Ordinal) ||
            String.Equals(value, "Edge", StringComparison.Ordinal);
    }

    private static bool IsAiService(string value)
    {
        return String.Equals(value, "ChatGPT", StringComparison.Ordinal) ||
            String.Equals(value, "Gemini", StringComparison.Ordinal);
    }

    private static bool IsVersion(string value)
    {
        return IsToken(value, 32);
    }

    private static bool IsTaskId(string value)
    {
        return value != null && value.Length == 32 && value.All(IsLowerHex);
    }

    private static bool IsChallenge(string value)
    {
        return value != null && value.Length >= 5 && value.Length <= 80 &&
            value.StartsWith("CCM-", StringComparison.Ordinal) &&
            value.Skip(4).All(character => IsAsciiLetterOrDigit(character) || character == '-');
    }

    private static bool IsToken(string value, int maximumLength)
    {
        return !String.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
            value.All(character => IsAsciiLetterOrDigit(character) || character == '-' || character == '_' || character == '.');
    }

    private static bool IsOptionalText(string value, int maximumLength)
    {
        return value == null || value.Length <= maximumLength;
    }

    private static bool IsAsciiLetterOrDigit(char value)
    {
        return (value >= '0' && value <= '9') || (value >= 'A' && value <= 'Z') ||
            (value >= 'a' && value <= 'z');
    }

    private static bool IsLowerHex(char value)
    {
        return (value >= '0' && value <= '9') || (value >= 'a' && value <= 'f');
    }
}
