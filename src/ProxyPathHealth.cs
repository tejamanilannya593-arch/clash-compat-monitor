using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;

public sealed class ProxyPathHealth
{
    private ProxyPathHealth(bool probeGoogle, bool probeGithub, bool systemGoogle, bool systemGithub)
    {
        ProbeReachable = probeGoogle || probeGithub;
        SystemReachable = systemGoogle || systemGithub;
        Mismatch = probeGoogle != systemGoogle || probeGithub != systemGithub;
        var differences = new List<string>();
        if (probeGoogle != systemGoogle) differences.Add(Describe("Google", probeGoogle, systemGoogle));
        if (probeGithub != systemGithub) differences.Add(Describe("GitHub", probeGithub, systemGithub));
        MismatchDetail = String.Join("；", differences);
    }

    public bool ProbeReachable { get; private set; }
    public bool SystemReachable { get; private set; }
    public bool Mismatch { get; private set; }
    public string MismatchDetail { get; private set; }
    public bool CanAutoSwitch { get { return !Mismatch; } }

    public static ProxyPathHealth Evaluate(bool probeGoogle, bool probeGithub,
        bool systemGoogle, bool systemGithub)
    {
        return new ProxyPathHealth(probeGoogle, probeGithub, systemGoogle, systemGithub);
    }

    private static string Describe(string site, bool probe, bool system)
    {
        return site + "：探测入口" + (probe ? "可用" : "失败") + "、系统代理入口" + (system ? "可用" : "失败");
    }
}

public interface IProxyPathHealthChecker
{
    ProxyPathHealth Check();
}

public sealed class ProxyPathHealthChecker : IProxyPathHealthChecker
{
    private readonly string probeProxy;
    private readonly string systemProxy;

    public ProxyPathHealthChecker(string probeProxy, string systemProxy)
    {
        this.probeProxy = probeProxy;
        this.systemProxy = systemProxy;
    }

    public ProxyPathHealth Check()
    {
        bool probeGoogle = CheckEndpoint(probeProxy, "https://www.google.com/generate_204", 204);
        bool systemGoogle = CheckEndpoint(systemProxy, "https://www.google.com/generate_204", 204);
        bool probeGithub = CheckEndpoint(probeProxy, "https://github.com/", 200);
        bool systemGithub = CheckEndpoint(systemProxy, "https://github.com/", 200);
        return ProxyPathHealth.Evaluate(probeGoogle, probeGithub, systemGoogle, systemGithub);
    }

    private static bool CheckEndpoint(string proxy, string url, int expectedStatus)
    {
        try
        {
            using (var handler = new HttpClientHandler { Proxy = new WebProxy(proxy), UseProxy = true,
                AllowAutoRedirect = false })
            using (var client = new HttpClient(handler))
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            using (var request = new HttpRequestMessage(HttpMethod.Head, url))
            using (var response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellation.Token).GetAwaiter().GetResult())
                return (int)response.StatusCode == expectedStatus;
        }
        catch (Exception ex)
        {
            if (ex is HttpRequestException || ex is OperationCanceledException || ex is WebException ||
                ex is InvalidOperationException) return false;
            throw;
        }
    }
}
