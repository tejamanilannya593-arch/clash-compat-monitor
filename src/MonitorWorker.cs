using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Runtime.InteropServices;

public sealed class BoundedLogger
{
    private readonly string path;
    private readonly long maximumBytes;
    private readonly object gate = new object();
    public BoundedLogger(string path, long maximumBytes) { this.path = path; this.maximumBytes = maximumBytes; }

    public void Write(string message)
    {
        lock (gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string line = DateTime.UtcNow.ToString("o") + " " + message.Replace('\r', ' ').Replace('\n', ' ') + Environment.NewLine;
            byte[] added = Encoding.UTF8.GetBytes(line);
            if (File.Exists(path) && new FileInfo(path).Length + added.Length > maximumBytes)
            {
                byte[] existing = File.ReadAllBytes(path);
                int keep = (int)Math.Min(existing.Length, Math.Max(0, maximumBytes / 2));
                var retained = new byte[keep];
                Buffer.BlockCopy(existing, existing.Length - keep, retained, 0, keep);
                using (var rewrite = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)) rewrite.Write(retained, 0, retained.Length);
            }
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                int count = (int)Math.Min(added.Length, Math.Max(0, maximumBytes - stream.Length));
                stream.Write(added, 0, count);
            }
        }
    }
}

public sealed class MonitorWorker
{
    private readonly MonitorConfiguration config;
    private readonly IMihomoClient mihomo;
    private readonly CompatibilityScanner scanner;
    private readonly BoundedLogger logger;
    private readonly IClock clock;
    private readonly FailoverController controller;
    private readonly TrafficGuard trafficGuard;
    private DateTime lastQualityRefreshUtc = DateTime.MinValue;
    private int running;

    public MonitorWorker(MonitorConfiguration config, IMihomoClient mihomo, IServiceProbe probe, BoundedLogger logger, IClock clock)
    {
        this.config = config;
        this.mihomo = mihomo;
        this.scanner = new CompatibilityScanner(mihomo, probe, config.ProbeGroup);
        this.logger = logger;
        this.clock = clock;
        controller = new FailoverController(clock, config.MinimumHold);
        trafficGuard = new TrafficGuard(clock, 3.0 * 1024 * 1024, TimeSpan.FromMinutes(5));
    }

