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

public sealed class MonitorWorker : IRestorableCycleRunner, IProgressCycleRunner
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
    private string previousSelectedNode;
    private Stopwatch cycleTimer;
    private int candidateOffset;
    private UserPreferences lastPreferences = UserPreferences.Defaults();
    private readonly ExperienceStore experienceStore;
    private ExperienceData experience;
    private bool firstCompletedCycle = true;
    public event Action<MonitorSnapshot> Progress;
    public Func<bool> ShouldStop { get; set; }
    private bool StopRequired() { return (ShouldStop != null && ShouldStop()) || (cycleTimer != null && cycleTimer.Elapsed > TimeSpan.FromMinutes(3)); }
    private void CheckStop() { if (StopRequired()) throw new OperationCanceledException("检测已暂停或达到本轮时间预算，将在下一轮继续"); }
    private void Report(string stage, MonitorSnapshot evidence)
    {
        CheckStop();
        var handler = Progress;
        if (handler != null) handler(evidence == null
            ? MonitorSnapshot.CreateState(MonitorRunState.Checking, stage, DateTime.MinValue, DateTime.MaxValue)
            : evidence.WithProgress(stage));
    }

    public MonitorWorker(MonitorConfiguration config, IMihomoClient mihomo, IServiceProbe probe, BoundedLogger logger, IClock clock)
    {
        this.config = config;
        this.mihomo = mihomo;
        this.scanner = new CompatibilityScanner(mihomo, probe, config.ProbeGroup);
        scanner.ShouldStop = StopRequired;
        this.logger = logger;
        this.clock = clock;
        experienceStore = new ExperienceStore(Path.Combine(config.RootPath, "state", "experience.json"));
        experience = experienceStore.Load();
        if (experience.Assurance == null || experience.Assurance.Standbys == null) experience.Assurance = new ConnectionAssurance();
        controller = new FailoverController(clock, config.MinimumHold);
        trafficGuard = new TrafficGuard(clock, 3.0 * 1024 * 1024, TimeSpan.FromMinutes(5));
    }

    public void RunOnce(bool dryRun)
    {
        RunOnce(dryRun, UserPreferences.Defaults());
    }

    public MonitorSnapshot Run(UserPreferences preferences)
    {
        return RunOnce(false, preferences);
    }

    public bool RestorePrevious()
    {
        if (String.IsNullOrWhiteSpace(previousSelectedNode)) return false;
        string current = mihomo.GetSelected(config.SharedGroup);
        if (String.Equals(current, previousSelectedNode, StringComparison.Ordinal)) return false;
        if (!mihomo.GetChoices(config.SharedGroup).Contains(previousSelectedNode, StringComparer.Ordinal)) return false;
        string target = previousSelectedNode;
        CandidateScanResult verified = scanner.ScanSelected(new CandidateNode(target, null), lastPreferences.RequiredServices);
        if (verified.Health != CandidateHealth.Compatible && verified.Health != CandidateHealth.BasicCompatible) return false;
        CheckStop();
        if (mihomo.GetSelected(config.SharedGroup) != current) return false;
        SelectRecorded(current, target, "用户恢复上一个节点（已复检）");
        previousSelectedNode = current;
        controller.RecordSwitch();
        logger.Write("restored previous node=" + SafeName(target));
        return true;
    }

    public MonitorSnapshot RunOnce(bool dryRun, UserPreferences preferences)
    {
        if (preferences == null) throw new ArgumentNullException("preferences");
        if (preferences.RequiredServices == null || preferences.RequiredServices.Count == 0)
            throw new ArgumentException("At least one required service is needed.", "preferences");
        if (System.Threading.Interlocked.Exchange(ref running, 1) != 0)
            return MonitorSnapshot.CreateState(MonitorRunState.Starting, "检测正在进行", clock.UtcNow,
                clock.UtcNow.Add(config.CycleInterval));
        try
        {
            cycleTimer = Stopwatch.StartNew();
            lastPreferences = preferences;
            Report("正在检查 Clash 连接与运行环境", null);
            ConflictResult conflict = new ConflictDetector().Evaluate(RuntimeInspector.Capture(mihomo));
            if (conflict.Paused)
            {
                logger.Write("paused-conflict " + conflict.Reason);
                return MonitorSnapshot.CreateState(MonitorRunState.Degraded, conflict.Reason, clock.UtcNow,
                    clock.UtcNow.Add(config.CycleInterval));
            }
            bool reloadDetected = mihomo.IsRuntimeIpv6Enabled();
            if (reloadDetected)
            {
                var pipeClient = mihomo as MihomoPipeClient;
                if (pipeClient == null || !pipeClient.EnsureIpv4Compatibility(config.ClashConfigPath))
                    throw new InvalidOperationException("Could not restore the IPv4 compatibility overlay.");
                logger.Write("restored IPv4 compatibility overlay after external config reload");
            }
            IList<CandidateNode> candidates = CandidateCatalog.Filter(mihomo.GetChoices(config.SharedGroup));
            if (candidates.Count == 0)
            {
                logger.Write("no eligible candidates");
                return MonitorSnapshot.CreateState(MonitorRunState.Degraded, "没有可用候选节点", clock.UtcNow,
                    clock.UtcNow.Add(config.CycleInterval));
            }
            var store = new StateStore(config.StatePath);
            HealthState state = store.Load();
            string fingerprint = Fingerprint(candidates);
            string servicesKey = String.Join(",", preferences.RequiredServices.Distinct().OrderBy(x => x));
            string memoryScope = experience.ResolveScope(fingerprint, candidates.Select(x => x.Name), servicesKey);
            logger.Write("subscription candidates=" + candidates.Count + " continuity=" + experience.ScopeContinuityReason);
            ConnectionAssurance assurance = experience.Assurance;
            assurance.SetScope(memoryScope);
            var requiredServices = preferences.RequiredServices.Distinct().ToList();
            var suppressedServices = requiredServices.Where(x =>
                ServiceIncidentPolicy.IsActive(experience.ServiceIncidents, x, clock.UtcNow)).ToList();
            IList<ServiceKind> servicesToProbe = ServiceIncidentPolicy.ServicesToProbe(requiredServices,
                experience.ServiceIncidents, clock.UtcNow);
            if (suppressedServices.Count > 0)
                logger.Write("service circuit active services=" + String.Join(",", suppressedServices));
            bool subscriptionChanged = !String.Equals(state.SubscriptionFingerprint, fingerprint, StringComparison.Ordinal);
            state.ApplySubscriptionFingerprint(fingerprint);
            var qualityStore = new QualityStateStore(config.QualityStatePath);
            List<QualitySample> qualityHistory = qualityStore.Load();
            string current = mihomo.GetSelected(config.SharedGroup);
            string cycleStartNode = current;
            experience.RecordChange(experience.LastNode, current, "检测到外部变更（Clash 手动选择或核心重载）", clock.UtcNow);
            experienceStore.Save(experience, clock.UtcNow);
            Report("正在检测当前节点：" + current, null);
            bool recoveredAfterReload = false;
            string recoveryTarget = ReloadRecovery.ChooseTarget(reloadDetected, state, candidates, clock.UtcNow,
                config.ReloadRecoveryFreshness);
            if (suppressedServices.Count > 0)
            {
                recoveryTarget = null;
                logger.Write("reload recovery skipped while service circuit is active");
            }
            if (!String.IsNullOrEmpty(recoveryTarget) && recoveryTarget != current)
            {
                logger.Write("reload recovery live recheck target=" + SafeName(recoveryTarget));
                CandidateScanResult recoveryScan = scanner.ScanSelected(new CandidateNode(recoveryTarget, null), requiredServices);
                CheckStop();
                if (!dryRun && preferences.AutomaticOptimization &&
                    (recoveryScan.Health == CandidateHealth.Compatible || recoveryScan.Health == CandidateHealth.BasicCompatible) &&
                    mihomo.GetSelected(config.SharedGroup) == current)
                {
                    SelectRecorded(current, recoveryTarget, "配置重载后恢复（已复检）");
                    current = recoveryTarget;
                    recoveredAfterReload = true;
                    logger.Write("restored shared selector to recent verified " + SafeName(recoveryTarget));
                }
                else if (dryRun) logger.Write("dry-run would restore shared selector to " + SafeName(recoveryTarget));
                else logger.Write("reload recovery recheck rejected target=" + SafeName(recoveryTarget) + " health=" + recoveryScan.Health);
            }
            else if (reloadDetected && String.IsNullOrEmpty(recoveryTarget))
                logger.Write("reload recovery skipped because no recent verified candidate is available");
            CandidateNode currentCandidate = candidates.FirstOrDefault(x => x.Name == current);
            CandidateScanResult currentScan = currentCandidate == null ? new CandidateScanResult(current ?? "", CandidateHealth.Transient, null, "current not eligible") : scanner.ScanSelected(currentCandidate, servicesToProbe);
            currentScan = ServiceIncidentPolicy.AttachSuppressed(currentScan, suppressedServices);
            logger.Write("current check completed elapsed_seconds=" + cycleTimer.Elapsed.TotalSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                " health=" + currentScan.Health);
            string assuranceDecision = null;
            bool observing = !String.IsNullOrEmpty(assurance.Target);
            string serviceIncidentDecision = suppressedServices.Count == 0 || currentScan.Health != CandidateHealth.Unknown ? null :
                ServiceIncidentText(suppressedServices) + " 多节点同类异常，熔断观察中，不归因于节点";
            var scans = new Dictionary<string, CandidateScanResult>(StringComparer.Ordinal);
            var scanTimes = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            var speedSamples = new Dictionary<string, QualitySample>(StringComparer.Ordinal);
            scans[currentScan.Name] = currentScan;
            scanTimes[currentScan.Name] = clock.UtcNow;
            MonitorSnapshot currentEvidence = MonitorSnapshot.CreateRunning(current, currentScan, null, "当前节点检测完成", clock.UtcNow, DateTime.MaxValue)
                .WithSelectionReason(experience.SelectionSummary(current, firstCompletedCycle && current == cycleStartNode));
            bool currentFailureConfirmed = false;
            if (currentCandidate != null && StartupRecovery.NeedsImmediateConfirmation(currentScan))
            {
                ServiceKind failedService = currentScan.FailedService.Value;
                if (!observing) controller.Decide(false, false, current, null);
                logger.Write("current first failure node=" + SafeName(current) + " service=" + failedService +
                    " health=" + currentScan.Health + " action=confirm");
                Report("当前节点出现异常，正在快速复检 " + MonitorPresentation.ServiceLabel(failedService), currentEvidence);
                CandidateScanResult confirmation = scanner.ScanSelected(currentCandidate, new[] { failedService });
                currentFailureConfirmed = confirmation.Health != CandidateHealth.Unknown && !ConnectionAssurance.Passed(confirmation);
                logger.Write("current failure confirmation node=" + SafeName(current) + " service=" + failedService +
                    " health=" + confirmation.Health + " confirmed=" + currentFailureConfirmed.ToString().ToLowerInvariant());
                if (currentFailureConfirmed)
                {
                    var incidentChecks = new List<CandidateScanResult>();
                    foreach (string standby in assurance.Available(candidates.Select(x => x.Name), current, clock.UtcNow).Take(2))
                    {
                        Report("正在区分节点故障与服务端异常：" + standby, currentEvidence);
                        CandidateNode standbyCandidate = candidates.FirstOrDefault(x => x.Name == standby);
                        if (standbyCandidate != null)
                            incidentChecks.Add(scanner.ScanSelected(standbyCandidate, new[] { failedService }));
                    }
                    if (ServiceIncidentPolicy.HasConsensus(confirmation, incidentChecks, failedService))
                    {
                        ProbeFailureKind kind = ServiceIncidentPolicy.FailureKind(confirmation, failedService);
                        ServiceIncidentPolicy.Open(experience.ServiceIncidents, failedService, kind,
                            clock.UtcNow, TimeSpan.FromMinutes(10));
                        if (!suppressedServices.Contains(failedService)) suppressedServices.Add(failedService);
                        servicesToProbe = ServiceIncidentPolicy.ServicesToProbe(requiredServices,
                            experience.ServiceIncidents, clock.UtcNow);
                        currentScan = ServiceIncidentPolicy.AttachSuppressed(currentScan, suppressedServices);
                        scans[currentScan.Name] = currentScan;
                        currentEvidence = MonitorSnapshot.CreateRunning(current, currentScan, null,
                            "已识别服务端异常", clock.UtcNow, DateTime.MaxValue)
                            .WithSelectionReason(experience.SelectionSummary(current, firstCompletedCycle && current == cycleStartNode));
                        currentFailureConfirmed = false;
                        if (!observing) controller.Decide(true, false, current, null);
                        serviceIncidentDecision = MonitorPresentation.ServiceLabel(failedService) +
                            " 已在当前节点和两个备用节点出现同类异常，暂停归因和切换 10 分钟";
                        logger.Write("service circuit opened service=" + failedService + " kind=" + kind +
                            " confirmations=3 duration_minutes=10");
                        experienceStore.Save(experience, clock.UtcNow);
                    }
                }
                else if (!observing) controller.Decide(true, false, current, null);
            }
            if (observing && suppressedServices.Count > 0)
            {
                assurance.Target = null;
                assurance.HoldUntilUtc = experience.ServiceIncidents.Where(x => x != null &&
                    suppressedServices.Contains(x.Service)).Select(x => x.UntilUtc).DefaultIfEmpty(clock.UtcNow.AddMinutes(10)).Max();
                observing = false;
                assuranceDecision = serviceIncidentDecision;
            }
            if (observing)
            {
                if (assurance.Target != current)
                {
                    assurance.Target = null;
                    assurance.HoldUntilUtc = clock.UtcNow.AddMinutes(10);
                    assuranceDecision = "检测到外部切换，取消自动回退";
                }
                else
                {
                    bool rollback = assurance.NeedsRollback(currentScan);
                    assuranceDecision = "切换后观察：第 " + assurance.VerificationCount + " 次复检";
                    if (rollback && !dryRun && preferences.AutomaticOptimization)
                    {
                        var old = candidates.FirstOrDefault(x => x.Name == assurance.Previous);
                        CandidateScanResult oldScan = old == null ? null : scanner.ScanSelected(old, servicesToProbe);
                        if (oldScan != null && ConnectionAssurance.Passed(oldScan) &&
                            (!ConnectionAssurance.Passed(currentScan) || QualityMeasurement.ResponseMilliseconds(oldScan, 5000) < QualityMeasurement.ResponseMilliseconds(currentScan, 5000) * 0.8) &&
                            mihomo.GetSelected(config.SharedGroup) == current)
                        {
                            SelectRecorded(current, old.Name, "切换后效果不佳，旧节点复检通过，自动回退");
                            current = old.Name; currentCandidate = old; currentScan = oldScan;
                            controller.RecordSwitch();
                            assuranceDecision = "已安全回退，暂停性能寻优 30 分钟";
                        }
                        else assuranceDecision = "切换效果不佳，旧节点不满足安全回退条件；暂停寻优";
                    }
                    if (assurance.VerificationCount >= 2)
                    {
                        if (!rollback) assuranceDecision = ConnectionAssurance.Passed(currentScan) ? "切换后复检通过，保持连接" : "切换后证据不足，暂缓寻优";
                        assurance.Target = null;
                        assurance.HoldUntilUtc = clock.UtcNow.AddMinutes(30);
                    }
                }
                experienceStore.Save(experience, clock.UtcNow);
            }
            if (currentScan.Health != CandidateHealth.Unknown)
            {
                state.Records[currentScan.Name] = Record(currentScan);
                state.RememberPreferred(currentScan.Name, currentScan.Health, clock.UtcNow);
            }
            scans[currentScan.Name] = currentScan;
            scanTimes[currentScan.Name] = clock.UtcNow;
            currentEvidence = MonitorSnapshot.CreateRunning(current, currentScan, null, "当前节点检测完成", clock.UtcNow, DateTime.MaxValue)
                .WithSelectionReason(experience.SelectionSummary(current, firstCompletedCycle && current == cycleStartNode));
            Report("当前节点检测完成，正在评估稳定性", currentEvidence);
            bool trafficIdle = trafficGuard.SampleAndMayProbe(new SystemTrafficMeter());

            QualitySample priorCurrent = Latest(qualityHistory, current);
            double currentResponse = QualityMeasurement.ResponseMilliseconds(currentScan, priorCurrent == null ? 5000 : priorCurrent.ResponseMedianMs);
            if (currentScan.Health != CandidateHealth.Unknown) qualityHistory.Add(new QualitySample(currentScan.Name, clock.UtcNow, currentScan.Health == CandidateHealth.Compatible || currentScan.Health == CandidateHealth.BasicCompatible,
                currentResponse, Jitter(qualityHistory, currentScan.Name, currentResponse), priorCurrent == null ? 0 : priorCurrent.ThroughputBytesPerSecond,
                currentCandidate == null ? (double?)null : currentCandidate.Multiplier));
            experience.Observe(memoryScope, currentScan, null, clock.UtcNow);

            if (lastQualityRefreshUtc == DateTime.MinValue)
                lastQualityRefreshUtc = qualityHistory.Where(x => x.ThroughputBytesPerSecond > 0).Select(x => x.CheckedUtc).DefaultIfEmpty(DateTime.MinValue).Max();
            bool throughputDue = RefreshPolicy.ShouldRunThroughput(subscriptionChanged, currentScan.Health,
                lastQualityRefreshUtc, clock.UtcNow, config.QualityRefreshInterval);
            if (suppressedServices.Count > 0) throughputDue = false;
            bool refreshQuality = RefreshPolicy.ShouldRefreshCandidates(subscriptionChanged, currentScan.Health, throughputDue);
            bool standbyDue = clock.UtcNow - assurance.RefreshUtc >= TimeSpan.FromMinutes(5);
            refreshQuality = refreshQuality || standbyDue;
            if (observing) refreshQuality = false;
            if (serviceIncidentDecision != null && currentScan.Health == CandidateHealth.Unknown) refreshQuality = false;
            if (!trafficIdle && (currentScan.Health == CandidateHealth.Compatible || currentScan.Health == CandidateHealth.BasicCompatible)) refreshQuality = false;

            var freshlyVerified = new HashSet<string>(StringComparer.Ordinal);
            if (currentScan.Health == CandidateHealth.Compatible || currentScan.Health == CandidateHealth.BasicCompatible) freshlyVerified.Add(currentScan.Name);
            if (refreshQuality)
            {
                var delays = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (string standby in assurance.Available(candidates.Select(x => x.Name), current, clock.UtcNow))
                {
                    Report("正在复检备用节点：" + standby, currentEvidence);
                    CandidateScanResult standbyScan = scanner.ScanSelected(candidates.First(x => x.Name == standby), servicesToProbe);
                    scans[standby] = standbyScan;
                    scanTimes[standby] = clock.UtcNow;
                    if (suppressedServices.Count == 0) assurance.Remember(standbyScan, current, clock.UtcNow);
                    if (ConnectionAssurance.Passed(standbyScan)) delays[standby] = (int)Math.Min(Int32.MaxValue, QualityMeasurement.ResponseMilliseconds(standbyScan, 5000));
                }
                foreach (var remembered in experience.Recommend(memoryScope, candidates.Select(x => x.Name), clock.UtcNow).Take(3))
                {
                    Report("正在复检历史优选节点：" + remembered.Node, currentEvidence);
                    delays[remembered.Node] = mihomo.GetDelay(remembered.Node, config.DelayProbeUrl, 3000);
                }
                int batch = Math.Min(ConnectionAssurance.Passed(currentScan) && !throughputDue ? 3 : 6, candidates.Count);
                if (!ConnectionAssurance.Passed(currentScan) && delays.Count > 0) batch = 0;
                for (int i = 0; i < batch; i++)
                {
                    CandidateNode candidate = candidates[candidateOffset % candidates.Count];
                    candidateOffset = (candidateOffset + 1) % candidates.Count;
                    Report("正在筛选备用节点 " + (i + 1) + "/" + batch, currentEvidence);
                    delays[candidate.Name] = mihomo.GetDelay(candidate.Name, config.DelayProbeUrl, 3000);
                }
                if (!delays.ContainsKey(current)) delays[current] = (int)Math.Min(Int32.MaxValue, currentResponse);
                IList<CandidateNode> delayed = CandidatePreselector.Select(candidates.Where(x => delays.ContainsKey(x.Name)), delays, current,
                    StartupRecovery.MaximumCandidates(currentScan));
                var compatible = new List<CandidateNode>();
                var failureCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (CandidateNode candidate in delayed)
                {
                    if (cycleTimer.Elapsed > TimeSpan.FromSeconds(90)) break;
                    Report("正在验证备用节点：" + candidate.Name, currentEvidence);
                    CandidateScanResult scan;
                    if (!scans.TryGetValue(candidate.Name, out scan))
                    {
                        scan = scanner.ScanSelected(candidate, servicesToProbe);
                        scanTimes[candidate.Name] = clock.UtcNow;
                    }
                    scans[candidate.Name] = scan;
                    if (suppressedServices.Count == 0) assurance.Remember(scan, current, clock.UtcNow);
                    CandidateScanResult historicalScan = ServiceIncidentPolicy.AttachSuppressed(scan, suppressedServices);
                    if (historicalScan.Health != CandidateHealth.Unknown) state.Records[candidate.Name] = Record(historicalScan);
                    if (scan.Health == CandidateHealth.Compatible || scan.Health == CandidateHealth.BasicCompatible) { compatible.Add(candidate); freshlyVerified.Add(candidate.Name); }
                    QualitySample previous = Latest(qualityHistory, candidate.Name);
                    double response = QualityMeasurement.ResponseMilliseconds(scan, 5000);
                    if (suppressedServices.Count == 0 && scan.Health != CandidateHealth.Unknown) qualityHistory.Add(new QualitySample(candidate.Name, clock.UtcNow, scan.Health == CandidateHealth.Compatible || scan.Health == CandidateHealth.BasicCompatible,
                        MedianResponse(qualityHistory, candidate.Name, response), Jitter(qualityHistory, candidate.Name, response),
                        previous == null ? 0 : previous.ThroughputBytesPerSecond, candidate.Multiplier));
                    experience.Observe(memoryScope, historicalScan, null, clock.UtcNow);
                    if (scan.Health != CandidateHealth.Compatible && scan.Health != CandidateHealth.BasicCompatible)
                    {
                        string failureKey = scan.FailedService.HasValue ? scan.FailedService.Value.ToString() : scan.Health.ToString();
                        int count;
                        failureCounts.TryGetValue(failureKey, out count);
                        failureCounts[failureKey] = count + 1;
                    }
                    if (currentFailureConfirmed && compatible.Count >= 1) break;
                    if (ConnectionAssurance.Passed(currentScan) && assurance.Available(candidates.Select(x => x.Name), current, clock.UtcNow).Length >= 2) break;
                }

                string throughputStatus = "not-due";
                if (throughputDue && trafficIdle && trafficGuard.SampleAndMayProbe(new SystemTrafficMeter()))
                {
                    throughputStatus = "sampled";
                    var budget = new ThroughputBudget(5L * ThroughputProbe.SampleBytes);
                    using (var throughputProbe = new ThroughputProbe(config.ProbeProxy, config.ThroughputProbeUrl))
                    {
                        foreach (CandidateNode candidate in compatible.Take(5))
                        {
                            if (cycleTimer.Elapsed > TimeSpan.FromSeconds(110)) break;
                            Report("正在进行限量速度采样", currentEvidence);
                            if (!budget.TryReserve(ThroughputProbe.SampleBytes)) break;
                            mihomo.Select(config.ProbeGroup, candidate.Name);
                            ThroughputResult measured = throughputProbe.Probe();
                            QualitySample previous = Latest(qualityHistory, candidate.Name);
                            qualityHistory.Add(new QualitySample(candidate.Name, clock.UtcNow, true,
                                previous == null ? 5000 : previous.ResponseMedianMs,
                                previous == null ? 0 : previous.JitterMs, measured.BytesPerSecond, candidate.Multiplier));
                            speedSamples[candidate.Name] = qualityHistory[qualityHistory.Count - 1];
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
                assurance.RefreshUtc = clock.UtcNow;
            }

            qualityHistory = QualityStateStore.Bound(qualityHistory);
            var latestCohort = candidates.Select(x => Latest(qualityHistory, x.Name)).Where(x => x != null && x.Compatible && freshlyVerified.Contains(x.Name)).ToList();
            var scores = new Dictionary<string, QualityBreakdown>(StringComparer.Ordinal);
            foreach (QualitySample sample in latestCohort)
                scores[sample.Name] = QualityScorer.Score(sample, qualityHistory.Where(x => x.Name == sample.Name), latestCohort, clock.UtcNow);
            if (suppressedServices.Count > 0)
            {
                var incidentCohort = scans.Values.Where(x => freshlyVerified.Contains(x.Name) && ConnectionAssurance.Passed(x))
                    .Select(x => new QualitySample(x.Name, clock.UtcNow, true,
                        QualityMeasurement.ResponseMilliseconds(x, 5000), 0, 0,
                        candidates.First(y => y.Name == x.Name).Multiplier)).ToList();
                foreach (QualitySample sample in incidentCohort)
                    scores[sample.Name] = QualityScorer.Score(sample, new[] { sample }, incidentCohort, clock.UtcNow);
            }
            foreach (var remembered in experience.Recommend(memoryScope, scores.Keys, clock.UtcNow))
                scores[remembered.Node].Score = 0.8 * scores[remembered.Node].Score + 0.2 * remembered.Rank(clock.UtcNow);
            QualityBreakdown currentScore = null;
            var best = scores.OrderByDescending(x => x.Value.Score).FirstOrDefault();
            bool switched = false;
            string decision = serviceIncidentDecision ??
                (recoveredAfterReload ? "配置重载后恢复最近稳定节点" : "保持当前节点");
            if (!trafficIdle && serviceIncidentDecision == null) decision = "检测到较大流量，暂缓速度采样和体验优化切换";
            double? reportedScore = null;
            QualityBreakdown reportedCurrent;
            if (scores.TryGetValue(current, out reportedCurrent)) reportedScore = reportedCurrent.Score;
            if (currentScan.Health == CandidateHealth.Compatible || currentScan.Health == CandidateHealth.BasicCompatible || currentScan.Health == CandidateHealth.Unknown)
                controller.Decide(true, false, current, null);
            FailoverDecision confirmedFailure = null;
            if (currentFailureConfirmed || (currentScan.Health != CandidateHealth.Unknown &&
                !ConnectionAssurance.Passed(currentScan) && !StartupRecovery.NeedsImmediateConfirmation(currentScan)))
            {
                IEnumerable<NodeHealthRecord> decisionCandidates = suppressedServices.Count == 0
                    ? state.Records.Values.Where(x => freshlyVerified.Contains(x.Name))
                    : scans.Values.Where(x => freshlyVerified.Contains(x.Name) && ConnectionAssurance.Passed(x)).Select(Record);
                confirmedFailure = controller.Decide(false, false, current, decisionCandidates);
            }
            if (!observing && currentScan.Health != CandidateHealth.Unknown && !String.IsNullOrEmpty(best.Key) && best.Key != current)
            {
                bool currentCompatible = (currentScan.Health == CandidateHealth.Compatible || currentScan.Health == CandidateHealth.BasicCompatible) && scores.TryGetValue(current, out currentScore);
                bool shouldSwitch;
                string reason;
                if (currentCompatible)
                {
                    CandidateScanResult bestScan;
                    scans.TryGetValue(best.Key, out bestScan);
                    FailoverDecision qualityDecision = controller.DecideQuality(currentScore.Score, best.Value.Score, false,
                        freshlyVerified.Contains(best.Key), experience.IsProvenStable(memoryScope, best.Key, clock.UtcNow),
                        experience.RecentResponses(memoryScope, current, 3), experience.RecentResponses(memoryScope, best.Key, 5), bestScan);
                    shouldSwitch = qualityDecision.ShouldSwitch;
                    reason = qualityDecision.Reason;
                }
                else
                {
                    NodeHealthRecord bestRecord;
                    state.Records.TryGetValue(best.Key, out bestRecord);
                    FailoverDecision failureDecision = confirmedFailure;
                    shouldSwitch = failureDecision != null && failureDecision.ShouldSwitch && freshlyVerified.Contains(best.Key);
                    reason = failureDecision == null ? "awaiting confirmation" : failureDecision.Reason;
                }
                decision = reason;
                if (currentCompatible && clock.UtcNow < assurance.HoldUntilUtc) { shouldSwitch = false; decision = "处于切换观察后的稳定期，保持连接"; }
                if (currentCompatible && !trafficIdle) { shouldSwitch = false; decision = "检测到较大流量，保持当前节点"; }
                if (shouldSwitch && !dryRun && preferences.AutomaticOptimization)
                {
                    DateTime verifiedUtc;
                    CandidateScanResult finalScan;
                    if (!scans.TryGetValue(best.Key, out finalScan) || !scanTimes.TryGetValue(best.Key, out verifiedUtc) ||
                        StartupRecovery.RequiresFinalRecheck(clock.UtcNow - verifiedUtc))
                    {
                        Report("切换前正在复检目标节点：" + best.Key, currentEvidence);
                        finalScan = scanner.ScanSelected(candidates.First(x => x.Name == best.Key), servicesToProbe);
                        scans[best.Key] = finalScan;
                        scanTimes[best.Key] = clock.UtcNow;
                    }
                    shouldSwitch = finalScan.Health == CandidateHealth.Compatible || finalScan.Health == CandidateHealth.BasicCompatible;
                    if (!shouldSwitch) decision = "目标节点复检未通过，保留当前连接";
                }
                CheckStop();
                if (shouldSwitch && !dryRun && preferences.AutomaticOptimization && String.Equals(mihomo.GetSelected(config.SharedGroup), current, StringComparison.Ordinal))
                {
                    SelectRecorded(current, best.Key, "自动切换：" + StatusReport.DecisionText(reason));
                    assurance.Begin(current, best.Key, currentResponse, currentCompatible);
                    experienceStore.Save(experience, clock.UtcNow);
                    previousSelectedNode = current;
                    controller.RecordSwitch();
                    switched = true;
                    reportedScore = suppressedServices.Count == 0 ? (double?)best.Value.Score : null;
                    decision = "已切换：" + reason;
                    logger.Write("switched node=" + SafeName(best.Key) + " score=" + best.Value.Score.ToString("F1") + " reason=" + reason);
                }
            }
            if (dryRun) logger.Write("dry-run quality evaluation completed; shared selector unchanged");
            else if (!switched && currentScan.Health == CandidateHealth.Unknown && serviceIncidentDecision == null)
            {
                decision = "证据不足，保留当前节点";
                logger.Write("待验证，保留当前连接；" + currentScan.Detail);
            }
            else if (!switched && currentScan.Health != CandidateHealth.Compatible && currentScan.Health != CandidateHealth.BasicCompatible) logger.Write("current check failed " + currentScan.Health + "; awaiting safe replacement");
            string actual = dryRun ? current : mihomo.GetSelected(config.SharedGroup);
            if (assuranceDecision != null) decision = assuranceDecision;
            CandidateScanResult actualScan;
            if (!scans.TryGetValue(actual, out actualScan)) actualScan =
                new CandidateScanResult(actual, CandidateHealth.Unknown, null, "节点发生变化，等待下一轮验证");
            actualScan = ServiceIncidentPolicy.AttachSuppressed(actualScan, suppressedServices);
            if (actual != current && !switched) reportedScore = null;
            experience.RecordChange(experience.LastNode, actual, "检测期间发现外部选择变更", clock.UtcNow);
            foreach (var scan in scans.Values)
            {
                QualitySample speed;
                speedSamples.TryGetValue(scan.Name, out speed);
                experience.Observe(memoryScope, ServiceIncidentPolicy.AttachSuppressed(scan, suppressedServices), speed, clock.UtcNow);
            }
            TimeSpan nextInterval = assurance.Interval(actualScan);
            decision = StatusReport.DecisionText(decision) + " · 近期备用 " + assurance.Available(candidates.Select(x => x.Name), actual, clock.UtcNow).Length + "/2";
            experienceStore.Save(experience, clock.UtcNow);
            if (!dryRun)
            {
                CandidateHealth status = actualScan.Health;
                string detail = actualScan.Detail;
                string text = StatusReport.Format(clock.UtcNow, actual, status, reportedScore, decision, detail);
                StatusReport.WriteAtomic(Path.Combine(config.RootPath, "current-status.txt"), text);
            }
            store.Save(state, candidates.Select(x => x.Name));
            qualityStore.Save(qualityHistory);
            string selectionSummary = experience.SelectionSummary(actual, firstCompletedCycle && actual == cycleStartNode && !switched);
            firstCompletedCycle = false;
            return MonitorSnapshot.CreateRunning(actual, actualScan, reportedScore, decision, clock.UtcNow,
                clock.UtcNow.Add(nextInterval)).WithSelectionReason(selectionSummary);
        }
        finally
        {
            cycleTimer = null;
            System.Threading.Volatile.Write(ref running, 0);
            MemoryTrimmer.TrimIdleWorkingSet();
        }
    }

    private void SelectRecorded(string from, string to, string reason)
    {
        CheckStop();
        mihomo.Select(config.SharedGroup, to);
        if (mihomo.GetSelected(config.SharedGroup) != to) throw new InvalidOperationException("节点切换未确认，未写入成功记录");
        RunStatistics.SelectionConfirmed();
        experience.RecordChange(from, to, reason, clock.UtcNow);
        experienceStore.Save(experience, clock.UtcNow);
    }

    private NodeHealthRecord Record(CandidateScanResult result)
    {
        return new NodeHealthRecord(result.Name, result.Health, clock.UtcNow, HealthPolicy.CooldownUntil(result.Health, clock.UtcNow), false);
    }

    private static string Fingerprint(IEnumerable<CandidateNode> candidates)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(string.Join("\n", candidates.Select(x => x.Name).Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)));
        using (SHA256 hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(bytes));
    }

    private static string SafeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "(none)";
        using (SHA256 hash = SHA256.Create())
            return "node-" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(name)), 0, 4).Replace("-", "").ToLowerInvariant();
    }

    private static string ServiceIncidentText(IEnumerable<ServiceKind> services)
    {
        return String.Join("、", (services ?? Enumerable.Empty<ServiceKind>()).Distinct()
            .Select(MonitorPresentation.ServiceLabel));
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
