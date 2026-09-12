using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;

public static class BrowserPipeIdentity
{
    public static string ForCurrentUser()
    {
        SecurityIdentifier sid = WindowsIdentity.GetCurrent().User;
        if (sid == null) throw new InvalidOperationException("The current Windows user SID is unavailable.");
        byte[] source = Encoding.UTF8.GetBytes(sid.Value);
        byte[] hash;
        using (SHA256 sha = SHA256.Create()) hash = sha.ComputeHash(source);
        var suffix = new StringBuilder(24);
        for (int i = 0; i < 12; i++) suffix.Append(hash[i].ToString("x2"));
        return "ccm-browser-" + suffix;
    }
}

public sealed class BrowserBridgeServer : IDisposable
{
    private readonly string pipeName;
    private readonly Func<BrowserBridgeMessage, BrowserBridgeMessage> handler;
    private readonly object gate = new object();
    private Thread thread;
    private NamedPipeServerStream active;
    private volatile bool stopping;

    public BrowserBridgeServer(string pipeName, Func<BrowserBridgeMessage, BrowserBridgeMessage> handler)
    {
        if (String.IsNullOrWhiteSpace(pipeName)) throw new ArgumentException("A pipe name is required.", "pipeName");
        if (handler == null) throw new ArgumentNullException("handler");
        this.pipeName = pipeName;
        this.handler = handler;
    }

    public Exception LastError { get; private set; }

    public void Start()
    {
        lock (gate)
        {
            if (thread != null) throw new InvalidOperationException("The browser bridge is already started.");
            thread = new Thread(Run) { IsBackground = true, Name = "CCM browser bridge" };
            thread.Start();
        }
    }

    public void Dispose()
    {
        stopping = true;
        NamedPipeServerStream stream;
        Thread worker;
        lock (gate)
        {
            stream = active;
            worker = thread;
        }
        if (stream != null)
        {
            try { stream.Dispose(); }
            catch (IOException) { }
        }
        WakeServer();
        if (worker != null && worker != Thread.CurrentThread) worker.Join(2000);
    }

    private void Run()
    {
        while (!stopping)
        {
            try
            {
                using (NamedPipeServerStream stream = CreateServer())
                {
                    lock (gate) active = stream;
                    stream.WaitForConnection();
                    if (stopping) return;
                    BrowserBridgeMessage request = BrowserNativeProtocol.Read(stream);
                    if (request == null) continue;
                    BrowserBridgeMessage response;
                    if (!BrowserMessageValidator.IsValidRequest(request))
                        response = Error(request.RequestId, "invalid-request");
                    else
                        response = handler(request) ?? Error(request.RequestId, "empty-response");
                    BrowserNativeProtocol.Write(stream, response);
                }
            }
            catch (ObjectDisposedException)
            {
                if (!stopping) LastError = new IOException("Browser bridge was closed unexpectedly.");
            }
            catch (IOException ex)
            {
                if (!stopping) LastError = ex;
            }
            catch (Exception ex)
            {
                LastError = ex;
            }
            finally
            {
                lock (gate) active = null;
            }
        }
    }

    private NamedPipeServerStream CreateServer()
    {
        SecurityIdentifier sid = WindowsIdentity.GetCurrent().User;
        if (sid == null) throw new InvalidOperationException("The current Windows user SID is unavailable.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(sid);
        security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.None, 4096, 4096, security);
    }

    private void WakeServer()
    {
        try
        {
            using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut)) client.Connect(100);
        }
        catch (IOException) { }
        catch (TimeoutException) { }
    }

    private static BrowserBridgeMessage Error(string requestId, string error)
    {
        return new BrowserBridgeMessage {
            Type = "error",
            ProtocolVersion = BrowserNativeProtocol.ProtocolVersion,
            RequestId = requestId,
            Error = error
        };
    }
}

public static class BrowserHostBridge
{
    public static BrowserBridgeMessage RoundTrip(string pipeName, BrowserBridgeMessage request, TimeSpan timeout)
    {
        if (String.IsNullOrWhiteSpace(pipeName)) throw new ArgumentException("A pipe name is required.", "pipeName");
        if (request == null) throw new ArgumentNullException("request");
        int timeoutMilliseconds = (int)Math.Max(1, Math.Min(Int32.MaxValue, timeout.TotalMilliseconds));
        try
        {
            using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
            {
                client.Connect(timeoutMilliseconds);
                BrowserNativeProtocol.Write(client, request);
                BrowserBridgeMessage response = BrowserNativeProtocol.Read(client);
                return response ?? Unavailable(request.RequestId);
            }
        }
        catch (TimeoutException) { return Unavailable(request.RequestId); }
        catch (IOException) { return Unavailable(request.RequestId); }
        catch (UnauthorizedAccessException) { return Unavailable(request.RequestId); }
        catch (InvalidDataException) { return Unavailable(request.RequestId); }
    }

    private static BrowserBridgeMessage Unavailable(string requestId)
    {
        return new BrowserBridgeMessage {
            Type = "unavailable",
            ProtocolVersion = BrowserNativeProtocol.ProtocolVersion,
            RequestId = requestId,
            Error = "monitor-unavailable"
        };
    }
}