    public void RunOnce(bool dryRun)
    {
        if (System.Threading.Interlocked.Exchange(ref running, 1) != 0) return;
        try
        {
            ConflictResult conflict = new ConflictDetector().Evaluate(RuntimeInspector.Capture(mihomo));
            if (conflict.Paused) { logger.Write("paused-conflict " + conflict.Reason); return; }
            bool reloadDetected = mihomo.IsRuntimeIpv6Enabled();
            if (reloadDetected)
            {
                var pipeClient = mihomo as MihomoPipeClient;
                if (pipeClient == null || !pipeClient.EnsureIpv4Compatibility(config.ClashConfigPath))
                    throw new InvalidOperationException("Could not restore the IPv4 compatibility overlay.");
                logger.Write("restored IPv4 compatibility overlay after external config reload");
            }
            IList<CandidateNode> candidates = CandidateCatalog.Filter(mihomo.GetChoices(config.SharedGroup));
            if (candidates.Count == 0) { logger.Write("no eligible candidates"); return; }
            var store = new StateStore(config.StatePath);
            HealthState state = store.Load();
            string fingerprint = Fingerprint(candidates);
            bool subscriptionChanged = !String.Equals(state.SubscriptionFingerprint, fingerprint, StringComparison.Ordinal);
            state.ApplySubscriptionFingerprint(fingerprint);
            var qualityStore = new QualityStateStore(config.QualityStatePath);
            List<QualitySample> qualityHistory = qualityStore.Load();
            IList<ServiceKind> optional = OptionalServiceActivator.FromProcessNames(Process.GetProcesses().Select(x => x.ProcessName));
            string current = mihomo.GetSelected(config.SharedGroup);
            bool recoveredAfterReload = false;
            string recoveryTarget = ReloadRecovery.ChooseTarget(reloadDetected, state, candidates, clock.UtcNow,
                config.ReloadRecoveryFreshness);
            if (!String.IsNullOrEmpty(recoveryTarget) && recoveryTarget != current)
            {
                if (!dryRun)
                {
                    mihomo.Select(config.SharedGroup, recoveryTarget);
                    current = recoveryTarget;
                    recoveredAfterReload = true;
                    logger.Write("restored shared selector to recent verified " + SafeName(recoveryTarget));
                }
                else logger.Write("dry-run would restore shared selector to " + SafeName(recoveryTarget));
            }
            else if (reloadDetected && String.IsNullOrEmpty(recoveryTarget))
                logger.Write("reload recovery skipped because no recent verified candidate is available");
            CandidateNode currentCandidate = candidates.FirstOrDefault(x => x.Name == current);
            CandidateScanResult currentScan = currentCandidate == null ? new CandidateScanResult(current ?? "", CandidateHealth.Transient, null, "current not eligible") : scanner.Scan(currentCandidate, optional);
            state.Records[currentScan.Name] = Record(currentScan);
            state.RememberPreferred(currentScan.Name, currentScan.Health, clock.UtcNow);

            QualitySample priorCurrent = Latest(qualityHistory, current);
            double currentResponse = QualityMeasurement.ResponseMilliseconds(currentScan, priorCurrent == null ? 5000 : priorCurrent.ResponseMedianMs);
            if (currentScan.Health != CandidateHealth.Unknown) qualityHistory.Add(new QualitySample(currentScan.Name, clock.UtcNow, currentScan.Health == CandidateHealth.Compatible || currentScan.Health == CandidateHealth.BasicCompatible,
                currentResponse, Jitter(qualityHistory, currentScan.Name, currentResponse), priorCurrent == null ? 0 : priorCurrent.ThroughputBytesPerSecond,
                currentCandidate == null ? (double?)null : currentCandidate.Multiplier));

            if (lastQualityRefreshUtc == DateTime.MinValue)
                lastQualityRefreshUtc = qualityHistory.Where(x => x.ThroughputBytesPerSecond > 0).Select(x => x.CheckedUtc).DefaultIfEmpty(DateTime.MinValue).Max();
            bool throughputDue = RefreshPolicy.ShouldRunThroughput(subscriptionChanged, currentScan.Health,
                lastQualityRefreshUtc, clock.UtcNow, config.QualityRefreshInterval);
            bool refreshQuality = RefreshPolicy.ShouldRefreshCandidates(subscriptionChanged, currentScan.Health, throughputDue);

            var freshlyVerified = new HashSet<string>(StringComparer.Ordinal);
            if (currentScan.Health == CandidateHealth.Compatible || currentScan.Health == CandidateHealth.BasicCompatible) freshlyVerified.Add(currentScan.Name);
            if (refreshQuality)
            {
                var delays = candidates.ToDictionary(x => x.Name, x => mihomo.GetDelay(x.Name, config.DelayProbeUrl, 5000), StringComparer.Ordinal);
                IList<CandidateNode> delayed = CandidatePreselector.Select(candidates, delays, current, 8);
                var compatible = new List<CandidateNode>();
                var failureCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (CandidateNode candidate in delayed)
                {
                    CandidateScanResult scan = candidate.Name == current ? currentScan : scanner.Scan(candidate, optional);
                    state.Records[candidate.Name] = Record(scan);
                    if (scan.Health == CandidateHealth.Compatible || scan.Health == CandidateHealth.BasicCompatible) { compatible.Add(candidate); freshlyVerified.Add(candidate.Name); }
                    QualitySample previous = Latest(qualityHistory, candidate.Name);
                    int delay = delays[candidate.Name];
                    double response = QualityMeasurement.ResponseMilliseconds(scan, 5000);
                    if (scan.Health != CandidateHealth.Unknown) qualityHistory.Add(new QualitySample(candidate.Name, clock.UtcNow, scan.Health == CandidateHealth.Compatible || scan.Health == CandidateHealth.BasicCompatible,
                        MedianResponse(qualityHistory, candidate.Name, response), Jitter(qualityHistory, candidate.Name, response),
                        previous == null ? 0 : previous.ThroughputBytesPerSecond, candidate.Multiplier));
                    if (scan.Health != CandidateHealth.Compatible && scan.Health != CandidateHealth.BasicCompatible)
                    {
                        string failureKey = scan.FailedService.HasValue ? scan.FailedService.Value.ToString() : scan.Health.ToString();
                        int count;
                        failureCounts.TryGetValue(failureKey, out count);
                        failureCounts[failureKey] = count + 1;
                    }
                }

                string throughputStatus = "not-due";
                if (throughputDue && trafficGuard.SampleAndMayProbe(new SystemTrafficMeter()))
                {
                    throughputStatus = "sampled";
                    var budget = new ThroughputBudget(5L * ThroughputProbe.SampleBytes);
                    using (var throughputProbe = new ThroughputProbe(config.ProbeProxy, config.ThroughputProbeUrl))
                    {
                        foreach (CandidateNode candidate in compatible.Take(5))
                        {
                            if (!budget.TryReserve(ThroughputProbe.SampleBytes)) break;
                            mihomo.Select(config.ProbeGroup, candidate.Name);
                            ThroughputResult measured = throughputProbe.Probe();
                            QualitySample previous = Latest(qualityHistory, candidate.Name);
                            qualityHistory.Add(new QualitySample(candidate.Name, clock.UtcNow, true,
                                previous == null ? 5000 : previous.ResponseMedianMs,
                                previous == null ? 0 : previous.JitterMs, measured.BytesPerSecond, candidate.Multiplier));
                        }
                    }
                    lastQualityRefreshUtc = clock.UtcNow;
                }
                else if (throughputDue)
                {
                    throughputStatus = "postponed";
                    lastQualityRefreshUtc = clock.UtcNow - config.QualityRefreshInterval + TimeSpan.FromMinutes(5);
                    logger.Write("throughput postponed because foreground traffic is active");
                }
                logger.Write("quality refresh candidates=" + delayed.Count + " compatible=" + compatible.Count +
                    " failures=" + String.Join(",", failureCounts.OrderBy(x => x.Key).Select(x => x.Key + ":" + x.Value)) +
                    " throughput=" + throughputStatus);
            }

            qualityHistory = QualityStateStore.Bound(qualityHistory);
            var latestCohort = candidates.Select(x => Latest(qualityHistory, x.Name)).Where(x => x != null && x.Compatible && freshlyVerified.Contains(x.Name)).ToList();
            var scores = new Dictionary<string, QualityBreakdown>(StringComparer.Ordinal);
            foreach (QualitySample sample in latestCohort)
                scores[sample.Name] = QualityScorer.Score(sample, qualityHistory.Where(x => x.Name == sample.Name), latestCohort, clock.UtcNow);
            QualityBreakdown currentScore = null;
            var best = scores.OrderByDescending(x => x.Value.Score).FirstOrDefault();
            bool switched = false;
            string decision = recoveredAfterReload ? "配置重载后恢复最近稳定节点" : "保持当前节点";
            double? reportedScore = null;
            QualityBreakdown reportedCurrent;
            if (scores.TryGetValue(current, out reportedCurrent)) reportedScore = reportedCurrent.Score;
            if (currentScan.Health == CandidateHealth.Compatible || currentScan.Health == CandidateHealth.BasicCompatible || currentScan.Health == CandidateHealth.Unknown)
                controller.Decide(true, false, current, null);
            if (currentScan.Health != CandidateHealth.Unknown && !String.IsNullOrEmpty(best.Key) && best.Key != current)
            {
                bool currentCompatible = (currentScan.Health == CandidateHealth.Compatible || currentScan.Health == CandidateHealth.BasicCompatible) && scores.TryGetValue(current, out currentScore);
                bool shouldSwitch;
                string reason;
                if (currentCompatible)
                {
                    FailoverDecision qualityDecision = controller.DecideQuality(currentScore.Score, best.Value.Score, false, freshlyVerified.Contains(best.Key));
                    shouldSwitch = qualityDecision.ShouldSwitch;
                    reason = qualityDecision.Reason;
                }
                else
                {
                    NodeHealthRecord bestRecord;
                    state.Records.TryGetValue(best.Key, out bestRecord);
                    FailoverDecision failureDecision = controller.Decide(false, false, current, bestRecord == null ? null : new[] { bestRecord });
                    shouldSwitch = failureDecision.ShouldSwitch && freshlyVerified.Contains(best.Key);
                    reason = failureDecision.Reason;
                }
                decision = reason;
                if (shouldSwitch && !dryRun && String.Equals(mihomo.GetSelected(config.SharedGroup), current, StringComparison.Ordinal))
                {
                    mihomo.Select(config.SharedGroup, best.Key);
                    controller.RecordSwitch();
                    switched = true;
                    reportedScore = best.Value.Score;
                    decision = "已切换：" + reason;
                    logger.Write("switched node=" + SafeName(best.Key) + " score=" + best.Value.Score.ToString("F1") + " reason=" + reason);
                }
            }
            if (dryRun) logger.Write("dry-run quality evaluation completed; shared selector unchanged");
            else if (!switched && currentScan.Health == CandidateHealth.Unknown)
            {
                decision = "证据不足，保留当前节点";
                logger.Write("待验证，保留当前连接；" + currentScan.Detail);
            }
            else if (!switched && currentScan.Health != CandidateHealth.Compatible && currentScan.Health != CandidateHealth.BasicCompatible) logger.Write("current check failed " + currentScan.Health + "; awaiting safe replacement");
            if (!dryRun)
            {
                string actual = mihomo.GetSelected(config.SharedGroup);
                CandidateHealth status = actual == currentScan.Name ? currentScan.Health : CandidateHealth.Unknown;
                string detail = actual == currentScan.Name ? currentScan.Detail : "节点发生变化，等待下一轮验证";
                string text = StatusReport.Format(clock.UtcNow, actual, status, reportedScore, decision, detail);
                StatusReport.WriteAtomic(Path.Combine(config.RootPath, "current-status.txt"), text);
            }
            store.Save(state, candidates.Select(x => x.Name));
            qualityStore.Save(qualityHistory);
        }
        finally
        {
            System.Threading.Volatile.Write(ref running, 0);
            MemoryTrimmer.TrimIdleWorkingSet();
        }
    }

