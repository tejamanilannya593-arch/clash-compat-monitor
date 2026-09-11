using System;
using System.IO;

public static class BrowserHostProgram
{
    public static int Main(string[] args)
    {
        if (args != null && args.Length == 1 && String.Equals(args[0], "--self-test", StringComparison.Ordinal))
            return SelfTest();
        if (args == null || args.Length == 0 || !BrowserOriginPolicy.IsAllowed(args[0]))
        {
            Console.Error.WriteLine("ClashCompatibilityMonitor browser host rejected the extension origin.");
            return 2;
        }

        try
        {
            Stream input = Console.OpenStandardInput();
            Stream output = Console.OpenStandardOutput();
            while (true)
            {
                BrowserBridgeMessage request = BrowserNativeProtocol.Read(input);
                if (request == null) return 0;
                BrowserBridgeMessage response;
                if (!BrowserMessageValidator.IsValidRequest(request))
                {
                    response = new BrowserBridgeMessage {
                        Type = "error",
                        ProtocolVersion = BrowserNativeProtocol.ProtocolVersion,
                        RequestId = request.RequestId,
                        Error = "invalid-request"
                    };
                }
                else
                {
                    response = BrowserHostBridge.RoundTrip(BrowserPipeIdentity.ForCurrentUser(), request,
                        TimeSpan.FromSeconds(30));
                }
                BrowserNativeProtocol.Write(output, response);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ClashCompatibilityMonitor browser host failed: " + ex.GetType().Name);
            return 3;
        }
    }

    private static int SelfTest()
    {
        try
        {
            var source = new BrowserBridgeMessage {
                Type = "hello",
                ProtocolVersion = BrowserNativeProtocol.ProtocolVersion,
                RequestId = "self-test",
                Browser = "Chrome",
                ExtensionVersion = "0.6.2"
            };
            using (var stream = new MemoryStream())
            {
                BrowserNativeProtocol.Write(stream, source);
                stream.Position = 0;
                BrowserBridgeMessage restored = BrowserNativeProtocol.Read(stream);
                if (!BrowserMessageValidator.IsValidRequest(restored)) return 1;
            }
            return String.IsNullOrWhiteSpace(BrowserPipeIdentity.ForCurrentUser()) ? 1 : 0;
        }
        catch
        {
            return 1;
        }
    }
}
