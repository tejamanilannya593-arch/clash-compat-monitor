using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

public interface IServiceProbe
{
    ProbeResult Probe(ServiceKind service, TimeSpan timeout);
}

public sealed class CompatibilityScanner
{
    private static readonly ServiceKind[] Mandatory = {
        ServiceKind.ChatGPT, ServiceKind.Gemini, ServiceKind.Google, ServiceKind.GitHub,
        ServiceKind.SteamStore, ServiceKind.SteamCommunity, ServiceKind.SteamApi
    };
    private readonly IMihomoClient mihomo;
    private readonly IServiceProbe probe;
    private readonly string probeGroup;
    public Func<bool> ShouldStop { get; set; }

    public CompatibilityScanner(IMihomoClient mihomo, IServiceProbe probe, string probeGroup)
    {
        this.mihomo = mihomo;
        this.probe = probe;
        this.probeGroup = probeGroup;
    }

    public CandidateScanResult Scan(CandidateNode candidate, IEnumerable<ServiceKind> optionalServices)
    {
        return ScanSelected(candidate, Mandatory.Concat(optionalServices ?? new ServiceKind[0]));
    }

    public CandidateScanResult ScanSelected(CandidateNode candidate, IEnumerable<ServiceKind> requiredServices)
    {
        if (ShouldStop != null && ShouldStop()) throw new OperationCanceledException("检测已暂停或达到本轮时间预算");
        long totalMilliseconds = 0;
        int probeCount = 0;
        var pending = new List<string>();
        var partial = new List<string>();
        var measurements = new Dictionary<ServiceKind, ProbeResult>();
        mihomo.Select(probeGroup, candidate.Name);
        foreach (ServiceKind service in (requiredServices ?? new ServiceKind[0]).Distinct())
        {
            if (ShouldStop != null && ShouldStop()) throw new OperationCanceledException("检测已暂停或达到本轮时间预算");
            ProbeResult result = probe.Probe(service, TimeSpan.FromSeconds(5));
            measurements[service] = result;
            probeCount++;
            totalMilliseconds += result.ElapsedMilliseconds;
            if (result.FailureKind == ProbeFailureKind.Partial) partial.Add(service + ": " + result.Detail);
            else if (result.FailureKind == ProbeFailureKind.Unverified) pending.Add(service + ": " + result.Detail);
            else if (!result.Passed)
            {
                foreach (ServiceKind untested in (requiredServices ?? new ServiceKind[0]).Distinct())
                    if (!measurements.ContainsKey(untested)) measurements[untested] = ProbeResult.Unverified("前序服务检测失败，本轮尚未检测");
                return Failure(candidate.Name, service, result, totalMilliseconds, probeCount, measurements);
            }
        }
        if (pending.Count > 0) return new CandidateScanResult(candidate.Name, CandidateHealth.Unknown, null, String.Join("; ", pending), totalMilliseconds, probeCount, measurements);
        if (partial.Count > 0) return new CandidateScanResult(candidate.Name, CandidateHealth.BasicCompatible, null, String.Join("; ", partial), totalMilliseconds, probeCount, measurements);
        return new CandidateScanResult(candidate.Name, CandidateHealth.Compatible, null, "ok", totalMilliseconds, probeCount, measurements);
    }

    private static CandidateScanResult Failure(string name, ServiceKind service, ProbeResult result,
        long totalMilliseconds, int probeCount, IDictionary<ServiceKind, ProbeResult> measurements)
    {
        CandidateHealth health = result.FailureKind == ProbeFailureKind.Region ? CandidateHealth.RegionBlocked :
            result.FailureKind == ProbeFailureKind.Transient ? CandidateHealth.Transient : CandidateHealth.ServiceFailed;
        return new CandidateScanResult(name, health, service, result.Detail, totalMilliseconds, probeCount, measurements);
    }
}

public sealed class HttpServiceProbe : IServiceProbe, IDisposable
{
    private readonly HttpClient client;