    private NodeHealthRecord Record(CandidateScanResult result)
    {
        return new NodeHealthRecord(result.Name, result.Health, clock.UtcNow, HealthPolicy.CooldownUntil(result.Health, clock.UtcNow), false);
    }

    private static string Fingerprint(IEnumerable<CandidateNode> candidates)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(string.Join("\n", candidates.Select(x => x.Name)));
        using (SHA256 hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(bytes));
    }

    private static string SafeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "(none)";
        using (SHA256 hash = SHA256.Create())
            return "node-" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(name)), 0, 4).Replace("-", "").ToLowerInvariant();
    }

    private static QualitySample Latest(IEnumerable<QualitySample> samples, string name)
    {
        return (samples ?? Enumerable.Empty<QualitySample>()).Where(x => String.Equals(x.Name, name, StringComparison.Ordinal))
            .OrderByDescending(x => x.CheckedUtc).FirstOrDefault();
    }

    private static double MedianResponse(IEnumerable<QualitySample> samples, string name, double current)
    {
        var values = samples.Where(x => x.Name == name && x.ResponseMedianMs > 0).OrderByDescending(x => x.CheckedUtc)
            .Take(4).Select(x => x.ResponseMedianMs).Concat(new[] { current }).OrderBy(x => x).ToList();
        return values.Count % 2 == 1 ? values[values.Count / 2] : (values[values.Count / 2 - 1] + values[values.Count / 2]) / 2.0;
    }

    private static double Jitter(IEnumerable<QualitySample> samples, string name, double current)
    {
        var values = samples.Where(x => x.Name == name && x.ResponseMedianMs > 0).OrderByDescending(x => x.CheckedUtc)
            .Take(4).Select(x => x.ResponseMedianMs).Concat(new[] { current }).ToList();
        if (values.Count < 2) return 0;
        double mean = values.Average();
        return Math.Sqrt(values.Sum(x => (x - mean) * (x - mean)) / values.Count);
    }
}

internal static class MemoryTrimmer
{
    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr process);
    public static void TrimIdleWorkingSet()
    {
        try { EmptyWorkingSet(Process.GetCurrentProcess().Handle); } catch { }
    }
}
