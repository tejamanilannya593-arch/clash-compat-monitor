using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Tests
{
    private sealed class RecoveryClient : IMihomoClient, IRecoveryConnectivityClient
    {
        public string Current = "current";
        public string General = "current";
        public string ProbeNode;
        public string[] Choices = new[] { "current", "node-01", "node-02", "manual" };
        public readonly Dictionary<string, int> Visits = new Dictionary<string, int>();
        public readonly List<string> Writes = new List<string>();
        public readonly List<string> Delays = new List<string>();
        public int ConnectivityCalls;
        public Func<string, int> Delay = node => node == "manual" ? Int32.MaxValue : 100;
        public Func<string, string, int> WebsiteDelay;
        public Action<string> OnScan;
        public Action<string> OnChoices;
        public Func<string, RecoveryEvidence> Basic = node => RecoveryEvidence.Healthy;
        public string[] GetChoices(string group)
        { if (OnChoices != null) OnChoices(group); return Choices; }
        public string GetSelected(string group) { return group == "shared" ? Current : group == "general" ? General : ProbeNode; }
        public void Select(string group, string node)
        {
            if (group == "shared") { Current = node; Writes.Add(node); }
            else if (group == "general") General = node;
            else
            {
                ProbeNode = node;
                if (!Visits.ContainsKey(node)) Visits[node] = 0;
                Visits[node]++;
                if (OnScan != null) OnScan(node);
            }
        }
        public int GetDelay(string node, string url, int timeout)
        { lock (Delays) Delays.Add(node); return WebsiteDelay == null ? Delay(node) : WebsiteDelay(node, url); }
        public RecoveryEvidence CheckConnectivity(string node, string url, int timeout, out int milliseconds)
        { ConnectivityCalls++; milliseconds = 100; return Basic(node); }
        public bool IsRuntimeIpv6Enabled() { return false; }
        public bool IsAvailable() { return true; }
    }

    private sealed class RecoveryProbe : IServiceProbe
    {
        public RecoveryClient Client;
        public readonly List<TimeSpan> Timeouts = new List<TimeSpan>();
        public Func<string, ServiceKind, int, ProbeResult> Result = (node, service, visit) => ProbeResult.Success(100);
        public ProbeResult Probe(ServiceKind service, TimeSpan timeout)
        { lock (Timeouts) Timeouts.Add(timeout); return Result(Client.ProbeNode, service, Client.Visits[Client.ProbeNode]); }
    }

    private sealed class RecoveryExitProbe : IExitIdentityProbe
    {
        public RecoveryClient Client;
        public Func<string, string> Country = node => "JP";
        public ExitIdentity Probe(TimeSpan timeout)
        {
            string node = Client == null ? "" : Client.ProbeNode;
            return new ExitIdentity(new string('a', 64), Country(node), "test");
        }
    }

    private sealed class RecoveryFixture : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "recovery-regression-" + Guid.NewGuid().ToString("N"));
        public readonly RecoveryClient Client = new RecoveryClient();
        public readonly FakeClock Clock = new FakeClock { UtcNow = DateTime.UtcNow };
        public readonly UserPreferences Preferences = UserPreferences.Defaults();
        public readonly RecoveryProbe Probe;
        public readonly RecoveryExitProbe ExitProbe;
        private readonly bool continuousOptimization;
        public MonitorWorker Worker;
        public RecoveryFixture(bool continuousOptimization = false)
        {
            this.continuousOptimization = continuousOptimization;
            Directory.CreateDirectory(Root);
            Probe = new RecoveryProbe { Client = Client };
            ExitProbe = new RecoveryExitProbe { Client = Client };
            Restart();
        }
        public void Restart()
        {
            MonitorConfiguration configuration = FastWorkerConfiguration(Root);
            var continuous = typeof(MonitorConfiguration).GetField("ContinuousOptimization");
            if (continuous != null) continuous.SetValue(configuration, continuousOptimization);
            Worker = new MonitorWorker(configuration, Client, Probe,
                new BoundedLogger(Path.Combine(Root, "monitor.log"), 1000000), Clock, ExitProbe, null,
                () => new RuntimeSnapshot(true, "", "verge-mihomo", false));
        }
        public MonitorSnapshot Run(MonitorCycleTrigger trigger = MonitorCycleTrigger.Scheduled)
        { Clock.UtcNow = Clock.UtcNow.AddSeconds(60); return Worker.Run(Preferences, trigger); }
        public void FailCurrent()
        { Probe.Result = (node, service, visit) => node == "current" && service == ServiceKind.ChatGPT
            ? ProbeResult.ServiceFailure("failed", 100) : ProbeResult.Success(node == "node-02" ? 150 : 100); }
        public RecoveryState Saved()
        { return new ExperienceStore(Path.Combine(Root, "state", "experience.json")).Load().Assurance.Recovery; }
        public void Dispose() { DeleteDirectoryEventually(Root); }
    }

    private static void RecoveryWorkflowBehavior()
    {
        using (var f = new RecoveryFixture())
        {
            f.FailCurrent(); f.Run();
            f.Client.OnScan = node => { throw new IOException("controller temporarily unavailable"); };
            f.Run();
            f.Client.OnScan = null; f.Run();
            Equal(0, f.Client.Writes.Count, "probe exception breaks the consecutive failure sequence");
            Equal(1, f.Saved().ChatGptFailures, "new failure after probe exception starts at one");
        }
        using (var f = new RecoveryFixture())
        {
            f.FailCurrent(); f.Run();
            f.Client.OnScan = node => {
                if (node == "node-01" && f.Client.Current == node) f.Worker.ShouldStop = () => true;
            };
            Throws<OperationCanceledException>(() => f.Run(), "pause during post-switch verification cancels recovery");
            Equal("current", f.Client.Current, "cancellation rolls back without requiring another cycle");
            Equal<string>(null, f.Saved().PendingWritten, "cancellation clears completed rollback journal");
        }
        using (var f = new RecoveryFixture())
        {
            f.Probe.Result = (node, service, visit) => node == "current" &&
                service == (visit % 2 == 1 ? ServiceKind.ChatGPT : ServiceKind.Gemini)
                ? ProbeResult.ServiceFailure("alternating") : ProbeResult.Success(100);
            f.Run(); f.Run(); f.Run(); f.Run();
            Equal(0, f.Client.Writes.Count, "different failing services cannot combine into consecutive failures");
        }
        using (var f = new RecoveryFixture())
        {
            f.Probe.Result = (node, service, visit) => ProbeResult.Success(visit == 4 ? 400 : 600);
            f.Run(); f.Run(); f.Run();
            Equal(0, f.Client.Writes.Count, "latency confirmation returning to normal prevents switching");
        }
        using (var f = new RecoveryFixture())
        {
            for (int i = 0; i < 180; i++)
            {
                if (i % 60 == 0) f.Restart();
                f.Run();
            }
            Equal(0, f.Client.Writes.Count, "three simulated hours and restarts keep a healthy node");
            Equal(0, f.Client.Delays.Count, "long healthy run performs no background candidate scans");
            Equal(true, new FileInfo(Path.Combine(f.Root, "state", "quality.state")).Length <= 256 * 1024,
                "long run keeps quality history bounded");
        }
        using (var f = new RecoveryFixture())
        {
            f.FailCurrent();
            f.Client.OnChoices = group => { if (group == "general") f.Client.General = "manual"; };
            f.Run(); f.Run();
            Equal("manual", f.Client.General, "general synchronization rechecks ownership after querying choices");
        }
        using (var f = new RecoveryFixture())
        {
            f.FailCurrent(); f.Run(); f.Run();
            var field = typeof(MonitorWorker).GetField("experience", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var data = (ExperienceData)field.GetValue(f.Worker);
            data.Assurance.Recovery.PendingPreviousWrite = "manual";
            f.Probe.Result = (node, service, visit) => ProbeResult.Success(100);
            f.Client.OnScan = node => { if (node == "current" && f.Client.Current == node) f.Client.Current = "manual"; };
            Equal(false, f.Worker.RestorePrevious(), "manual restore interrupted by external selection");
            Equal("manual", f.Client.Current, "stale previous-write journal cannot override external choice");
        }
        using (var f = new RecoveryFixture())
        {
            string path = Path.Combine(f.Root, "atomic.state");
            File.WriteAllText(path, "original");
            Task write;
            using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                write = Task.Factory.StartNew(() => StatusReport.WriteAtomic(path, "replacement"));
                SpinWait.SpinUntil(() => write.IsCompleted, 60);
            }
            try { write.GetAwaiter().GetResult(); }
            catch (IOException) { }
            Equal("replacement", File.ReadAllText(path), "atomic state save survives a brief reader sharing lock");
        }
        using (var f = new RecoveryFixture())
        {
            f.Preferences.AutomaticOptimization = true;
            for (int i = 0; i < 5; i++) f.Run(MonitorCycleTrigger.Scheduled);
            Equal(0, f.Client.Writes.Count, "healthy scheduled cycles ignore the legacy automatic optimization preference");
            Equal(0, f.Client.Delays.Count, "healthy cycles never scan candidate delay rankings");
            Equal(5, f.Client.Visits["current"], "healthy cycles only scan current node");
        }
        using (var f = new RecoveryFixture())
        {
            f.FailCurrent(); f.Run();
            Equal(0, f.Client.Writes.Count, "one failure holds");
            f.Run();
            Equal("node-01", f.Client.Current, "second failure plus confirmation restores service");
            Equal(3, f.Client.Visits["current"], "two failures followed by immediate confirmation");
            Equal(3, f.Client.Visits["node-01"], "target screened rechecked and post-write verified");
            Equal(true, Math.Abs((f.Clock.UtcNow.AddMinutes(10) - f.Saved().CooldownUntilUtc).TotalMilliseconds) < 1,
                "successful verification persists ten minute cooldown");
        }
        using (var f = new RecoveryFixture())
        {
            f.Probe.Result = (node, service, visit) => node == "current" && service == ServiceKind.ChatGPT && visit < 3
                ? ProbeResult.ServiceFailure("temporary") : ProbeResult.Success(100);
            f.Run(); f.Run();
            Equal(0, f.Client.Writes.Count, "confirmation recovery cancels switching");
            Equal(0, f.Client.Delays.Count, "confirmation recovery avoids candidate scan");
        }
        using (var f = new RecoveryFixture())
        {
            f.Probe.Result = (node, service, visit) => service == ServiceKind.ChatGPT
                ? ProbeResult.Unverified("unknown") : ProbeResult.Success(100);
            for (int i = 0; i < 4; i++) f.Run();
            Equal(0, f.Client.Writes.Count, "unknown never counts as confirmed failure");
            Equal(0, f.Saved().ChatGptFailures, "unknown resets consecutive failure count");
        }
        using (var f = new RecoveryFixture())
        {
            f.Probe.Result = (node, service, visit) => service == ServiceKind.Google || service == ServiceKind.SteamApi
                ? ProbeResult.ServiceFailure("optional unavailable") : ProbeResult.Success(400);
            MonitorSnapshot result = null;
            for (int i = 0; i < 4; i++) result = f.Run();
            Equal(0, f.Client.Writes.Count, "optional sites never gate recovery");
            Equal(CandidateHealth.Compatible, result.Health, "core health is independent of optional diagnostics");
            Equal(true, result.Services.Any(x => x.Service == ServiceKind.SteamApi && !x.Available), "optional results remain visible");
        }
        using (var f = new RecoveryFixture())
        {
            f.Probe.Result = (node, service, visit) =>
                (node == "current" && service == ServiceKind.ChatGPT) || (node == "node-01" && service == ServiceKind.Gemini)
                ? ProbeResult.ServiceFailure("failed") : ProbeResult.Success(100);
            f.Run(); f.Run();
            Equal("node-02", f.Client.Current, "candidate Gemini failure rejects faster candidate");
            Equal(false, f.Client.Writes.Contains("node-01"), "Gemini failed candidate is never selected");
        }
        using (var f = new RecoveryFixture())
        {
            f.Probe.Result = (node, service, visit) => service == ServiceKind.ChatGPT &&
                (node == "current" || (node == "node-01" && f.Client.Current == node))
                ? ProbeResult.ServiceFailure("failed") : ProbeResult.Success(node == "node-02" ? 150 : 100);
            f.Run(); f.Run();
            Equal("node-01,node-02", String.Join(",", f.Client.Writes), "post-switch failure tries next screened candidate");
            Equal("node-02", f.Client.Current, "next candidate passes post-switch verification");
        }
        using (var f = new RecoveryFixture())
        {
            f.Probe.Result = (node, service, visit) => service == ServiceKind.ChatGPT &&
                (node == "current" || f.Client.Current == node)
                ? ProbeResult.ServiceFailure("failed") : ProbeResult.Success(100);
            f.Run(); f.Run();
            Equal("node-01,node-02,current", String.Join(",", f.Client.Writes), "all failed post-checks roll back original");
            Equal(DateTime.MinValue, f.Saved().CooldownUntilUtc, "failed recovery does not start success cooldown");
        }
        using (var f = new RecoveryFixture())
        {
            f.FailCurrent(); f.Client.OnScan = node => { if (node == "node-01") f.Client.Current = "manual"; };
            f.Run(); f.Run();
            Equal("manual", f.Client.Current, "external selection before write is respected");
            Equal(0, f.Client.Writes.Count, "external choice cancels candidate writes");
        }
        using (var f = new RecoveryFixture())
        {
            f.FailCurrent();
            f.Client.OnScan = node => { if (node == "node-01" && f.Client.Current == node) f.Client.Current = "manual"; };
            f.Run(); f.Run();
            Equal("manual", f.Client.Current, "external selection during verification prevents rollback");
            Equal("node-01", String.Join(",", f.Client.Writes), "rollback cannot overwrite manual choice");
        }
        using (var f = new RecoveryFixture())
        {
            f.Probe.Result = (node, service, visit) => ProbeResult.Success(node == "current" ? 501 : 100);
            f.Run(); f.Run();
            Equal(0, f.Client.Writes.Count, "two high latency readings do not switch");
            f.Run();
            Equal("node-01", f.Client.Current, "third high latency reading plus confirmation permits recovery");
            f.Restart();
            f.Probe.Result = (node, service, visit) => ProbeResult.Success(node == "node-01" ? 600 : 100);
            f.Run(); f.Run(); f.Run();
            Equal(1, f.Client.Writes.Count, "restart preserves cooldown for latency-only recovery");
            f.Probe.Result = (node, service, visit) => node == "node-01" && service == ServiceKind.Gemini
                ? ProbeResult.ServiceFailure("Gemini down") : ProbeResult.Success(100);
            f.Run(); f.Run();
            Equal(2, f.Client.Writes.Count, "confirmed core outage bypasses active cooldown");
        }
        using (var f = new RecoveryFixture())
        {
            f.Client.Basic = node => RecoveryEvidence.Unknown;
            for (int i = 0; i < 4; i++) f.Run();
            Equal(0, f.Client.Writes.Count, "controller evidence unknown never triggers network recovery");
        }
        using (var f = new RecoveryFixture())
        {
            f.Client.Basic = node => node == "current" ? RecoveryEvidence.Failed : RecoveryEvidence.Healthy;
            f.Run(); f.Run();
            Equal("node-01", f.Client.Current, "independently confirmed base network failure recovers");
        }
        using (var f = new RecoveryFixture())
        {
            f.FailCurrent(); f.Preferences.AutomaticRecovery = false; f.Run(); f.Run();
            Equal(0, f.Client.Writes.Count, "disabled automatic recovery records fault without switching");
            Equal(0, f.Client.Delays.Count, "disabled recovery avoids candidate scans");
        }
        using (var f = new RecoveryFixture())
        {
            f.FailCurrent(); f.Worker.ReportServiceFailure("current", ServiceKind.ChatGPT, f.Clock.UtcNow);
            Equal(0, f.Client.Writes.Count, "user report only requests detection");
            f.Worker.RunOnce(true, f.Preferences); f.Worker.RunOnce(true, f.Preferences);
            Equal(0, f.Client.Writes.Count, "dry run never changes traffic selectors");
        }
        using (var f = new RecoveryFixture())
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            f.FailCurrent(); f.Run();
            f.Client.OnScan = node => { if (node == "current") { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); } };
            Task first = Task.Factory.StartNew(() => f.Run());
            try
            {
                Equal(true, entered.Wait(TimeSpan.FromSeconds(5)), "concurrent recovery test enters scan");
                f.Worker.Run(f.Preferences);
                Equal(false, f.Worker.RestorePrevious(), "manual restore cannot overlap running recovery");
            }
            finally { release.Set(); }
            first.GetAwaiter().GetResult();
            Equal(1, f.Client.Writes.Count, "overlapping requests produce a single verified switch");
        }
        using (var f = new RecoveryFixture())
        {
            f.FailCurrent(); f.Client.General = "manual"; f.Run(); f.Run();
            Equal("manual", f.Client.General, "recovery respects independent manual general selector");
            f.Probe.Result = (node, service, visit) => ProbeResult.Success(100);
            Equal(true, f.Worker.RestorePrevious(), "explicit manual restore is verified and allowed");
            Equal("current", f.Client.Current, "manual restore returns to original");
        }
        using (var f = new RecoveryFixture())
        {
            f.Run();
            var store = new ExperienceStore(Path.Combine(f.Root, "state", "experience.json"));
            ExperienceData data = store.Load();
            data.Assurance.Recovery.PendingOriginal = "current";
            data.Assurance.Recovery.PendingPreviousWrite = "node-01";
            data.Assurance.Recovery.PendingWritten = "node-02";
            store.Save(data, f.Clock.UtcNow);
            f.Client.Current = "node-01"; f.Restart(); f.Run();
            Equal("current", f.Client.Current, "restart recovers journal between two candidate writes");
        }
    }

    private static void ManualOptimizationBehavior()
    {
        using (var f = new RecoveryFixture())
        {
            f.Client.Delay = node => node == "node-01" ? 30 : node == "node-02" ? 50 :
                node == "manual" ? Int32.MaxValue : 100;
            f.Probe.Result = (node, service, visit) => ProbeResult.Success(
                node == "current" ? 600 : node == "node-01" ? 100 : 80);
            f.Run(MonitorCycleTrigger.ManualOptimization);
            Equal("node-01", f.Client.Current,
                "manual optimization immediately uses the first verified faster low-delay candidate");
            Equal(false, f.Client.Visits.ContainsKey("node-02"),
                "manual optimization stops service probing after a verified faster candidate");
        }
        using (var f = new RecoveryFixture())
        {
            f.Probe.Result = (node, service, visit) => ProbeResult.Success(
                node == "current" ? 600 : node == "node-01" ? 100 : 180);
            MonitorSnapshot result = f.Run(MonitorCycleTrigger.ManualOptimization);
            Equal("node-01", f.Client.Current,
                "manual optimization switches a healthy current node to a verified faster candidate");
            Equal(true, result.Decision.Contains("用户强制寻优完成"),
                "manual optimization reports its explicit result");
        }
        using (var f = new RecoveryFixture())
        {
            f.Probe.Result = (node, service, visit) => node == "node-01" && service == ServiceKind.Gemini
                ? ProbeResult.ServiceFailure("gemini failed", 60)
                : ProbeResult.Success(node == "current" ? 500 : node == "node-02" ? 150 : 60);
            f.Run(MonitorCycleTrigger.ManualOptimization);
            Equal("node-02", f.Client.Current,
                "manual optimization rejects a faster candidate when Gemini fails");
            Equal(false, f.Client.Writes.Contains("node-01"),
                "manual optimization never writes a candidate that failed a core service");
        }
        using (var f = new RecoveryFixture())
        {
            f.Probe.Result = (node, service, visit) => ProbeResult.Success(
                node == "current" ? 100 : node == "node-01" ? 120 : 140);
            MonitorSnapshot result = f.Run(MonitorCycleTrigger.ManualOptimization);
            Equal("current", f.Client.Current,
                "manual optimization keeps the current node when no verified candidate is faster");
            Equal(0, f.Client.Writes.Count,
                "manual optimization does not write a slower candidate");
            Equal(true, result.Decision.Contains("未找到实测更快"),
                "manual optimization explains why it held the current node");
        }
        using (var f = new RecoveryFixture())
        {
            f.Probe.Result = (node, service, visit) =>
                node == "node-01" && f.Client.Current == "node-01" && service == ServiceKind.ChatGPT
                    ? ProbeResult.ServiceFailure("post-switch failed", 80)
                    : ProbeResult.Success(node == "current" ? 600 : node == "node-01" ? 80 : 160);
            f.Run(MonitorCycleTrigger.ManualOptimization);
            Equal("node-01,node-02", String.Join(",", f.Client.Writes),
                "manual optimization continues after post-switch verification fails");
            Equal("node-02", f.Client.Current,
                "manual optimization keeps the next verified faster candidate");
        }
        using (var f = new RecoveryFixture())
        {
            f.Probe.Result = (node, service, visit) => ProbeResult.Success(node == "current" ? 600 : 100);
            f.Client.OnScan = node => { if (node == "node-01") f.Client.Current = "manual"; };
            f.Run(MonitorCycleTrigger.ManualOptimization);
            Equal("manual", f.Client.Current,
                "manual optimization respects a user selection made during the scan");
            Equal(0, f.Client.Writes.Count,
                "manual optimization cancels before writing over an external selection");
        }
    }

    private static void ContinuousOptimizationBehavior()
    {
        using (var f = new RecoveryFixture(true))
        {
            f.Client.Choices = new[] { "current", "node-01" };
            f.Client.WebsiteDelay = (node, url) => node == "current" ? 200 : 100;
            f.Probe.Result = (node, service, visit) => ProbeResult.ServiceFailure("login blocked");
            f.Run(MonitorCycleTrigger.Scheduled);
            Equal("current", f.Client.Current,
                "latency-qualified current node stays despite a separate login failure");
            Equal(0, f.Probe.Timeouts.Count,
                "latency-only automatic decision does not run login-chain probes");
        }
        using (var f = new RecoveryFixture(true))
        {
            f.Client.Choices = new[] { "current", "node-01", "node-02" };
            f.Client.WebsiteDelay = (node, url) =>
                url.Contains("chatgpt.com") ? (node == "node-01" ? 700 : node == "node-02" ? 120 : 300) :
                url.Contains("gemini.google.com") ? (node == "node-01" ? 750 : node == "node-02" ? 150 : 300) :
                node == "node-01" ? 10 : node == "node-02" ? 30 : 40;
            f.Probe.Result = (node, service, visit) => ProbeResult.Success(900);
            var scanned = new List<string>();
            f.Client.OnScan = node => scanned.Add(node);
            f.Run(MonitorCycleTrigger.ManualOptimization);
            Equal("node-02,current,node-01", String.Join(",", scanned.Take(3)),
                "core website delays rank candidates before generic Clash latency");
        }
        using (var f = new RecoveryFixture(true))
        {
            f.Client.Choices = new[] { "current", "node-01", "node-02" };
            f.Client.WebsiteDelay = (node, url) => url.Contains("gemini.google.com")
                ? Int32.MaxValue : f.Client.Delay(node);
            f.Probe.Result = (node, service, visit) => ProbeResult.Success(900);
            var scanned = new List<string>();
            f.Client.OnScan = node => scanned.Add(node);
            f.Run(MonitorCycleTrigger.ManualOptimization);
            Equal(0, scanned.Count, "unmeasurable core website latency cannot qualify a node");
        }
        using (var f = new RecoveryFixture(true))
        {
            f.Client.Choices = new[] { "current", "node-01", "node-02" };
            f.Probe.Result = (node, service, visit) => ProbeResult.Success(800);
            f.Run(MonitorCycleTrigger.Scheduled);
            Equal("current,current,current", String.Join(",", f.Client.Delays),
                "healthy current measures its three website delays only");
            Equal(0, f.Client.Writes.Count,
                "healthy current at 800 ms remains selected");
        }
        using (var f = new RecoveryFixture(true))
        {
            f.Client.Choices = new[] { "current", "node-01", "node-02" };
            f.Client.Delay = node => node == "node-01" ? 10 : node == "node-02" ? 20 : 30;
            f.Client.WebsiteDelay = (node, url) => node == "current" ? 900 :
                node == "node-01" ? 100 : 200;
            f.Probe.Result = (node, service, visit) => ProbeResult.Success(
                node == "current" ? 900 : node == "node-01" ? 800 : 100);
            f.Run(MonitorCycleTrigger.Scheduled);
            Equal("node-01", f.Client.Current,
                "first verified candidate within 800 ms switches without waiting for a lower-ranked node");
            Equal(false, f.Client.Visits.ContainsKey("node-02"),
                "automatic scan stops after the first verified 800 ms candidate");
            int afterSwitch = f.Client.Delays.Count;
            f.Run(MonitorCycleTrigger.Scheduled);
            Equal(afterSwitch + 3, f.Client.Delays.Count,
                "scheduled checks hold the selected 800 ms node without restarting all-node discovery");
            f.Run(MonitorCycleTrigger.ManualOptimization);
            Equal(true, f.Client.Visits.ContainsKey("node-02"),
                "explicit force optimization scans beyond a healthy 800 ms current node");
        }
        using (var f = new RecoveryFixture(true))
        {
            f.Client.Choices = new[] { "current", "node-01", "node-02" };
            f.Client.Delay = node => node == "current" ? 10 : node == "node-01" ? 20 : 30;
            f.Probe.Result = (node, service, visit) => ProbeResult.Success(900);
            f.Run(MonitorCycleTrigger.ManualOptimization);
            Equal(0, f.Client.ConnectivityCalls,
                "continuous scan reuses successful Clash delays for basic reachability");
            Equal(0, f.Probe.Timeouts.Count,
                "continuous latency scan does not request login-chain probes");
        }
        using (var f = new RecoveryFixture(true))
        {
            f.Client.Choices = new[] { "current", "node-01", "node-02" };
            f.Client.Delay = node => node == "node-01" ? Int32.MaxValue : 20;
            f.Client.Basic = node => node == "node-01" ? RecoveryEvidence.Failed : RecoveryEvidence.Healthy;
            f.Probe.Result = (node, service, visit) => ProbeResult.Success(900);
            f.Run(MonitorCycleTrigger.ManualOptimization);
            Equal(0, f.Client.ConnectivityCalls,
                "latency-only scan does not run a separate connectivity probe");
            Equal(false, f.Client.Visits.ContainsKey("node-01"),
                "failed basic fallback skips expensive core probes for an ineligible node");
        }
        using (var f = new RecoveryFixture(true))
        {
            f.Client.Choices = new[] { "current", "node-01", "node-02" };
            f.Client.Delay = node => node == "node-01" ? 20 : node == "current" ? 30 : 40;
            f.Client.WebsiteDelay = (node, url) => url.Contains("chatgpt.com") ?
                node == "node-01" ? 100 : node == "node-02" ? 450 : 600 :
                url.Contains("gemini.google.com") ?
                node == "node-01" ? 700 : node == "node-02" ? 450 : 600 :
                f.Client.Delay(node);
            var scanned = new List<string>();
            f.Client.OnScan = node => scanned.Add(node);
            f.Probe.Result = (node, service, visit) => ProbeResult.Success(
                node == "current" ? 600 : node == "node-01"
                    ? (service == ServiceKind.ChatGPT ? 100 : 700) : 450);
            f.Run(MonitorCycleTrigger.ManualOptimization);
            Equal("node-02,current,node-01", String.Join(",", scanned.Take(3)),
                "continuous optimization checks exit region in core website latency order");
            Equal("node-02", f.Client.Current,
                "continuous optimization ranks by the slower of ChatGPT and Gemini");
            Equal(true, File.ReadAllText(Path.Combine(f.Root, "current-status.txt")).Contains("0.7.0-preview.36"),
                "continuous optimization publishes the latest status file");
        }
        using (var f = new RecoveryFixture(true))
        {
            f.Client.Choices = new[] { "current", "node-01", "node-02" };
            f.Client.Delay = node => node == "node-01" ? 20 : node == "node-02" ? 30 : 40;
            f.Client.WebsiteDelay = (node, url) => node == "current" ? 900 :
                node == "node-01" ? 50 : 120;
            f.ExitProbe.Country = node => node == "node-01" ? "HK" : "JP";
            f.Probe.Result = (node, service, visit) => ProbeResult.Success(
                node == "node-01" ? 50 : node == "node-02" ? 120 : 900);
            f.Run(MonitorCycleTrigger.Scheduled);
            Equal("node-02", f.Client.Current,
                "continuous optimization excludes the fastest node outside the shared AI region policy");
            Equal(false, f.Client.Writes.Contains("node-01"),
                "region-ineligible node is never written to the active selector");
        }
        using (var f = new RecoveryFixture(true))
        {
            f.Client.Choices = new[] { "current", "node-01", "node-02" };
            f.Client.Delay = node => node == "current" ? 10 : node == "node-01" ? 20 : 30;
            f.Probe.Result = (node, service, visit) => ProbeResult.Success(
                node == "current" ? 80 : node == "node-01" ? 120 : 160);
            f.Run(MonitorCycleTrigger.Scheduled);
            int firstVisits = f.Client.Visits.ContainsKey("node-01") ? f.Client.Visits["node-01"] : 0;
            f.Run(MonitorCycleTrigger.Scheduled);
            Equal(firstVisits, f.Client.Visits.ContainsKey("node-01") ? f.Client.Visits["node-01"] : 0,
                "continuous optimization holds alternatives while current latency remains at or below 800 ms");
            Equal(0, f.Client.Writes.Count,
                "continuous optimization keeps the selector when the current node ranks first");
        }
        using (var f = new RecoveryFixture(true))
        {
            f.Client.Choices = new[] { "current", "node-01", "node-02" };
            f.Client.Delay = node => node == "node-01" ? 10 : node == "node-02" ? 20 : 30;
            f.Client.WebsiteDelay = (node, url) => node == "current" ? 900 :
                node == "node-01" && f.Client.Current == "node-01" ? Int32.MaxValue :
                node == "node-01" ? 50 : 100;
            f.Probe.Result = (node, service, visit) => node == "node-01" && visit > 1 &&
                service == ServiceKind.ChatGPT ? ProbeResult.ServiceFailure("changed after switch", 100) :
                ProbeResult.Success(node == "node-01" ? 50 : node == "node-02" ? 100 : 900);
            f.Run(MonitorCycleTrigger.Scheduled);
            Equal("node-01,current,node-02", String.Join(",", f.Client.Writes),
                "continuous optimization advances after the first-ranked node fails post-switch verification");
            Equal("node-02", f.Client.Current,
                "continuous optimization keeps the next verified ranked node");
        }
        using (var f = new RecoveryFixture(true))
        {
            f.Client.Choices = new[] { "current", "node-01", "node-02" };
            f.Client.Delay = node => node == "node-01" ? 10 : node == "node-02" ? 20 : 30;
            f.Client.WebsiteDelay = (node, url) => node == "current" ? 900 :
                node == "node-01" && f.Client.Current == "node-01" ? 801 :
                node == "node-01" ? 700 : 750;
            f.Probe.Result = (node, service, visit) => ProbeResult.Success(
                node == "current" ? 900 : node == "node-01" && visit > 1 ? 801 :
                node == "node-01" ? 700 : 500);
            f.Run(MonitorCycleTrigger.Scheduled);
            Equal("node-02", f.Client.Current,
                "post-switch response above 800 ms resumes candidate search");
            Equal("node-01,current,node-02", String.Join(",", f.Client.Writes),
                "slow post-switch verification rolls back before probing the next candidate");
        }
        using (var f = new RecoveryFixture(true))
        {
            f.Client.Choices = new[] { "current", "node-01", "node-02", "manual" };
            f.Client.Delay = node => node == "node-01" ? 10 : node == "node-02" ? 20 : 30;
            f.Client.WebsiteDelay = (node, url) => node == "current" ? 900 :
                node == "node-01" ? 100 : 200;
            f.Probe.Result = (node, service, visit) => ProbeResult.Success(node == "current" ? 900 : 100);
            f.Client.OnScan = node => { if (node == "node-01") f.Client.Current = "manual"; };
            f.Run(MonitorCycleTrigger.Scheduled);
            Equal("manual", f.Client.Current,
                "continuous optimization preserves a manual selection made during the sweep");
            Equal(0, f.Client.Writes.Count,
                "continuous optimization does not overwrite an external selection");
        }
    }
}