    public HttpServiceProbe(string proxyUrl)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var handler = new HttpClientHandler {
            Proxy = new WebProxy(proxyUrl), UseProxy = true, UseCookies = false, AllowAutoRedirect = false
        };
        client = new HttpClient(handler);
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClashCompatibilityMonitor/1.0");
    }

    public ProbeResult Probe(ServiceKind service, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            using (var cancellation = new CancellationTokenSource(timeout))
            using (var request = new HttpRequestMessage(HttpMethod.Get, Endpoint(service)))
            using (HttpResponseMessage response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token).GetAwaiter().GetResult())
            {
                byte[] bytes;
                if (response.Content == null) bytes = new byte[0];
                else using (Stream bodyStream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                    bytes = ReadLimitedAsync(bodyStream, 4096, cancellation.Token).GetAwaiter().GetResult();
                string body = System.Text.Encoding.UTF8.GetString(bytes);
                bool challengeHeader = response.Headers.Contains("Cf-Mitigated") &&
                    String.Join(",", response.Headers.GetValues("Cf-Mitigated")).IndexOf("challenge", StringComparison.OrdinalIgnoreCase) >= 0;
                return EvaluateResponse(service, (int)response.StatusCode, body, response.Headers.Location, timer.ElapsedMilliseconds, challengeHeader);
            }
        }
        catch (TaskCanceledException) { return ProbeResult.TransientFailure("timeout", timer.ElapsedMilliseconds); }
        catch (OperationCanceledException) { return ProbeResult.TransientFailure("timeout", timer.ElapsedMilliseconds); }
        catch (HttpRequestException) { return ProbeResult.TransientFailure("network failure", timer.ElapsedMilliseconds); }
        catch (IOException) { return ProbeResult.TransientFailure("I/O failure", timer.ElapsedMilliseconds); }
    }

    public static ProbeResult EvaluateResponse(ServiceKind service, int status, string body, Uri location, long elapsed, bool challengeHeader = false)
    {
                if (IsRegionBlocked(body)) return ProbeResult.RegionFailure("explicit unsupported-region response", elapsed);
                switch (service)
                {
                    case ServiceKind.Google: return status == 204 ? ProbeResult.Success(elapsed) : ProbeResult.ServiceFailure("unexpected HTTP " + status, elapsed);
                    case ServiceKind.GitHub: return status == 200 ? ProbeResult.Success(elapsed) : ProbeResult.ServiceFailure("unexpected HTTP " + status, elapsed);
                    case ServiceKind.ChatGPT:
                        if (status == 200) return ProbeResult.Partial("页面可达，未验证登录及对话功能", elapsed);
                        if (status == 403 && challengeHeader) return ProbeResult.Partial("Cloudflare 验证页可达，未验证登录及对话功能", elapsed);
                        if (status == 403 && IsChallengeResponse(body)) return ProbeResult.ReachableChallenge(elapsed);
                        return ProbeResult.ServiceFailure("unexpected HTTP " + status, elapsed);
                    case ServiceKind.Gemini:
                        if (status == 200 || (status >= 300 && status < 400 && location != null && location.IsAbsoluteUri && location.Scheme == "https" && String.Equals(location.Host, "accounts.google.com", StringComparison.OrdinalIgnoreCase))) return ProbeResult.Partial("页面可达或需要登录，未验证生成能力", elapsed);
                        return ProbeResult.ServiceFailure("unexpected HTTP " + status, elapsed);
                    case ServiceKind.Discord:
                        return status == 200 && body.IndexOf("url", StringComparison.OrdinalIgnoreCase) >= 0 ? ProbeResult.Success(elapsed) : ProbeResult.ServiceFailure("gateway unavailable", elapsed);
                    default:
                        return status >= 200 && status < 400 ? ProbeResult.Success(elapsed) : ProbeResult.ServiceFailure("unexpected HTTP " + status, elapsed);
                }
    }

    public static byte[] LimitBody(byte[] bytes)
    {
        if (bytes == null || bytes.Length <= 4096) return bytes ?? new byte[0];
        var limited = new byte[4096];
        Buffer.BlockCopy(bytes, 0, limited, 0, limited.Length);
        return limited;
    }

    public static byte[] ReadLimited(Stream stream, int maximumBytes)
    {
        if (stream == null || maximumBytes <= 0) return new byte[0];
        using (var output = new MemoryStream())
        {
            var buffer = new byte[Math.Min(1024, maximumBytes)];
            while (output.Length < maximumBytes)
            {
                int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, maximumBytes - output.Length));
                if (read <= 0) break;
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
    }

    public static async Task<byte[]> ReadLimitedAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        if (stream == null || maximumBytes <= 0) return new byte[0];
        using (var output = new MemoryStream())
        {
            var buffer = new byte[Math.Min(1024, maximumBytes)];
            while (output.Length < maximumBytes)
            {
                int count = (int)Math.Min(buffer.Length, maximumBytes - output.Length);
                int read = await stream.ReadAsync(buffer, 0, count, cancellationToken).ConfigureAwait(false);
                if (read <= 0) break;
                RunStatistics.AddBodyBytes(read);
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
    }

    public static Uri Endpoint(ServiceKind service)
    {
        switch (service)
        {
            case ServiceKind.ChatGPT: return new Uri("https://chatgpt.com/");
            case ServiceKind.Gemini: return new Uri("https://gemini.google.com/app");
            case ServiceKind.Google: return new Uri("https://www.gstatic.com/generate_204");
            case ServiceKind.GitHub: return new Uri("https://github.com/favicon.ico");
            case ServiceKind.SteamStore: return new Uri("https://store.steampowered.com/favicon.ico");
            case ServiceKind.SteamCommunity: return new Uri("https://steamcommunity.com/favicon.ico");
            case ServiceKind.SteamApi: return new Uri("https://api.steampowered.com/ISteamWebAPIUtil/GetServerInfo/v1/");
            case ServiceKind.Discord: return new Uri("https://discord.com/api/v10/gateway");
            case ServiceKind.Spotify: return new Uri("https://open.spotify.com/");
            default: return new Uri("https://store.epicgames.com/");
        }
    }

    private static bool IsRegionBlocked(string body)
    {
        if (string.IsNullOrEmpty(body)) return false;
        string value = body.ToLowerInvariant();
        return value.Contains("unsupported country") || value.Contains("unsupported_country_region_territory") || value.Contains("not available in your country") || value.Contains("not available in your region");
    }

    public static bool IsChallengeResponse(string body)
    {
        if (string.IsNullOrEmpty(body)) return false;
        return body.IndexOf("challenge-platform", StringComparison.OrdinalIgnoreCase) >= 0 ||
            body.IndexOf("cf-chl", StringComparison.OrdinalIgnoreCase) >= 0 ||
            body.IndexOf("just a moment", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public void Dispose() { client.Dispose(); }
}
