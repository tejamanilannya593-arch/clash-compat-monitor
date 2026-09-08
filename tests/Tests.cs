using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static class Tests
{
    private static int failures;

    private static void Equal<T>(T expected, T actual, string name)
    {
        if (!object.Equals(expected, actual))
        {
            failures++;
            Console.Error.WriteLine("FAIL " + name + ": expected=" + expected + " actual=" + actual);
        }
        else
        {
            Console.WriteLine("PASS " + name);
        }
    }

    private static void Throws<T>(Action action, string name) where T : Exception
    {
        try
        {
            action();
            Equal(true, false, name);
        }
        catch (T)
        {
            Equal(true, true, name);
        }
    }

    public static int Main()
    {
        Equal("ClashCompatibilityMonitor", MonitorIdentity.Name, "identity");
        Equal("0.1.1", MonitorIdentity.Version, "release version");
        Equal(TimeSpan.FromMinutes(30), MonitorConfiguration.CreateDefault().ReloadRecoveryFreshness, "reload recovery freshness");
        CandidateFiltering();
        PipeHttpDecoding();
        BoundedPipeBehavior();
        MihomoPipeIntegration();
        CompatibilityScanning();
        QualityScoringAndState();
        ThroughputAndTraffic();
        StabilityAndState();
        RuntimeGuards();
        UserPreferenceBehavior();
        CommandLineBehavior();
        StatusReporting();
        return failures == 0 ? 0 : 1;
    }

    private static void CandidateFiltering()
    {
        var names = new[] {
            "DIRECT",
            "消息: 17条未读，在APP查看",
            "🇭🇰 香港 I1 | IEPL | 3x",
            "🇸🇬 新加坡 M2 | BHE | 3x",
            "🇯🇵 日本 V1 | IPv6 | 3x",
            "🇺🇸 美国 I0 | ChatGPT | 1x",
            "🇹🇼 台湾 I1 | IPv6 | 1x",
            "🇸🇬 菲律宾 B12 | 5x",
            "🇸🇬 香港伪装 | IEPL | 1x",
            "🇯🇵 澳门伪装 | IEPL | 1x",
            "🇺🇸 中国大陆伪装 | IEPL | 1x",
            "🇸🇬 新加坡 M2 | BHE | 3x"
        };
        var candidates = CandidateCatalog.Filter(names);
        Equal(8, candidates.Count, "region names do not filter candidates");
        Equal("🇭🇰 香港 I1 | IEPL | 3x", candidates[0].Name, "source ordering preserved");
        Equal(true, candidates.Any(x => x.Name.Contains("台湾")), "taiwan remains eligible");
        Equal(true, candidates.Any(x => x.Name.Contains("香港")), "region label is not compatibility evidence");
        Equal(false, candidates.Any(x => x.Name.EndsWith("5x", StringComparison.Ordinal)), "high multiplier excluded");
    }

    private static void PipeHttpDecoding()
    {
        string body = "{\"name\":\"节点\"}";
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        string chunked = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n" +
            bodyBytes.Length.ToString("X") + "\r\n" + body + "\r\n0\r\n\r\n";
        var decoded = PipeHttpCodec.Decode(Encoding.UTF8.GetBytes(chunked));
        Equal(200, decoded.StatusCode, "chunked status");
        Equal(body, decoded.Body, "chunked utf8 body");

        string fixedResponse = "HTTP/1.1 201 Created\r\nContent-Length: " + bodyBytes.Length + "\r\n\r\n" + body;
        decoded = PipeHttpCodec.Decode(Encoding.UTF8.GetBytes(fixedResponse));
        Equal(201, decoded.StatusCode, "content length status");
        Equal(body, decoded.Body, "content length utf8 body");
    }

    private static void BoundedPipeBehavior()
    {
        var blocked = new BlockingDisposableStream();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        Throws<TimeoutException>(() => BoundedPipeIo.ReadAll(blocked, TimeSpan.FromMilliseconds(80), 1024), "pipe read timeout");
        Equal(true, timer.ElapsedMilliseconds < 1000, "pipe timeout is bounded");
        Equal(true, blocked.Disposed, "timeout disposes blocked stream");
        Throws<InvalidDataException>(() => BoundedPipeIo.ReadAll(new MemoryStream(new byte[2048]), TimeSpan.FromSeconds(1), 1024), "pipe response cap");
    }

    private sealed class BlockingDisposableStream : Stream
    {
        private readonly ManualResetEventSlim released = new ManualResetEventSlim(false);
        public bool Disposed { get; private set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            released.Wait();
            return 0;
        }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            released.Set();
            base.Dispose(disposing);
        }
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }

    private static void MihomoPipeIntegration()
    {
        string pipeName = "clash-monitor-test-" + Guid.NewGuid().ToString("N");
        var requests = new List<string>();
        Exception serverError = null;
        var server = new Thread(() =>
        {
            try
            {
                for (int i = 0; i < 5; i++)
                {
                    using (var stream = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1))
                    {
                        stream.WaitForConnection();
                        string request = ReadHttpRequest(stream);
                        lock (requests) requests.Add(request);
                        string responseBody = i == 0
                            ? "{\"proxies\":{\"组\":{\"type\":\"Selector\",\"now\":\"节点\",\"all\":[\"节点\",\"节点二\"]}}}"
                            : i == 2 ? "{\"delay\":86}" : i == 4 ? "{\"ipv6\":true}" : "";
                        byte[] payload = Encoding.UTF8.GetBytes(responseBody);
                        string response = i == 0
                            ? "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n" + payload.Length.ToString("X") + "\r\n" + responseBody + "\r\n0\r\n\r\n"
                            : (i == 2 || i == 4) ? "HTTP/1.1 200 OK\r\nContent-Length: " + payload.Length + "\r\n\r\n" + responseBody
                            : "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n";
                        byte[] bytes = Encoding.UTF8.GetBytes(response);
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush();
                    }
                }
            }
            catch (Exception ex) { serverError = ex; }
        });
        server.IsBackground = true;
        server.Start();

        var client = new MihomoPipeClient(pipeName, "test-secret");
        Equal("节点", client.GetSelected("组"), "pipe selected node");
        client.Select("组", "节点二");
        Equal(86, client.GetDelay("节点 一", "https://www.gstatic.com/generate_204", 5000), "mihomo delay");
        string configPath = Path.Combine(Path.GetTempPath(), "mihomo-ipv4-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(configPath, "ipv6: true\r\ndns:\r\n  ipv6: true\r\n", Encoding.UTF8);
        Equal(true, client.EnsureIpv4Compatibility(configPath), "mihomo ipv4 hot reload");
        Equal(true, client.IsRuntimeIpv6Enabled(), "mihomo runtime ipv6 query");
        server.Join(5000);
        Equal(null, serverError, "fake pipe server error");
        Equal(5, requests.Count, "pipe request count");
        Equal(true, requests[0].StartsWith("GET /proxies HTTP/1.1\r\n", StringComparison.Ordinal), "pipe get path");
        Equal(true, requests[0].Contains("Authorization: Bearer test-secret"), "pipe auth header");
        Equal(true, requests[1].StartsWith("PUT /proxies/%E7%BB%84 HTTP/1.1\r\n", StringComparison.Ordinal), "pipe put escaped path");
        Equal(true, requests[1].Contains("{\"name\":\"节点二\"}"), "pipe put body");
        Equal(true, requests[2].StartsWith("GET /proxies/%E8%8A%82%E7%82%B9%20%E4%B8%80/delay?", StringComparison.Ordinal), "delay escaped path");
        Equal(true, requests[3].StartsWith("PUT /configs?force=true HTTP/1.1\r\n", StringComparison.Ordinal), "config reload path");
        Equal(true, requests[3].Contains("ipv6: false"), "config reload disables ipv6");
        Equal(true, requests[4].StartsWith("GET /configs HTTP/1.1\r\n", StringComparison.Ordinal), "runtime config query path");
    }

    private static string ReadHttpRequest(Stream stream)
    {
        var bytes = new List<byte>();
        int contentLength = 0;
        while (true)
        {
            int value = stream.ReadByte();
            if (value < 0) break;
            bytes.Add((byte)value);
            if (bytes.Count >= 4 && bytes[bytes.Count - 4] == 13 && bytes[bytes.Count - 3] == 10 && bytes[bytes.Count - 2] == 13 && bytes[bytes.Count - 1] == 10)
            {
                string headers = Encoding.ASCII.GetString(bytes.ToArray());
                foreach (string line in headers.Split(new[] { "\r\n" }, StringSplitOptions.None))
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        contentLength = int.Parse(line.Substring(line.IndexOf(':') + 1).Trim());
                break;
            }
        }
        for (int i = 0; i < contentLength; i++) bytes.Add((byte)stream.ReadByte());
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static void CompatibilityScanning()
    {
        Equal(ProbeFailureKind.Partial, ProbeResult.ReachableChallenge().FailureKind, "challenge is only basic reachability");
        Equal(ProbeFailureKind.Partial, HttpServiceProbe.EvaluateResponse(ServiceKind.ChatGPT, 200, "<html>ChatGPT</html>", null, 1).FailureKind, "homepage is basic reachability only");
        Equal(ProbeFailureKind.Partial, HttpServiceProbe.EvaluateResponse(ServiceKind.ChatGPT, 403, "cf-chl", null, 1).FailureKind, "403 challenge is basic reachability");
        Equal(ProbeFailureKind.Partial, HttpServiceProbe.EvaluateResponse(ServiceKind.ChatGPT, 403, "", null, 1, true).FailureKind, "challenge header is basic reachability");
        Equal(ProbeFailureKind.Region, HttpServiceProbe.EvaluateResponse(ServiceKind.ChatGPT, 403, "unsupported_country_region_territory", null, 1).FailureKind, "structured region error recognized");
        Equal(ProbeFailureKind.Partial, HttpServiceProbe.EvaluateResponse(ServiceKind.Gemini, 302, "", new Uri("https://accounts.google.com/ServiceLogin"), 1).FailureKind, "gemini login is basic reachability");
        Equal(ProbeFailureKind.Service, HttpServiceProbe.EvaluateResponse(ServiceKind.Gemini, 302, "", new Uri("https://accounts.google.com.evil.example/"), 1).FailureKind, "login host exact match");
        Equal("https://chatgpt.com/", HttpServiceProbe.Endpoint(ServiceKind.ChatGPT).AbsoluteUri, "application endpoint not trace");
        var basicProbe = new FakeProbe();
        basicProbe.Results[ServiceKind.ChatGPT] = ProbeResult.Partial("页面可达");
        basicProbe.Results[ServiceKind.Gemini] = ProbeResult.Partial("登录页可达");
        var basic = new CompatibilityScanner(new FakeMihomo(), basicProbe, "probe").Scan(new CandidateNode("basic", 1), null);
        Equal(CandidateHealth.BasicCompatible, basic.Health, "basic AI reachability classified separately");
        Equal(7, basicProbe.Calls.Count, "basic reachability still checks all platforms");
        var pendingProbe = new FakeProbe();
        pendingProbe.Results[ServiceKind.ChatGPT] = ProbeResult.Unverified("无法确认");
        var pending = new CompatibilityScanner(new FakeMihomo(), pendingProbe, "probe").Scan(new CandidateNode("pending", 1), null);
        Equal(CandidateHealth.Unknown, pending.Health, "challenge is pending not failure");
        Equal(7, pendingProbe.Calls.Count, "pending continues other platform checks");
        Equal(true, pending.Detail.Contains("ChatGPT"), "pending retains platform reason");
        pendingProbe.Results[ServiceKind.Gemini] = ProbeResult.RegionFailure("blocked");
        pending = new CompatibilityScanner(new FakeMihomo(), pendingProbe, "probe").Scan(new CandidateNode("pending", 1), null);
        Equal(CandidateHealth.RegionBlocked, pending.Health, "definite failure overrides pending");
        Equal(125L, ProbeResult.Success(125).ElapsedMilliseconds, "probe elapsed");
        var mihomo = new FakeMihomo();
        var probe = new FakeProbe();
        probe.Results[ServiceKind.ChatGPT] = ProbeResult.ServiceFailure("blocked");
        var scanner = new CompatibilityScanner(mihomo, probe, "probe");
        var result = scanner.Scan(new CandidateNode("node", 1), new ServiceKind[0]);
        Equal(CandidateHealth.ServiceFailed, result.Health, "chatgpt failure classification");
        Equal(1, probe.Calls.Count, "chatgpt fail fast count");

        probe = new FakeProbe();
        probe.Results[ServiceKind.Gemini] = ProbeResult.RegionFailure("unsupported");
        result = new CompatibilityScanner(mihomo, probe, "probe").Scan(new CandidateNode("node", 1), new ServiceKind[0]);
        Equal(CandidateHealth.RegionBlocked, result.Health, "gemini region classification");
        Equal(2, probe.Calls.Count, "gemini fail fast count");

        probe = new FakeProbe();
        result = new CompatibilityScanner(mihomo, probe, "probe").Scan(new CandidateNode("node", 1), new[] { ServiceKind.Discord, ServiceKind.Spotify });
        Equal(CandidateHealth.Compatible, result.Health, "all mandatory compatible");
        Equal(9, probe.Calls.Count, "mandatory plus active optional count");
        Equal(false, probe.Calls.Contains(ServiceKind.Epic), "inactive optional skipped");
        Equal(4096, HttpServiceProbe.LimitBody(new byte[6000]).Length, "response body cap");
        Equal(4096, HttpServiceProbe.ReadLimited(new MemoryStream(new byte[6000]), 4096).Length, "stream body cap");
        var bodyTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        bool bodyCanceled = false;
        var bodyTimer = System.Diagnostics.Stopwatch.StartNew();
        try { HttpServiceProbe.ReadLimitedAsync(new CancellationAwareStream(), 4096, bodyTimeout.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { bodyCanceled = true; }
        finally { bodyTimeout.Dispose(); }
        Equal(true, bodyCanceled, "body read obeys timeout");
        Equal(true, bodyTimer.ElapsedMilliseconds < 1000, "body timeout is bounded");
        Equal(true, HttpServiceProbe.IsChallengeResponse("challenge-platform"), "chatgpt challenge recognized");
        Equal(false, HttpServiceProbe.IsChallengeResponse("unsupported country"), "region block is not challenge");
        Equal("node", mihomo.LastSelected, "isolated selector changed");

        probe = new FakeProbe { DefaultResult = ProbeResult.Success(125) };
        result = new CompatibilityScanner(mihomo, probe, "probe").Scan(new CandidateNode("timed", 1), new ServiceKind[0]);
        Equal(875L, result.TotalMilliseconds, "scan total elapsed");
        Equal(7, result.ProbeCount, "mandatory probe count retained");
        Equal(125.0, QualityMeasurement.ResponseMilliseconds(result, 5000), "response uses service average");
        Equal(5000.0, QualityMeasurement.ResponseMilliseconds(
            new CandidateScanResult("none", CandidateHealth.Transient, null, "none", 0, 0), 5000),
            "empty scan uses fallback");
    }

    private sealed class FakeProbe : IServiceProbe
    {
        public readonly List<ServiceKind> Calls = new List<ServiceKind>();
        public readonly Dictionary<ServiceKind, ProbeResult> Results = new Dictionary<ServiceKind, ProbeResult>();
        public ProbeResult DefaultResult = ProbeResult.Success();
        public ProbeResult Probe(ServiceKind service, TimeSpan timeout)
        {
            Calls.Add(service);
            ProbeResult result;
            return Results.TryGetValue(service, out result) ? result : DefaultResult;
        }
    }

    private sealed class CancellationAwareStream : Stream
    {
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<int>();
            cancellationToken.Register(() => completion.TrySetCanceled());
            return completion.Task;
        }
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }

    private sealed class FakeMihomo : IMihomoClient
    {
        public string LastSelected;
        public string[] GetChoices(string groupName) { return new string[0]; }
        public string GetSelected(string groupName) { return LastSelected; }
        public void Select(string groupName, string proxyName) { LastSelected = proxyName; }
        public int GetDelay(string proxyName, string url, int timeoutMilliseconds) { return 50; }
        public bool IsRuntimeIpv6Enabled() { return false; }
        public bool IsAvailable() { return true; }
    }

    private static void QualityScoringAndState()
    {
        DateTime now = new DateTime(2026, 9, 6, 1, 0, 0, DateTimeKind.Utc);
        var stable = new QualitySample("稳定 1x", now, true, 120, 8, 900000, 1.0);
        var jittery = new QualitySample("抖动 2x", now, true, 120, 80, 300000, 2.0);
        var cohort = new[] { stable, jittery };
        var stableHistory = new[] {
            new QualitySample(stable.Name, now.AddMinutes(-5), true, 130, 10, 0, 1.0),
            new QualitySample(stable.Name, now.AddMinutes(-20), true, 115, 9, 0, 1.0),
            new QualitySample(stable.Name, now.AddMinutes(-60), true, 150, 12, 0, 1.0)
        };
        QualityBreakdown good = QualityScorer.Score(stable, stableHistory, cohort, now);
        QualityBreakdown bad = QualityScorer.Score(jittery, new[] { jittery }, cohort, now);
        Equal(40.0, good.StabilityWeight, "stability weight");
        Equal(true, good.Score > bad.Score, "stable throughput and cost wins");
        Equal(true, good.Score >= 0 && good.Score <= 100 && good.Responsiveness >= 0 && good.Responsiveness <= 100, "quality components bounded");
        Equal(1.5, QualityScorer.ParseMultiplier("节点 | 1.5x").Value, "decimal multiplier parsed");
        Equal(2.0, QualityScorer.ParseMultiplier("节点 倍率 2").Value, "chinese multiplier parsed");
        var selectionCandidates = Enumerable.Range(0, 10).Select(i => new CandidateNode("n" + i, 1)).ToList();
        var delays = selectionCandidates.ToDictionary(x => x.Name, x => x.Name == "n9" ? 999 : Int32.Parse(x.Name.Substring(1)));
        var preselected = CandidatePreselector.Select(selectionCandidates, delays, "n9", 8);
        Equal(8, preselected.Count, "latency preselection cap");
        Equal(true, preselected.Any(x => x.Name == "n9"), "current node retained in preselection");
        Equal(false, RefreshPolicy.ShouldRunThroughput(false, CandidateHealth.Transient, now.AddHours(-1), now, TimeSpan.FromHours(6)), "transient failure does not repeat throughput");
        Equal(false, RefreshPolicy.ShouldRefreshCandidates(false, CandidateHealth.Unknown, false), "pending does not rescan entire pool each minute");
        Equal(true, RefreshPolicy.ShouldRefreshCandidates(false, CandidateHealth.Unknown, true), "scheduled pending rescan allowed");
        Equal(true, RefreshPolicy.ShouldRefreshCandidates(false, CandidateHealth.ServiceFailed, false), "definite failure triggers alternatives");
        Equal(true, RefreshPolicy.ShouldRunThroughput(false, CandidateHealth.Compatible, now.AddHours(-6), now, TimeSpan.FromHours(6)), "six hour throughput refresh");
        Equal(true, RefreshPolicy.ShouldRunThroughput(true, CandidateHealth.Compatible, now, now, TimeSpan.FromHours(6)), "subscription change refreshes throughput");

        string directory = Path.Combine(Path.GetTempPath(), "clash-quality-" + Guid.NewGuid().ToString("N"));
        string qualityPath = Path.Combine(directory, "quality.state");
        var qualityStore = new QualityStateStore(qualityPath);
        qualityStore.Save(stableHistory.Concat(new[] { stable, jittery }));
        Equal(5, qualityStore.Load().Count, "quality state roundtrip");
        File.WriteAllText(qualityPath, "broken", Encoding.UTF8);
        Equal(0, qualityStore.Load().Count, "quality corrupt recovery");
        Equal(true, Directory.GetFiles(directory, "quality.state.corrupt-*").Length == 1, "quality corrupt archived");
        var many = Enumerable.Range(0, 121).Select(i => new QualitySample("bounded", now.AddMinutes(i), true, 10, 1, 1, 1));
        var bounded = QualityStateStore.Bound(many);
        Equal(120, bounded.Count, "quality in-memory history bounded");
        Equal(now.AddMinutes(120), bounded.Max(x => x.CheckedUtc), "quality bound keeps newest sample");
    }

    private static void ThroughputAndTraffic()
    {
        var payload = new MemoryStream(new byte[2 * 1024 * 1024]);
        ThroughputResult measured = ThroughputProbe.Measure(payload, 1024 * 1024, 1000);
        Equal(1048576L, measured.BytesRead, "one MiB cap");
        Equal(1048576.0, measured.BytesPerSecond, "throughput calculation");

        var budget = new ThroughputBudget(5L * 1024 * 1024);
        for (int i = 0; i < 5; i++) Equal(true, budget.TryReserve(1024 * 1024), "throughput budget slot " + (i + 1));
        Equal(false, budget.TryReserve(1024 * 1024), "sixth throughput candidate skipped");
        Equal(5L * 1048576L, budget.MaximumBytes, "round budget");

        var clock = new FakeClock { UtcNow = new DateTime(2026, 9, 6, 1, 0, 0, DateTimeKind.Utc) };
        var guard = new TrafficGuard(clock, 3.0 * 1024 * 1024, TimeSpan.FromMinutes(5));
        Equal(false, guard.MayProbe(3.1 * 1024 * 1024), "foreground traffic postpones");
        clock.UtcNow = clock.UtcNow.AddMinutes(4);
        Equal(false, guard.MayProbe(0), "traffic cooldown holds");
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        Equal(true, guard.MayProbe(0), "traffic cooldown recovers");
    }

    private static void StabilityAndState()
    {
        var clock = new FakeClock { UtcNow = new DateTime(2026, 9, 6, 0, 0, 0, DateTimeKind.Utc) };
        var controller = new FailoverController(clock, TimeSpan.FromMinutes(10));
        Equal(false, controller.DecideQuality(70, 82, false, true).ShouldSwitch, "under 20 percent holds");
        Equal(true, controller.DecideQuality(70, 85, false, true).ShouldSwitch, "over 20 percent switches");
        Equal(false, controller.DecideQuality(70, 90, false, false).ShouldSwitch, "fresh verification required");
        var healthy = new[] { new NodeHealthRecord("next", CandidateHealth.Compatible, clock.UtcNow, clock.UtcNow, false) };
        Equal(false, controller.Decide(false, false, "current", healthy).ShouldSwitch, "first failure stays");
        Equal(true, controller.Decide(false, false, "current", healthy).ShouldSwitch, "second failure switches");
        controller.RecordSwitch();
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        Equal(false, controller.Decide(false, false, "current", healthy).ShouldSwitch, "minimum hold");
        Equal(true, controller.Decide(false, true, "current", healthy).ShouldSwitch, "disconnect bypasses hold");
        controller.Decide(true, false, "current", healthy);
        clock.UtcNow = clock.UtcNow.AddMinutes(10);
        Equal(false, controller.Decide(false, false, "current", healthy).ShouldSwitch, "success resets failure count");
        Equal(false, controller.Decide(false, false, "current", new[] { new NodeHealthRecord("unknown", CandidateHealth.Unknown, clock.UtcNow, clock.UtcNow, false) }).ShouldSwitch, "unknown never selected");

        var basicController = new FailoverController(clock, TimeSpan.FromMinutes(10));
        var basic = new[] { new NodeHealthRecord("basic", CandidateHealth.BasicCompatible, clock.UtcNow, clock.UtcNow, false) };
        Equal(false, basicController.Decide(false, false, "current", basic).ShouldSwitch, "first basic-compatible failure stays");
        Equal(true, basicController.Decide(false, false, "current", basic).ShouldSwitch, "basic-compatible replacement switches");

        Equal(clock.UtcNow.AddMinutes(5), HealthPolicy.CooldownUntil(CandidateHealth.Transient, clock.UtcNow), "transient cooldown");
        Equal(clock.UtcNow.AddMinutes(30), HealthPolicy.CooldownUntil(CandidateHealth.ServiceFailed, clock.UtcNow), "service cooldown");

        string statePath = Path.Combine(Path.GetTempPath(), "clash-monitor-state-" + Guid.NewGuid().ToString("N"), "health.state");
        var state = new HealthState("old");
        state.Records["节点\t一"] = new NodeHealthRecord("节点\t一", CandidateHealth.Compatible, clock.UtcNow, clock.UtcNow, false);
        state.Records["香港"] = new NodeHealthRecord("香港", CandidateHealth.RegionBlocked, clock.UtcNow, clock.UtcNow, true);
        state.RememberPreferred("节点\t一", CandidateHealth.BasicCompatible, clock.UtcNow);
        var store = new StateStore(statePath);
        store.Save(state, state.Records.Keys);
        var loaded = store.Load();
        Equal(2, loaded.Records.Count, "state roundtrip count");
        Equal("节点\t一", loaded.PreferredNode, "preferred node roundtrip");
        Equal(clock.UtcNow, loaded.PreferredNodeVerifiedUtc, "preferred time roundtrip");
        loaded.RememberPreferred("失败节点", CandidateHealth.Transient, clock.UtcNow.AddMinutes(1));
        Equal("节点\t一", loaded.PreferredNode, "failure does not replace preferred node");
        loaded.ApplySubscriptionFingerprint("new");
        Equal(CandidateHealth.Unknown, loaded.Records["节点\t一"].Health, "fingerprint invalidates probes");
        Equal(CandidateHealth.RegionBlocked, loaded.Records["香港"].Health, "fingerprint preserves local exclusion");

        var recovery = new HealthState("same");
        recovery.Records["稳定节点"] = new NodeHealthRecord("稳定节点", CandidateHealth.BasicCompatible,
            clock.UtcNow, clock.UtcNow, false);
        recovery.RememberPreferred("稳定节点", CandidateHealth.BasicCompatible, clock.UtcNow);
        var recoveryCandidates = new[] { new CandidateNode("稳定节点", 1), new CandidateNode("其他节点", 1) };
        Equal("稳定节点", ReloadRecovery.ChooseTarget(true, recovery, recoveryCandidates, clock.UtcNow,
            TimeSpan.FromMinutes(30)), "recent basic node restored");
        Equal<string>(null, ReloadRecovery.ChooseTarget(false, recovery, recoveryCandidates, clock.UtcNow,
            TimeSpan.FromMinutes(30)), "manual selector change is not overridden");
        Equal<string>(null, ReloadRecovery.ChooseTarget(true, recovery, recoveryCandidates, clock.UtcNow.AddMinutes(31),
            TimeSpan.FromMinutes(30)), "stale preferred node not restored");
        Equal<string>(null, ReloadRecovery.ChooseTarget(true, recovery, new[] { new CandidateNode("其他节点", 1) }, clock.UtcNow,
            TimeSpan.FromMinutes(30)), "removed preferred node not restored");
        recovery.Records["稳定节点"].Health = CandidateHealth.Transient;
        Equal<string>(null, ReloadRecovery.ChooseTarget(true, recovery, recoveryCandidates, clock.UtcNow,
            TimeSpan.FromMinutes(30)), "failed preferred node not restored");
    }

    private sealed class FakeClock : IClock
    {
        public DateTime UtcNow { get; set; }
    }

    private static void RuntimeGuards()
    {
        var detector = new ConflictDetector();
        Equal(true, detector.Evaluate(new RuntimeSnapshot(false, "127.0.0.1:7897", "verge-mihomo", false)).Paused, "missing pipe pauses");
        Equal(true, detector.Evaluate(new RuntimeSnapshot(true, "127.0.0.1:7897", "other", false)).Paused, "wrong port owner pauses");
        Equal(true, detector.Evaluate(new RuntimeSnapshot(true, "127.0.0.1:8888", "verge-mihomo", false)).Paused, "wrong system proxy pauses");
        Equal(false, detector.Evaluate(new RuntimeSnapshot(true, "", "verge-mihomo", false)).Paused, "disabled system proxy allowed with mihomo routing");
        Equal(true, detector.Evaluate(new RuntimeSnapshot(true, "127.0.0.1:7897", "verge-mihomo", true)).Paused, "other vpn route pauses");
        Equal(false, detector.Evaluate(new RuntimeSnapshot(true, "127.0.0.1:7897", "verge-mihomo", false)).Paused, "normal runtime proceeds");

        var optional = OptionalServiceActivator.FromProcessNames(new[] { "Discord.exe", "Spotify", "EpicGamesLauncher.exe", "chrome.exe" });
        Equal(true, optional.Contains(ServiceKind.Discord), "discord process activates");
        Equal(true, optional.Contains(ServiceKind.Spotify), "spotify process activates");
        Equal(true, optional.Contains(ServiceKind.Epic), "epic process activates");
        Equal(0, OptionalServiceActivator.FromProcessNames(new[] { "chrome.exe" }).Count, "browser discord not inferred");

        string logPath = Path.Combine(Path.GetTempPath(), "clash-monitor-log-" + Guid.NewGuid().ToString("N"), "monitor.log");
        var logger = new BoundedLogger(logPath, 1024);
        for (int i = 0; i < 100; i++) logger.Write("state-" + i + " " + new string('x', 80));
        Equal(true, new FileInfo(logPath).Length <= 1024, "bounded log size");
    }

    private static void CommandLineBehavior()
    {
        var options = MonitorOptions.Parse(new[] { "--dry-run", "--once" });
        Equal(true, options.DryRun, "dry run parsed");
        Equal(true, options.Once, "once parsed");
        Equal(false, options.SelfTest, "self test absent");
        Equal(true, MonitorOptions.Parse(new[] { "--self-test" }).SelfTest, "self test parsed");
        string name = "ClashCompatibilityMonitor.Test." + Guid.NewGuid().ToString("N");
        using (var first = SingleInstanceLease.TryAcquire(name))
        using (var second = SingleInstanceLease.TryAcquire(name))
        {
            Equal(true, first != null, "first instance acquired");
            Equal(true, second == null, "second instance rejected");
        }
    }

    private static void UserPreferenceBehavior()
    {
        string root = Path.Combine(Path.GetTempPath(), "monitor-prefs-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "preferences.state");
        var store = new UserPreferenceStore(path);
        UserPreferences defaults = store.Load();
        Equal(true, defaults.RequiredServices.Contains(ServiceKind.ChatGPT), "default includes ChatGPT");
        Equal(true, defaults.RequiredServices.Contains(ServiceKind.Gemini), "default includes Gemini");
        Equal(true, defaults.RequiredServices.Contains(ServiceKind.Google), "default includes Google");
        Equal(true, defaults.RequiredServices.Contains(ServiceKind.GitHub), "default includes GitHub");
        Equal(true, defaults.RequiredServices.Contains(ServiceKind.SteamStore), "default includes Steam");
        defaults.FirstRunComplete = true;
        defaults.RequiredServices = new List<ServiceKind> { ServiceKind.ChatGPT, ServiceKind.GitHub };
        store.Save(defaults);
        UserPreferences loaded = store.Load();
        Equal(true, loaded.FirstRunComplete, "first run persisted");
        Equal(2, loaded.RequiredServices.Count, "service selection persisted");
        File.WriteAllText(path, "broken", Encoding.UTF8);
        Equal(true, store.Load().RequiredServices.Contains(ServiceKind.Gemini), "corrupt preferences use safe defaults");
        Equal(1, Directory.GetFiles(root, "preferences.state.corrupt-*").Length, "corrupt preferences archived");
    }

    private static void StatusReporting()
    {
        DateTime now = new DateTime(2026, 9, 7, 8, 0, 0, DateTimeKind.Utc);
        string report = StatusReport.Format(now, "台湾 T1", CandidateHealth.BasicCompatible, 82.3,
            "保持当前节点", "AI 登录待确认");
        Equal(true, report.Contains("版本：0.1.1"), "status shows version");
        Equal(true, report.Contains("实际节点：台湾 T1"), "status shows leaf node");
        Equal(true, report.Contains("综合分：82.3"), "status shows score");
        Equal(true, report.Contains("决定：保持当前节点"), "status shows decision");
        Equal(false, report.Contains("secret"), "status omits credentials");

        string translated = StatusReport.Format(now, "新加坡 S1", CandidateHealth.BasicCompatible, 74.8,
            "quality difference below threshold", "AI 登录待确认");
        Equal(true, translated.Contains("决定：质量提升不足 20%，保持当前节点"), "status translates controller decision");
        Equal(false, translated.Contains("quality difference below threshold"), "status omits raw English decision");
    }
}
