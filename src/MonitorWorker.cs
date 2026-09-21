using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

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

public sealed class MonitorWorker : IRestorableCycleRunner, IProgressCycleRunner, IAccountVerificationRunner,
    ITriggeredCycleRunner
{
    private readonly MonitorConfiguration config;
    private readonly IMihomoClient mihomo;
    private readonly CompatibilityScanner scanner;
    private readonly BoundedLogger logger;
    private readonly IClock clock;
    private readonly FailoverController controller;
    private readonly TrafficGuard trafficGuard;
    private readonly IProxyPathHealthChecker pathHealthChecker;
    private readonly Func<RuntimeSnapshot> runtimeSnapshotProvider;
    private DateTime lastQualityRefreshUtc = DateTime.MinValue;
    private int running;
    private int accountCommandRunning;
    private string previousSelectedNode;
    private Stopwatch cycleTimer;
    private int candidateOffset;
    private UserPreferences lastPreferences = UserPreferences.Defaults();
    private readonly ExperienceStore experienceStore;
    private ExperienceData experience;
    private readonly RegionEligibilityStore regionEligibilityStore;
    private readonly RegionEligibilityCache regionEligibility;
    private bool firstCompletedCycle = true;
    public event Action<MonitorSnapshot> Progress;
    public Func<bool> ShouldStop { get; set; }
    private bool StopRequired() { return System.Threading.Volatile.Read(ref accountCommandRunning) == 0 &&
        ((ShouldStop != null && ShouldStop()) || (cycleTimer != null && cycleTimer.Elapsed > TimeSpan.FromMinutes(3))); }
    private void CheckStop() { if (StopRequired()) throw new OperationCanceledException("检测已暂停或达到本轮时间预算，将在下一轮继续"); }
    private void Report(string stage, MonitorSnapshot evidence)
    {
        CheckStop();
        var handler = Progress;
        if (handler != null) handler(evidence == null
            ? MonitorSnapshot.CreateState(MonitorRunState.Checking, stage, DateTime.MinValue, DateTime.MaxValue)
            : evidence.WithProgress(stage));
    }

    public MonitorWorker(MonitorConfiguration config, IMihomoClient mihomo, IServiceProbe probe, BoundedLogger logger, IClock clock,
        IExitIdentityProbe exitIdentityProbe = null, IProxyPathHealthChecker pathHealthChecker = null)
        : this(config, mihomo, probe, logger, clock, exitIdentityProbe, pathHealthChecker, null)
    {
    }

    internal MonitorWorker(MonitorConfiguration config, IMihomoClient mihomo, IServiceProbe probe,
        BoundedLogger logger, IClock clock, IExitIdentityProbe exitIdentityProbe,
        IProxyPathHealthChecker pathHealthChecker, Func<RuntimeSnapshot> runtimeSnapshotProvider)
    {
        this.config = config;
        this.mihomo = mihomo;
        this.scanner = new CompatibilityScanner(mihomo, probe, config.ProbeGroup, exitIdentityProbe);
        scanner.ShouldStop = StopRequired;
        this.logger = logger;
        this.clock = clock;
        this.pathHealthChecker = pathHealthChecker;
        this.runtimeSnapshotProvider = runtimeSnapshotProvider ?? delegate { return RuntimeInspector.Capture(mihomo); };
        experienceStore = new ExperienceStore(Path.Combine(config.RootPath, "state", "experience.json"));
        experience = experienceStore.Load();
        regionEligibilityStore = new RegionEligibilityStore(
            Path.Combine(config.RootPath, "state", "region-eligibility.json"));
        regionEligibility = regionEligibilityStore.Load();
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
        return Run(preferences, MonitorCycleTrigger.Scheduled);
    }

    public MonitorSnapshot Run(UserPreferences preferences, MonitorCycleTrigger trigger)
    {
        logger.Write("cycle trigger=" + trigger.ToString().ToLowerInvariant());
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
        if (!ServiceEvidencePolicy.CanEmergencySwitch(verified)) return false;
        CheckStop();
        if (mihomo.GetSelected(config.SharedGroup) != current) return false;
        SelectRecorded(current, target, "用户恢复上一个节点（已复检）");
        ConnectionAssurance assurance = experience.Assurance;
        assurance.EnsureDecisionState(target, clock.UtcNow);
        ApplyDecision(assurance, AutomaticDecisionEvent.ManualNodeChanged,
            new AutomaticDecisionContext { NowUtc = clock.UtcNow, Current = target,
                ExpiresUtc = clock.UtcNow.AddMinutes(10),
                Reason = "user restored previous node" });
        previousSelectedNode = current;
        controller.RecordSwitch();
        logger.Write("restored previous node=" + SafeName(target));
        return true;
    }

    public bool RecordBrowserConversationProof(string node, string exitFingerprint,
        ServiceKind service, DateTime verifiedUtc, int protocolVersion)
    {
        if ((service != ServiceKind.ChatGPT && service != ServiceKind.Gemini) ||
            protocolVersion != BrowserConversationProof.CurrentProtocolVersion ||
            String.IsNullOrWhiteSpace(node) || String.IsNullOrWhiteSpace(exitFingerprint) ||
            !String.Equals(mihomo.GetSelected(config.SharedGroup), node, StringComparison.Ordinal) ||
            String.IsNullOrWhiteSpace(experience.ActiveScope)) return false;
        System.Threading.Interlocked.Exchange(ref accountCommandRunning, 1);
        Stopwatch previousTimer = cycleTimer;
        cycleTimer = Stopwatch.StartNew();
        try
        {
            CandidateScanResult fresh = scanner.ScanSelected(new CandidateNode(node, null), new[] { service });
            ProbeResult evidence;
            if (!fresh.ServiceResults.TryGetValue(service, out evidence) || !evidence.Passed ||
                evidence.FailureKind != ProbeFailureKind.None ||
                !String.Equals(fresh.ExitFingerprint, exitFingerprint, StringComparison.Ordinal) ||
                !String.Equals(mihomo.GetSelected(config.SharedGroup), node, StringComparison.Ordinal)) return false;
            AccountVerificationMemory.MarkBrowserConversation(experience, experience.ActiveScope, node,
                fresh.ExitFingerprint, service, verifiedUtc, protocolVersion);
            experienceStore.Save(experience, verifiedUtc);
            logger.Write("browser conversation proof recorded node=" + SafeName(node) + " service=" + service);
            return true;
        }
        finally
        {
            cycleTimer = previousTimer;
            System.Threading.Volatile.Write(ref accountCommandRunning, 0);
        }
    }

    public void ReportServiceFailure(string node, ServiceKind service, DateTime reportedUtc)
    {
        if (service != ServiceKind.ChatGPT && service != ServiceKind.Gemini) return;
        AccountVerificationMemory.Revoke(experience, experience.ActiveScope, node, service,
            reportedUtc, "user reported failure");
        logger.Write("user reported service failure node=" + SafeName(node) + " service=" + service);
        ConnectionAssurance assurance = experience.Assurance;
        string current = mihomo.GetSelected(config.SharedGroup);
        if (String.Equals(current, node, StringComparison.Ordinal) && assurance != null &&
            assurance.CanUserFeedbackRollback(current, clock.UtcNow) && assurance.Previous != current &&
            mihomo.GetChoices(config.SharedGroup).Contains(assurance.Previous, StringComparer.Ordinal))
        {
            string previous = assurance.Previous;
            System.Threading.Interlocked.Exchange(ref accountCommandRunning, 1);
            Stopwatch previousTimer = cycleTimer;
            cycleTimer = Stopwatch.StartNew();
            try
            {
                CandidateScanResult verified = scanner.ScanSelected(new CandidateNode(previous, null), lastPreferences.RequiredServices);
                if (ServiceEvidencePolicy.CanEmergencySwitch(verified) && mihomo.GetSelected(config.SharedGroup) == current &&
                    assurance.CanUserFeedbackRollback(current, clock.UtcNow))
                {
                    DecisionTransition authorization = ApplyDecision(assurance,
                        AutomaticDecisionEvent.RollbackRequired,
                        new AutomaticDecisionContext { NowUtc = clock.UtcNow,
                            Current = current, Previous = current, Target = previous,
                            Reason = "user feedback rollback verified" });
                    if (ApplyAutomaticSwitch(assurance, authorization, current, previous,
                        "用户反馈 AI 服务失败，旧节点复检通过，自动回退",
                        assurance.PreviousResponse, false, false))
                    {
                        ApplyDecision(assurance, AutomaticDecisionEvent.ObservationComplete,
                            new AutomaticDecisionContext { NowUtc = clock.UtcNow,
                                Current = previous, ExpiresUtc = clock.UtcNow.AddMinutes(30),
                                Reason = "user feedback rollback completed" });
                        assurance.Target = null;
                        assurance.HoldUntilUtc = clock.UtcNow.AddMinutes(30);
                        logger.Write("user feedback rollback node=" + SafeName(previous));
                    }
                }
            }
            finally
            {
                cycleTimer = previousTimer;
                System.Threading.Volatile.Write(ref accountCommandRunning, 0);
            }
        }
        experienceStore.Save(experience, reportedUtc);
    }

    public void ReportBrowserConversationFailure(string node, string exitFingerprint, ServiceKind service,
        BrowserVerificationOutcome outcome, bool messageSent, DateTime reportedUtc)
    {
        if (service != ServiceKind.ChatGPT && service != ServiceKind.Gemini) return;
        AccountVerificationMemory.RevokeBrowserFailure(experience, experience.ActiveScope, node,
            exitFingerprint, service, reportedUtc);
        logger.Write("browser conversation failure node=" + SafeName(node) + " service=" + service +
            " outcome=" + outcome + " message_sent=" + messageSent.ToString().ToLowerInvariant());
        ConnectionAssurance assurance = experience.Assurance;
        string current = mihomo.GetSelected(config.SharedGroup);
        if (String.Equals(current, node, StringComparison.Ordinal) &&
            !String.IsNullOrWhiteSpace(exitFingerprint) && assurance != null &&
            assurance.ShouldRecheckPreviousAfterBrowserResult(current, outcome, messageSent, clock.UtcNow) &&
            assurance.Previous != current &&
            mihomo.GetChoices(config.SharedGroup).Contains(assurance.Previous, StringComparer.Ordinal))
        {
            string previous = assurance.Previous;
            System.Threading.Interlocked.Exchange(ref accountCommandRunning, 1);
            Stopwatch previousTimer = cycleTimer;
            cycleTimer = Stopwatch.StartNew();
            try
            {
                CandidateScanResult currentCheck = scanner.ScanSelected(new CandidateNode(current, null),
                    new[] { service });
                if (String.Equals(currentCheck.ExitFingerprint, exitFingerprint, StringComparison.Ordinal))
                {
                    CandidateScanResult verified = scanner.ScanSelected(new CandidateNode(previous, null),
                        lastPreferences.RequiredServices);
                    if (ServiceEvidencePolicy.CanEmergencySwitch(verified))
                    {
                        CandidateScanResult currentAfter = scanner.ScanSelected(new CandidateNode(current, null),
                            new[] { service });
                        if (String.Equals(currentAfter.ExitFingerprint, exitFingerprint, StringComparison.Ordinal) &&
                            mihomo.GetSelected(config.SharedGroup) == current &&
                            assurance.ShouldRecheckPreviousAfterBrowserResult(current, outcome, messageSent, clock.UtcNow))
                        {
                            DecisionTransition authorization = ApplyDecision(assurance,
                                AutomaticDecisionEvent.RollbackRequired,
                                new AutomaticDecisionContext { NowUtc = clock.UtcNow,
                                    Current = current, Previous = current, Target = previous,
                                    Reason = "browser proof rollback verified" });
                            if (ApplyAutomaticSwitch(assurance, authorization, current, previous,
                                "浏览器真实对话失败，旧节点复检通过，自动回退",
                                assurance.PreviousResponse, false, false))
                            {
                                ApplyDecision(assurance, AutomaticDecisionEvent.ObservationComplete,
                                    new AutomaticDecisionContext { NowUtc = clock.UtcNow,
                                        Current = previous, ExpiresUtc = clock.UtcNow.AddMinutes(30),
                                        Reason = "browser proof rollback completed" });
                                assurance.Target = null;
                                assurance.HoldUntilUtc = clock.UtcNow.AddMinutes(30);
                                logger.Write("browser proof rollback node=" + SafeName(previous));
                            }
                        }
                    }
                }
            }
            finally
            {
                cycleTimer = previousTimer;
                System.Threading.Volatile.Write(ref accountCommandRunning, 0);
            }
        }
        experienceStore.Save(experience, reportedUtc);
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
            RuntimeSnapshot runtime = runtimeSnapshotProvider();
            ConflictResult conflict = new ConflictDetector().Evaluate(runtime);
            if (conflict.Paused)
            {
                logger.Write("paused-conflict " + conflict.Reason);
                return MonitorSnapshot.CreateState(MonitorRunState.Degraded, conflict.Reason, clock.UtcNow,
                    clock.UtcNow.Add(config.CycleInterval));
            }
            string ipv6Action = RuntimeConfigurationPolicy.Ipv6Action(mihomo.IsRuntimeIpv6Enabled());
            if (ipv6Action != null)
            {
                logger.Write("runtime IPv6 enabled; waiting for enhancement script reapply");
                return MonitorSnapshot.CreateState(MonitorRunState.Degraded, ipv6Action, clock.UtcNow,
                    clock.UtcNow.Add(config.CycleInterval));
            }
            IDictionary<string, string> runtimeTypes = null;
            var catalogClient = mihomo as MihomoPipeClient;
            if (catalogClient != null)
            {
                try { runtimeTypes = catalogClient.GetProxyTypes(); }
                catch (IOException ex) { logger.Write("runtime proxy kinds unavailable " + ex.GetType().Name); }
                catch (TimeoutException ex) { logger.Write("runtime proxy kinds unavailable " + ex.GetType().Name); }
                catch (ArgumentException ex) { logger.Write("runtime proxy kinds unavailable " + ex.GetType().Name); }
            }
            IList<CandidateNode> candidates = CandidateCatalog.Filter(mihomo.GetChoices(config.SharedGroup), runtimeTypes);
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
            string previouslyObservedNode = experience.LastNode;
            assurance.EnsureDecisionState(current, clock.UtcNow);
            if (!String.IsNullOrEmpty(previouslyObservedNode) &&
                !String.Equals(previouslyObservedNode, current, StringComparison.Ordinal))
            {
                ApplyDecision(assurance, AutomaticDecisionEvent.ManualNodeChanged,
                    new AutomaticDecisionContext { NowUtc = clock.UtcNow, Current = current,
                        ExpiresUtc = clock.UtcNow.AddMinutes(10), Reason = "external selector change" });
                assurance.Target = null;
                assurance.Previous = null;
                assurance.PendingOptimization = null;
                assurance.HoldUntilUtc = clock.UtcNow.AddMinutes(10);
            }
            ProxyPathHealth pathHealth = null;
            string cycleStartNode = current;
            experience.RecordChange(experience.LastNode, current, "检测到外部变更（Clash 手动选择或核心重载）", clock.UtcNow);
            experienceStore.Save(experience, clock.UtcNow);
            Report("正在检测当前节点：" + current, null);
            CandidateNode currentCandidate = candidates.FirstOrDefault(x => x.Name == current);
            CandidateScanResult currentScan = currentCandidate == null ? new CandidateScanResult(current ?? "", CandidateHealth.Transient, null, "current not eligible") : scanner.ScanSelected(currentCandidate, requiredServices);
            RememberRegionEligibility(memoryScope, currentScan);
            currentScan = ServiceIncidentPolicy.AttachSuppressed(currentScan, suppressedServices);
            bool selectorAlignmentChanged = !dryRun && FollowGeneralNode(currentScan);
            if (ProxyPathHealthPolicy.ShouldCheck(selectorAlignmentChanged) && pathHealthChecker != null && runtime.SystemProxy.Length > 0)
            {
                pathHealth = pathHealthChecker.Check();
                if (pathHealth.Mismatch)
                    logger.Write("proxy path mismatch after selector alignment probe=" + pathHealth.ProbeReachable +
                        " system=" + pathHealth.SystemReachable);
            }
            AccountVerificationMemory.RevokeForChangedExit(experience, memoryScope, current,
                currentScan.ExitFingerprint, clock.UtcNow);
            logger.Write("current check completed elapsed_seconds=" + cycleTimer.Elapsed.TotalSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                " health=" + currentScan.Health);
            string assuranceDecision = null;
            bool observing = assurance.Decision.State == AutomaticDecisionState.Observing;
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
            bool fastSwitched = false;
            bool currentRecoveredOnRetry = false;
            ServiceKind? fastFailoverService = StartupRecovery.FastFailoverService(currentScan);
            if (currentCandidate != null && fastFailoverService.HasValue)
            {
                ServiceKind failedService = fastFailoverService.Value;
                bool severeLatency = !currentScan.FailedService.HasValue;
                string failedNode = current;
                double failedNodeResponse = QualityMeasurement.ResponseMilliseconds(currentScan, 5000);
                if (!observing) controller.Decide(false, false, current, null);
                bool repeatCurrent = StartupRecovery.RequiresRepeatConfirmation(currentScan);
                logger.Write("current degradation node=" + SafeName(current) + " service=" + failedService +
                    " health=" + currentScan.Health + " reason=" + (severeLatency ? "severe-latency" : "service-failure") +
                    " action=" + (repeatCurrent ? "confirm" : "fast-failover"));
                CandidateScanResult confirmation = currentScan;
                if (repeatCurrent)
                {
                    Report("当前节点出现超时，正在快速确认 " + MonitorPresentation.ServiceLabel(failedService), currentEvidence);
                    confirmation = scanner.ScanSelected(currentCandidate, new[] { failedService });
                    RememberRegionEligibility(memoryScope, confirmation);
                }
                currentFailureConfirmed = severeLatency
                    ? StartupRecovery.ConfirmsSevereLatency(confirmation, failedService)
                    : confirmation.Health != CandidateHealth.Unknown && !ConnectionAssurance.Passed(confirmation);
                DecisionEvidenceClass evidenceClass = DecisionEvidencePolicy.Classify(
                    confirmation, severeLatency && currentFailureConfirmed);
                logger.Write("current failure confirmation node=" + SafeName(current) + " service=" + failedService +
                    " health=" + confirmation.Health + " confirmed=" + currentFailureConfirmed.ToString().ToLowerInvariant() +
                    " evidence=" + evidenceClass);
                if (currentFailureConfirmed)
                {
                    assurance.ClearPendingOptimization();
                    if (evidenceClass == DecisionEvidenceClass.HardFailure)
                    {
                        ApplyDecision(assurance, AutomaticDecisionEvent.HardFailure,
                            new AutomaticDecisionContext { NowUtc = clock.UtcNow, Current = current,
                                Service = failedService, Evidence = evidenceClass,
                                Reason = "confirmed hard failure" });
                        ApplyDecision(assurance, AutomaticDecisionEvent.FailureConfirmed,
                            new AutomaticDecisionContext { NowUtc = clock.UtcNow, Current = current,
                                Service = failedService, Evidence = evidenceClass });
                    }
                    else
                    {
                        ApplyDecision(assurance, AutomaticDecisionEvent.SevereDegradation,
                            new AutomaticDecisionContext { NowUtc = clock.UtcNow, Current = current,
                                Service = failedService, Evidence = evidenceClass,
                                Reason = "confirmed severe degradation" });
                    }
                    var incidentChecks = new List<CandidateScanResult>();
                    List<CandidateNode> delayPool = candidates.Where(x => x.Name != current).ToList();
                    Stopwatch delayTimer = Stopwatch.StartNew();
                    Dictionary<string, int> rescueDelays = MeasureLiveDelays(delayPool);
                    delayTimer.Stop();
                    string[] liveRanked = StartupRecovery.RankFastCandidates(
                        delayPool, rescueDelays, current, Int32.MaxValue);
                    ServiceKind[] fastServices = StartupRecovery.FastProbeServices(requiredServices, failedService);
                    ServiceKind[] fullFailoverServices = StartupRecovery.FullFailoverProbeServices(
                        requiredServices, failedService);
                    var quickScans = new List<CandidateScanResult>();
                    int comparedCandidates = 0;
                    foreach (string standby in liveRanked)
                    {
                        if (StartupRecovery.ShouldStopAfterCheckedCandidates(comparedCandidates)) break;
                        Report("正在比较低延迟候选：" + standby, currentEvidence);
                        CandidateNode standbyCandidate = candidates.FirstOrDefault(x => x.Name == standby);
                        if (standbyCandidate != null)
                        {
                            CandidateScanResult quickScan = scanner.ScanSelected(standbyCandidate, fastServices,
                                TimeSpan.FromSeconds(2));
                            RememberRegionEligibility(memoryScope, quickScan);
                            incidentChecks.Add(quickScan);
                            scans[standby] = quickScan;
                            scanTimes[standby] = clock.UtcNow;
                            if (StartupRecovery.IsEligibleQuickScan(quickScan, failedService)) quickScans.Add(quickScan);
                            comparedCandidates++;
                            if (StartupRecovery.ShouldStopAfterEligibleCandidates(quickScans.Count)) break;
                        }
                    }
                    string[] verifiedTargets = StartupRecovery.RankVerifiedFastTargets(quickScans, rescueDelays, failedService);
                    double selectedResponseForLog = -1;
                    var fullValidationAttempts = new HashSet<string>(StringComparer.Ordinal);
                    while (true)
                    {
                        string standby = StartupRecovery.NextFullValidationTarget(
                            verifiedTargets, fullValidationAttempts);
                        if (standby == null) break;
                        fullValidationAttempts.Add(standby);
                        CandidateNode standbyCandidate = candidates.First(x => x.Name == standby);
                        Report("候选正在完成切换前验证：" + standby, currentEvidence);
                        CandidateScanResult replacementScan = scanner.ScanSelected(
                            standbyCandidate, fullFailoverServices);
                        RememberRegionEligibility(memoryScope, replacementScan);
                        scans[standby] = replacementScan;
                        scanTimes[standby] = clock.UtcNow;
                        if (!StartupRecovery.IsEligibleQuickScan(replacementScan, failedService)) continue;
                        if (dryRun || !String.Equals(mihomo.GetSelected(config.SharedGroup), failedNode, StringComparison.Ordinal)) continue;

                        if (!severeLatency && (failedService == ServiceKind.ChatGPT || failedService == ServiceKind.Gemini))
                            AccountVerificationMemory.Revoke(experience, memoryScope, failedNode, failedService,
                                clock.UtcNow, "confirmed service failure");
                        bool provisional = !ServiceEvidencePolicy.CanEmergencySwitch(replacementScan);
                        double selectedResponse = QualityMeasurement.ResponseMilliseconds(replacementScan, 5000);
                        selectedResponseForLog = selectedResponse;
                        if (severeLatency)
                        {
                            ProbeResult slowResult;
                            double severeBaseline = confirmation.ServiceResults.TryGetValue(failedService, out slowResult) &&
                                slowResult != null ? slowResult.ElapsedMilliseconds : failedNodeResponse;
                            MaterialImprovementDecision improvement = MaterialImprovementPolicy.Evaluate(
                                severeBaseline, selectedResponse,
                                assurance.HasRecentAutomaticSwitch(clock.UtcNow));
                            logger.Write("severe degradation target=" + SafeName(standby) +
                                " baseline_ms=" + severeBaseline.ToString("F0") +
                                " target_ms=" + selectedResponse.ToString("F0") +
                                " relative=" + improvement.RelativeImprovement.ToString("P1") +
                                " absolute_ms=" + improvement.AbsoluteImprovementMilliseconds.ToString("F0") +
                                " required_relative=" + improvement.RequiredRelativeImprovement.ToString("P0") +
                                " required_absolute_ms=" + improvement.RequiredAbsoluteImprovementMilliseconds.ToString("F0") +
                                " accepted=" + improvement.Accepted.ToString().ToLowerInvariant() +
                                " reason=" + improvement.Reason);
                            if (!improvement.Accepted) break;
                            SwitchBudgetDecision budget = assurance.AutomaticSwitchBudget(clock.UtcNow);
                            if (!budget.Allowed)
                            {
                                ApplyDecision(assurance, AutomaticDecisionEvent.BudgetExhausted,
                                    new AutomaticDecisionContext { NowUtc = clock.UtcNow,
                                        Current = current, ExpiresUtc = budget.AllowedAtUtc,
                                        Evidence = evidenceClass, Reason = budget.Reason });
                                assuranceDecision = "自动切换次数达到预算，进入稳定观察";
                                break;
                            }
                        }
                        DecisionTransition switchTransition = ApplyDecision(assurance,
                            AutomaticDecisionEvent.RecoveryTargetReady,
                            new AutomaticDecisionContext { NowUtc = clock.UtcNow,
                                Current = failedNode, Previous = failedNode, Target = standby,
                                Service = failedService, Evidence = evidenceClass,
                                BaselineResponse = failedNodeResponse,
                                TargetResponse = selectedResponse });
                        string fastSelectionSummary = StartupRecovery.FastSelectionSummary(selectedResponse);
                        string fastReason = severeLatency ? "当前节点单项响应超过 2000 ms，" + fastSelectionSummary + "完整验证通过" :
                            "当前节点故障，" + fastSelectionSummary + "完整验证通过，快速切换";
                        if (!ApplyAutomaticSwitch(assurance, switchTransition, failedNode,
                            standby, fastReason, failedNodeResponse, false, provisional)) continue;
                        current = standby;
                        currentCandidate = standbyCandidate;
                        currentScan = replacementScan;
                        pathHealth = null;
                        currentFailureConfirmed = false;
                        fastSwitched = true;
                        observing = true;
                        assuranceDecision = severeLatency ? "当前节点单项响应过慢，已切换到" + fastSelectionSummary :
                            "当前节点故障，已切换到" + fastSelectionSummary;
                        logger.Write("fast failover switched from=" + SafeName(failedNode) + " to=" + SafeName(standby) +
                            " failed_service=" + failedService + " target_health=" + replacementScan.Health +
                            " response_ms=" + selectedResponse.ToString("F0"));
                        break;
                    }
                    if (!fastSwitched && severeLatency &&
                        assurance.Decision.State != AutomaticDecisionState.Stabilization)
                    {
                        ApplyDecision(assurance, AutomaticDecisionEvent.NoRecoveryTarget,
                            new AutomaticDecisionContext { NowUtc = clock.UtcNow, Current = current,
                                Evidence = DecisionEvidenceClass.SevereDegradation,
                                Reason = "no materially better severe-degradation target" });
                    }
                    logger.Write("fast selection delay_ms=" + delayTimer.ElapsedMilliseconds +
                        " checked=" + comparedCandidates + " eligible=" + verifiedTargets.Length +
                        " selected_response_ms=" + selectedResponseForLog.ToString("F0",
                            System.Globalization.CultureInfo.InvariantCulture));
                    if (!fastSwitched && !severeLatency && ServiceIncidentPolicy.HasConsensus(confirmation, incidentChecks, failedService))
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
                else
                {
                    ApplyDecision(assurance, AutomaticDecisionEvent.FailureRecovered,
                        new AutomaticDecisionContext { NowUtc = clock.UtcNow, Current = current,
                            Evidence = DecisionEvidenceClass.Healthy,
                            Reason = "focused confirmation recovered" });
                    currentScan = StartupRecovery.ApplySuccessfulConfirmation(currentScan, confirmation, failedService);
                    scans[currentScan.Name] = currentScan;
                    scanTimes[currentScan.Name] = clock.UtcNow;
                    currentRecoveredOnRetry = true;
                    logger.Write("current retry recovered node=" + SafeName(current) + " service=" + failedService +
                        " health=" + currentScan.Health);
                    if (!observing) controller.Decide(true, false, current, null);
                }
                if (currentFailureConfirmed && (failedService == ServiceKind.ChatGPT || failedService == ServiceKind.Gemini))
                    AccountVerificationMemory.Revoke(experience, memoryScope, current, failedService,
                        clock.UtcNow, "confirmed service failure");
            }
            if (observing && !fastSwitched && serviceIncidentDecision != null)
            {
                assurance.Target = null;
                assurance.HoldUntilUtc = experience.ServiceIncidents.Where(x => x != null &&
                    suppressedServices.Contains(x.Service)).Select(x => x.UntilUtc).DefaultIfEmpty(clock.UtcNow.AddMinutes(10)).Max();
                ApplyDecision(assurance, AutomaticDecisionEvent.OutageDetected,
                    new AutomaticDecisionContext { NowUtc = clock.UtcNow, Current = current,
                        ExpiresUtc = assurance.HoldUntilUtc,
                        Reason = "service incident interrupted observation" });
                observing = false;
                assuranceDecision = serviceIncidentDecision;
            }
            if (observing && !fastSwitched)
            {
                if (assurance.Target != current)
                {
                    ApplyDecision(assurance, AutomaticDecisionEvent.ManualNodeChanged,
                        new AutomaticDecisionContext { NowUtc = clock.UtcNow, Current = current,
                            ExpiresUtc = clock.UtcNow.AddMinutes(10),
                            Reason = "selector changed during observation" });
                    assurance.Target = null;
                    assurance.HoldUntilUtc = clock.UtcNow.AddMinutes(10);
                    assuranceDecision = "检测到外部切换，取消自动回退";
                }
                else
                {
                    bool rollback = assurance.NeedsRollback(currentScan);
                    assuranceDecision = "切换后观察：第 " + assurance.VerificationCount + " 次复检";
                    if (rollback && !dryRun && (pathHealth == null || pathHealth.CanAutoSwitch))
                    {
                        var old = candidates.FirstOrDefault(x => x.Name == assurance.Previous);
                        CandidateScanResult oldScan = old == null ? null : scanner.ScanSelected(old, requiredServices);
                        RememberRegionEligibility(memoryScope, oldScan);
                        if (ServiceEvidencePolicy.CanEmergencySwitch(oldScan) &&
                            (!ConnectionAssurance.Passed(currentScan) || QualityMeasurement.ResponseMilliseconds(oldScan, 5000) < QualityMeasurement.ResponseMilliseconds(currentScan, 5000) * 0.8) &&
                            mihomo.GetSelected(config.SharedGroup) == current)
                        {
                            DecisionTransition rollbackAuthorization = ApplyDecision(assurance,
                                AutomaticDecisionEvent.RollbackRequired,
                                new AutomaticDecisionContext { NowUtc = clock.UtcNow,
                                    Current = current, Previous = current, Target = old.Name,
                                    BaselineResponse = QualityMeasurement.ResponseMilliseconds(currentScan, 5000),
                                    TargetResponse = QualityMeasurement.ResponseMilliseconds(oldScan, 5000),
                                    Reason = "observation rollback target verified" });
                            if (ApplyAutomaticSwitch(assurance, rollbackAuthorization, current,
                                old.Name, "切换后效果不佳，旧节点复检通过，自动回退",
                                QualityMeasurement.ResponseMilliseconds(currentScan, 5000), false, false))
                            {
                                current = old.Name; currentCandidate = old; currentScan = oldScan;
                                assuranceDecision = "已安全回退，继续观察连接稳定性";
                            }
                        }
                        else assuranceDecision = "切换效果不佳，旧节点不满足安全回退条件；暂停寻优";
                    }
                    if (assurance.VerificationCount >= 2)
                    {
                        if (!rollback) assuranceDecision = ConnectionAssurance.Passed(currentScan) ? "切换后复检通过，保持连接" : "切换后证据不足，暂缓寻优";
                        ApplyDecision(assurance, AutomaticDecisionEvent.ObservationComplete,
                            new AutomaticDecisionContext { NowUtc = clock.UtcNow, Current = current,
                                ExpiresUtc = clock.UtcNow.AddMinutes(30),
                                Reason = "switch observation completed" });
                        assurance.Target = null;
                        assurance.HoldUntilUtc = clock.UtcNow.AddMinutes(30);
                    }
                }
                experienceStore.Save(experience, clock.UtcNow);
            }
            bool sharedIncidentFailure = serviceIncidentDecision != null &&
                ServiceIncidentPolicy.IsSuppressedFailure(currentScan, suppressedServices);
            if (currentScan.Health != CandidateHealth.Unknown && !sharedIncidentFailure)
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
            if (currentScan.Health != CandidateHealth.Unknown && !sharedIncidentFailure) qualityHistory.Add(new QualitySample(currentScan.Name, clock.UtcNow, ServiceEvidencePolicy.CanEmergencySwitch(currentScan),
                currentResponse, Jitter(qualityHistory, currentScan.Name, currentResponse), priorCurrent == null ? 0 : priorCurrent.ThroughputBytesPerSecond,
                currentCandidate == null ? (double?)null : currentCandidate.Multiplier));
            if (!sharedIncidentFailure) experience.Observe(memoryScope, currentScan, null, clock.UtcNow);

            bool opportunityActivity = false;
            bool opportunitySwitched = false;
            IList<double> recentCurrentResponses = experience.RecentResponses(memoryScope, current, 3);
            PendingOptimization pendingOptimization = assurance.PendingOptimization;
            if (pendingOptimization != null)
            {
                opportunityActivity = true;
                string cancelReason = null;
                if (!preferences.AutomaticOptimization) cancelReason = "automatic optimization disabled";
                else if (!OpportunityOptimizationPolicy.IsFresh(pendingOptimization, memoryScope, current, clock.UtcNow))
                    cancelReason = pendingOptimization.Scope != memoryScope ? "scope changed" :
                        pendingOptimization.Current != current ? "selected node changed" : "pending target expired";
                else if (observing || fastSwitched) cancelReason = "switch observation active";
                else if (suppressedServices.Count > 0 || serviceIncidentDecision != null) cancelReason = "service incident active";
                else if (!trafficIdle) cancelReason = "foreground traffic active";
                else if (pathHealth != null && !pathHealth.CanAutoSwitch) cancelReason = "proxy path mismatch";
                else if (!QualityPolicy.CurrentNeedsOptimization(recentCurrentResponses)) cancelReason = "current node recovered";
                else if (clock.UtcNow < assurance.HoldUntilUtc) cancelReason = "optimization hold active";

                if (cancelReason != null)
                {
                    logger.Write("opportunity pending cancelled reason=" + cancelReason);
                    ApplyDecision(assurance, AutomaticDecisionEvent.OptimizationRejected,
                        new AutomaticDecisionContext { NowUtc = clock.UtcNow, Current = current,
                            Target = pendingOptimization.Target, Reason = cancelReason });
                    assurance.ClearPendingOptimization();
                    assuranceDecision = "自动寻优已取消：" + cancelReason;
                }
                else if (clock.UtcNow - pendingOptimization.CreatedUtc >= OpportunityOptimizationPolicy.ConfirmationInterval)
                {
                    CandidateNode pendingCandidate = candidates.FirstOrDefault(x => x.Name == pendingOptimization.Target);
                    if (pendingCandidate == null)
                    {
                        ApplyDecision(assurance, AutomaticDecisionEvent.OptimizationRejected,
                            new AutomaticDecisionContext { NowUtc = clock.UtcNow, Current = current,
                                Target = pendingOptimization.Target,
                                Reason = "pending target removed" });
                        assurance.ClearPendingOptimization();
                        assuranceDecision = "自动寻优目标已不在候选列表";
                    }
                    else
                    {
                        Report("正在确认自动寻优目标：" + pendingCandidate.Name, currentEvidence);
                        CandidateScanResult targetScan = scanner.ScanSelected(pendingCandidate, requiredServices);
                        RememberRegionEligibility(memoryScope, targetScan);
                        scans[targetScan.Name] = targetScan;
                        scanTimes[targetScan.Name] = clock.UtcNow;
                        double confirmedBaseline = QualityMeasurement.ResponseMilliseconds(currentScan,
                            pendingOptimization.BaselineResponse);
                        double confirmedTarget = QualityMeasurement.ResponseMilliseconds(targetScan,
                            pendingOptimization.TargetResponse);
                        bool comparable = OpportunityOptimizationPolicy.IsPerformanceComparable(targetScan, requiredServices);
                        MaterialImprovementDecision improvement = MaterialImprovementPolicy.Evaluate(
                            confirmedBaseline, confirmedTarget,
                            assurance.HasRecentAutomaticSwitch(clock.UtcNow));
                        bool confirmed = comparable && improvement.Accepted &&
                            String.Equals(mihomo.GetSelected(config.SharedGroup), pendingOptimization.Current,
                                StringComparison.Ordinal);
                        if (confirmed && !dryRun)
                        {
                            string oldCurrent = current;
                            SwitchBudgetDecision budget = assurance.AutomaticSwitchBudget(clock.UtcNow);
                            if (!budget.Allowed)
                            {
                                ApplyDecision(assurance, AutomaticDecisionEvent.BudgetExhausted,
                                    new AutomaticDecisionContext { NowUtc = clock.UtcNow,
                                        Current = current, ExpiresUtc = budget.AllowedAtUtc,
                                        Reason = budget.Reason });
                                assurance.PendingOptimization = null;
                                assuranceDecision = "自动寻优达到切换预算，进入稳定观察";
                            }
                            else
                            {
                                DecisionTransition authorization = ApplyDecision(assurance,
                                    AutomaticDecisionEvent.SwitchAuthorized,
                                    new AutomaticDecisionContext { NowUtc = clock.UtcNow,
                                        Current = oldCurrent, Previous = oldCurrent,
                                        Target = pendingCandidate.Name,
                                        BaselineResponse = confirmedBaseline,
                                        TargetResponse = confirmedTarget,
                                        Reason = "confirmed material optimization" });
                                bool provisional = !ServiceEvidencePolicy.CanEmergencySwitch(targetScan);
                                string switchReason = "自动寻优：30 秒复检通过相对与绝对改善阈值";
                                if (ApplyAutomaticSwitch(assurance, authorization, oldCurrent,
                                    pendingCandidate.Name, switchReason, confirmedBaseline, true, provisional))
                                {
                                    current = pendingCandidate.Name;
                                    currentCandidate = pendingCandidate;
                                    currentScan = targetScan;
                                    currentResponse = confirmedTarget;
                                    observing = true;
                                    opportunitySwitched = true;
                                    assuranceDecision = "自动寻优复检通过，已切换到实测更快节点";
                                    logger.Write("opportunity switched from=" + SafeName(oldCurrent) + " to=" +
                                        SafeName(current) + " baseline_ms=" + confirmedBaseline.ToString("F0") +
                                        " target_ms=" + confirmedTarget.ToString("F0"));
                                }
                            }
                        }
                        else
                        {
                            ApplyDecision(assurance, AutomaticDecisionEvent.OptimizationRejected,
                                new AutomaticDecisionContext { NowUtc = clock.UtcNow, Current = current,
                                    Target = pendingCandidate.Name,
                                    BaselineResponse = confirmedBaseline,
                                    TargetResponse = confirmedTarget,
                                    Reason = "optimization confirmation rejected" });
                            assurance.ClearPendingOptimization();
                            assuranceDecision = dryRun ? "自动寻优演练完成，未切换" : "自动寻优目标复检未达到切换标准";
                            logger.Write("opportunity pending rejected target=" + SafeName(pendingCandidate.Name) +
                                " baseline_ms=" + confirmedBaseline.ToString("F0") + " target_ms=" +
                                confirmedTarget.ToString("F0") + " comparable=" + comparable.ToString().ToLowerInvariant() +
                                " relative=" + improvement.RelativeImprovement.ToString("P1") +
                                " absolute_ms=" + improvement.AbsoluteImprovementMilliseconds.ToString("F0") +
                                " required_relative=" + improvement.RequiredRelativeImprovement.ToString("P0") +
                                " required_absolute_ms=" + improvement.RequiredAbsoluteImprovementMilliseconds.ToString("F0") +
                                " reason=" + improvement.Reason);
                        }
                    }
                }
                else assuranceDecision = "已找到更快候选，等待 30 秒确认";
            }

            bool opportunityDue = !opportunityActivity && !fastSwitched && serviceIncidentDecision == null &&
                suppressedServices.Count == 0 && trafficIdle && (pathHealth == null || pathHealth.CanAutoSwitch) &&
                (assurance.Decision.State == AutomaticDecisionState.Healthy ||
                 assurance.Decision.State == AutomaticDecisionState.Degraded) &&
                clock.UtcNow >= assurance.HoldUntilUtc && OpportunityOptimizationPolicy.ShouldScan(
                    preferences.AutomaticOptimization, observing, recentCurrentResponses,
                    assurance.LastOpportunityScanUtc, clock.UtcNow);
            if (opportunityDue)
            {
                opportunityActivity = true;
                ApplyDecision(assurance, AutomaticDecisionEvent.OptimizationDue,
                    new AutomaticDecisionContext { NowUtc = clock.UtcNow, Current = current,
                        Reason = "scheduled opportunity scan" });
                assurance.LastOpportunityScanUtc = clock.UtcNow;
                List<CandidateNode> opportunityPool = candidates.Where(x => x.Name != current).ToList();
                Stopwatch opportunityTimer = Stopwatch.StartNew();
                Dictionary<string, int> opportunityDelays = MeasureLiveDelays(opportunityPool);
                string[] ranked = StartupRecovery.RankFastCandidates(opportunityPool, opportunityDelays,
                    current, Int32.MaxValue);
                ServiceKind[] quickServices = StartupRecovery.FastProbeServices(requiredServices, ServiceKind.ChatGPT);
                var comparableScans = new List<CandidateScanResult>();
                int checkedCandidates = 0;
                foreach (string candidateName in ranked)
                {
                    if (StartupRecovery.ShouldStopAfterCheckedCandidates(checkedCandidates) ||
                        StartupRecovery.ShouldStopAfterEligibleCandidates(comparableScans.Count)) break;
                    CandidateNode candidate = candidates.First(x => x.Name == candidateName);
                    Report("正在自动比较低延迟候选：" + candidateName, currentEvidence);
                    CandidateScanResult quickScan = scanner.ScanSelected(candidate, quickServices,
                        TimeSpan.FromSeconds(2));
                    RememberRegionEligibility(memoryScope, quickScan);
                    scans[candidateName] = quickScan;
                    scanTimes[candidateName] = clock.UtcNow;
                    checkedCandidates++;
                    if (OpportunityOptimizationPolicy.IsPerformanceComparable(quickScan, quickServices))
                        comparableScans.Add(quickScan);
                }
                string[] opportunityTargets = StartupRecovery.RankPerformanceComparableTargets(
                    comparableScans, opportunityDelays);
                if (opportunityTargets.Length > 0)
                {
                    string target = opportunityTargets[0];
                    CandidateNode targetCandidate = candidates.First(x => x.Name == target);
                    Report("正在完整验证自动寻优目标：" + target, currentEvidence);
                    CandidateScanResult fullTargetScan = scanner.ScanSelected(targetCandidate, requiredServices);
                    RememberRegionEligibility(memoryScope, fullTargetScan);
                    scans[target] = fullTargetScan;
                    scanTimes[target] = clock.UtcNow;
                    List<double> orderedResponses = recentCurrentResponses.OrderBy(x => x).ToList();
                    double baseline = orderedResponses.Count == 0 ? currentResponse :
                        orderedResponses[orderedResponses.Count / 2];
                    double targetResponse = QualityMeasurement.ResponseMilliseconds(fullTargetScan, 5000);
                    MaterialImprovementDecision improvement = MaterialImprovementPolicy.Evaluate(
                        baseline, targetResponse, assurance.HasRecentAutomaticSwitch(clock.UtcNow));
                    if (OpportunityOptimizationPolicy.IsPerformanceComparable(fullTargetScan, requiredServices) &&
                        improvement.Accepted)
                    {
                        assurance.PendingOptimization = new PendingOptimization { Scope = memoryScope,
                            Current = current, Target = target, BaselineResponse = baseline,
                            TargetResponse = targetResponse, CreatedUtc = clock.UtcNow };
                        ApplyDecision(assurance, AutomaticDecisionEvent.OptimizationTargetPrepared,
                            new AutomaticDecisionContext { NowUtc = clock.UtcNow, Scope = memoryScope,
                                Current = current, Target = target, BaselineResponse = baseline,
                                TargetResponse = targetResponse,
                                ExpiresUtc = clock.UtcNow.Add(OpportunityOptimizationPolicy.PendingLifetime),
                                Reason = "material optimization target prepared" });
                        assuranceDecision = "已找到更快候选，30 秒后自动确认";
                        logger.Write("opportunity pending target=" + SafeName(target) + " baseline_ms=" +
                            baseline.ToString("F0") + " target_ms=" + targetResponse.ToString("F0") +
                            " relative=" + improvement.RelativeImprovement.ToString("P1") +
                            " absolute_ms=" + improvement.AbsoluteImprovementMilliseconds.ToString("F0") +
                            " required_relative=" + improvement.RequiredRelativeImprovement.ToString("P0") +
                            " required_absolute_ms=" + improvement.RequiredAbsoluteImprovementMilliseconds.ToString("F0"));
                    }
                    else
                    {
                        ApplyDecision(assurance, AutomaticDecisionEvent.OptimizationRejected,
                            new AutomaticDecisionContext { NowUtc = clock.UtcNow, Current = current,
                                Target = target, BaselineResponse = baseline,
                                TargetResponse = targetResponse, Reason = improvement.Reason });
                        assurance.ClearPendingOptimization();
                    }
                }
                else
                {
                    ApplyDecision(assurance, AutomaticDecisionEvent.OptimizationRejected,
                        new AutomaticDecisionContext { NowUtc = clock.UtcNow, Current = current,
                            Reason = "no comparable optimization target" });
                    assurance.ClearPendingOptimization();
                }
                opportunityTimer.Stop();
                logger.Write("opportunity scan elapsed_ms=" + opportunityTimer.ElapsedMilliseconds +
                    " checked=" + checkedCandidates + " eligible=" + comparableScans.Count);
            }

            if (lastQualityRefreshUtc == DateTime.MinValue)
                lastQualityRefreshUtc = qualityHistory.Where(x => x.ThroughputBytesPerSecond > 0).Select(x => x.CheckedUtc).DefaultIfEmpty(DateTime.MinValue).Max();
            bool throughputDue = RefreshPolicy.ShouldRunThroughput(subscriptionChanged, currentScan.Health,
                lastQualityRefreshUtc, clock.UtcNow, config.QualityRefreshInterval);
            if (suppressedServices.Count > 0) throughputDue = false;
            bool refreshQuality = RefreshPolicy.ShouldRefreshCandidates(subscriptionChanged, currentScan.Health, throughputDue);
            bool standbyDue = clock.UtcNow - assurance.RefreshUtc >= TimeSpan.FromMinutes(5);
            refreshQuality = refreshQuality || standbyDue;
            if (observing) refreshQuality = false;
            if (currentRecoveredOnRetry) refreshQuality = false;
            if (serviceIncidentDecision != null) refreshQuality = false;
            if (opportunityActivity) refreshQuality = false;
            if (!trafficIdle && ServiceEvidencePolicy.CanHold(currentScan)) refreshQuality = false;

            var freshlyVerified = new HashSet<string>(StringComparer.Ordinal);
            if (ServiceEvidencePolicy.CanEmergencySwitch(currentScan)) freshlyVerified.Add(currentScan.Name);
            if (refreshQuality)
            {
                var delays = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (string standby in assurance.Available(candidates.Select(x => x.Name), current, clock.UtcNow))
                {
                    Report("正在复检备用节点：" + standby, currentEvidence);
                    CandidateScanResult standbyScan = scanner.ScanSelected(candidates.First(x => x.Name == standby), servicesToProbe);
                    RememberRegionEligibility(memoryScope, standbyScan);
                    scans[standby] = standbyScan;
                    scanTimes[standby] = clock.UtcNow;
                    if (suppressedServices.Count == 0) assurance.Remember(standbyScan, current, clock.UtcNow);
                    if (ServiceEvidencePolicy.CanEmergencySwitch(standbyScan)) delays[standby] = (int)Math.Min(Int32.MaxValue, QualityMeasurement.ResponseMilliseconds(standbyScan, 5000));
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
                        RememberRegionEligibility(memoryScope, scan);
                        scanTimes[candidate.Name] = clock.UtcNow;
                    }
                    scans[candidate.Name] = scan;
                    if (suppressedServices.Count == 0) assurance.Remember(scan, current, clock.UtcNow);
                    CandidateScanResult historicalScan = ServiceIncidentPolicy.AttachSuppressed(scan, suppressedServices);
                    if (historicalScan.Health != CandidateHealth.Unknown) state.Records[candidate.Name] = Record(historicalScan);
                    if (ServiceEvidencePolicy.CanEmergencySwitch(scan)) { compatible.Add(candidate); freshlyVerified.Add(candidate.Name); }
                    QualitySample previous = Latest(qualityHistory, candidate.Name);
                    double response = QualityMeasurement.ResponseMilliseconds(scan, 5000);
                    if (suppressedServices.Count == 0 && scan.Health != CandidateHealth.Unknown) qualityHistory.Add(new QualitySample(candidate.Name, clock.UtcNow, ServiceEvidencePolicy.CanEmergencySwitch(scan),
                        MedianResponse(qualityHistory, candidate.Name, response), Jitter(qualityHistory, candidate.Name, response),
                        previous == null ? 0 : previous.ThroughputBytesPerSecond, candidate.Multiplier));
                    experience.Observe(memoryScope, historicalScan, null, clock.UtcNow);
                    if (!ServiceEvidencePolicy.CanHold(scan))
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
                var incidentCohort = scans.Values.Where(x => freshlyVerified.Contains(x.Name) && ServiceEvidencePolicy.CanEmergencySwitch(x))
                    .Select(x => new QualitySample(x.Name, clock.UtcNow, true,
                        QualityMeasurement.ResponseMilliseconds(x, 5000), 0, 0,
                        candidates.First(y => y.Name == x.Name).Multiplier)).ToList();
                foreach (QualitySample sample in incidentCohort)
                    scores[sample.Name] = QualityScorer.Score(sample, new[] { sample }, incidentCohort, clock.UtcNow);
            }
            foreach (var remembered in experience.Recommend(memoryScope, scores.Keys, clock.UtcNow))
                scores[remembered.Node].Score = 0.8 * scores[remembered.Node].Score + 0.2 * remembered.Rank(clock.UtcNow);
            QualityBreakdown currentScore = null;
            var best = scores.OrderByDescending(x => {
                CandidateScanResult candidateScan;
                scans.TryGetValue(x.Key, out candidateScan);
                return ServiceEvidencePolicy.CanQualitySwitch(candidateScan, experience,
                    memoryScope, requiredServices, clock.UtcNow);
            }).ThenByDescending(x => x.Value.Score).FirstOrDefault();
            bool switched = fastSwitched || opportunitySwitched;
            string decision = serviceIncidentDecision ?? "保持当前节点";
            if (!trafficIdle && serviceIncidentDecision == null) decision = "检测到较大流量，暂缓速度采样和体验优化切换";
            double? reportedScore = null;
            QualityBreakdown reportedCurrent;
            if (scores.TryGetValue(current, out reportedCurrent)) reportedScore = reportedCurrent.Score;
            if (ServiceEvidencePolicy.CanHold(currentScan) || currentScan.Health == CandidateHealth.Unknown)
                controller.Decide(true, false, current, null);
            FailoverDecision confirmedFailure = null;
            if (currentFailureConfirmed || (currentScan.Health != CandidateHealth.Unknown &&
                !ConnectionAssurance.Passed(currentScan) && !StartupRecovery.NeedsImmediateConfirmation(currentScan)))
            {
                IEnumerable<NodeHealthRecord> decisionCandidates = suppressedServices.Count == 0
                    ? state.Records.Values.Where(x => freshlyVerified.Contains(x.Name))
                    : scans.Values.Where(x => freshlyVerified.Contains(x.Name) && ServiceEvidencePolicy.CanEmergencySwitch(x)).Select(Record);
                confirmedFailure = controller.Decide(false, false, current, decisionCandidates);
            }
            if (!observing && currentScan.Health != CandidateHealth.Unknown && !String.IsNullOrEmpty(best.Key) && best.Key != current)
            {
                bool currentCompatible = ServiceEvidencePolicy.CanEmergencySwitch(currentScan) && scores.TryGetValue(current, out currentScore);
                bool shouldSwitch;
                string reason;
                CandidateScanResult selectedTargetScan;
                scans.TryGetValue(best.Key, out selectedTargetScan);
                if (currentCompatible)
                {
                    if (!SwitchModePolicy.ShouldEvaluateQuality(currentCompatible, preferences.AutomaticOptimization))
                    {
                        shouldSwitch = false;
                        reason = SwitchModePolicy.ConservativeHoldReason;
                    }
                    else if (!ServiceEvidencePolicy.CanQualitySwitch(selectedTargetScan, experience, memoryScope, requiredServices, clock.UtcNow))
                    {
                        shouldSwitch = false;
                        reason = "target requires account verification";
                    }
                    else
                    {
                        FailoverDecision qualityDecision = controller.DecideQuality(currentScore.Score, best.Value.Score, false,
                            freshlyVerified.Contains(best.Key), experience.IsProvenStable(memoryScope, best.Key, clock.UtcNow),
                            experience.RecentResponses(memoryScope, current, 3), experience.RecentResponses(memoryScope, best.Key, 5), selectedTargetScan);
                        shouldSwitch = qualityDecision.ShouldSwitch;
                        reason = qualityDecision.Reason;
                    }
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
                if (shouldSwitch && !dryRun && (pathHealth == null || pathHealth.CanAutoSwitch) && SwitchModePolicy.AllowsAutomaticSwitch(currentCompatible, preferences.AutomaticOptimization))
                {
                    DateTime verifiedUtc;
                    CandidateScanResult finalScan;
                    if (!scans.TryGetValue(best.Key, out finalScan) || !scanTimes.TryGetValue(best.Key, out verifiedUtc) ||
                        StartupRecovery.RequiresFinalRecheck(clock.UtcNow - verifiedUtc))
                    {
                        Report("切换前正在复检目标节点：" + best.Key, currentEvidence);
                        finalScan = scanner.ScanSelected(candidates.First(x => x.Name == best.Key), servicesToProbe);
                        RememberRegionEligibility(memoryScope, finalScan);
                        scans[best.Key] = finalScan;
                        scanTimes[best.Key] = clock.UtcNow;
                    }
                    selectedTargetScan = finalScan;
                    shouldSwitch = ServiceEvidencePolicy.CanEmergencySwitch(finalScan);
                    if (currentCompatible && shouldSwitch)
                        shouldSwitch = ServiceEvidencePolicy.CanQualitySwitch(finalScan, experience, memoryScope, requiredServices, clock.UtcNow);
                    if (!shouldSwitch) decision = "目标节点复检未通过，保留当前连接";
                }
                CheckStop();
                if (shouldSwitch && !dryRun && (pathHealth == null || pathHealth.CanAutoSwitch) && SwitchModePolicy.AllowsAutomaticSwitch(currentCompatible, preferences.AutomaticOptimization) &&
                    String.Equals(mihomo.GetSelected(config.SharedGroup), current, StringComparison.Ordinal))
                {
                    bool provisional = !ServiceEvidencePolicy.CanQualitySwitch(selectedTargetScan,
                        experience, memoryScope, requiredServices, clock.UtcNow);
                    if (!currentCompatible && provisional) reason = "provisional emergency failover";
                    if (currentCompatible)
                    {
                        shouldSwitch = false;
                        decision = "性能寻优需经过两阶段确认，保留当前节点";
                    }
                    else
                    {
                        if (assurance.Decision.State != AutomaticDecisionState.Recovering)
                        {
                            ApplyDecision(assurance, AutomaticDecisionEvent.HardFailure,
                                new AutomaticDecisionContext { NowUtc = clock.UtcNow,
                                    Current = current, Evidence = DecisionEvidenceClass.HardFailure,
                                    Reason = "confirmed failure fallback" });
                            ApplyDecision(assurance, AutomaticDecisionEvent.FailureConfirmed,
                                new AutomaticDecisionContext { NowUtc = clock.UtcNow,
                                    Current = current, Evidence = DecisionEvidenceClass.HardFailure });
                        }
                        DecisionTransition authorization = ApplyDecision(assurance,
                            AutomaticDecisionEvent.RecoveryTargetReady,
                            new AutomaticDecisionContext { NowUtc = clock.UtcNow,
                                Current = current, Previous = current, Target = best.Key,
                                Evidence = DecisionEvidenceClass.HardFailure,
                                BaselineResponse = currentResponse,
                                TargetResponse = QualityMeasurement.ResponseMilliseconds(selectedTargetScan, 5000),
                                Reason = reason });
                        if (ApplyAutomaticSwitch(assurance, authorization, current, best.Key,
                            "自动切换：" + StatusReport.DecisionText(reason), currentResponse,
                            false, provisional))
                        {
                            experienceStore.Save(experience, clock.UtcNow);
                            switched = true;
                            reportedScore = suppressedServices.Count == 0 ? (double?)best.Value.Score : null;
                            decision = "已切换：" + reason;
                            logger.Write("switched node=" + SafeName(best.Key) + " score=" + best.Value.Score.ToString("F1") + " reason=" + reason);
                        }
                    }
                }
            }
            if (dryRun) logger.Write("dry-run quality evaluation completed; shared selector unchanged");
            if (pathHealth != null && pathHealth.Mismatch)
                decision = "检测路径异常：兼容性探测与系统代理连通结果不一致，暂停自动切换";
            else if (!switched && currentScan.Health == CandidateHealth.BasicCompatible && serviceIncidentDecision == null)
            {
                decision = "current entry reachable but login unverified";
                logger.Write("current entry reachable but login remains unverified; holding current node");
            }
            else if (!switched && currentScan.Health == CandidateHealth.Unknown && serviceIncidentDecision == null)
            {
                decision = "证据不足，保留当前节点";
                logger.Write("待验证，保留当前连接；" + currentScan.Detail);
            }
            else if (!switched && !ServiceEvidencePolicy.CanHold(currentScan)) logger.Write("current check failed " + currentScan.Health + "; awaiting safe replacement");
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
                if (serviceIncidentDecision != null &&
                    ServiceIncidentPolicy.IsSuppressedFailure(scan, suppressedServices)) continue;
                QualitySample speed;
                speedSamples.TryGetValue(scan.Name, out speed);
                experience.Observe(memoryScope, ServiceIncidentPolicy.AttachSuppressed(scan, suppressedServices), speed, clock.UtcNow);
            }
            TimeSpan nextInterval = assurance.Interval(actualScan);
            if (assurance.PendingOptimization != null) nextInterval = OpportunityOptimizationPolicy.ConfirmationInterval;
            decision = StatusReport.DecisionText(decision) + " · 近期备用 " + assurance.Available(candidates.Select(x => x.Name), actual, clock.UtcNow).Length + "/2";
            experienceStore.Save(experience, clock.UtcNow);
            if (!dryRun)
            {
                CandidateHealth status = actualScan.Health;
                string detail = actualScan.Detail;
                if (pathHealth != null && pathHealth.Mismatch)
                    detail += "; 检测路径异常：" + pathHealth.MismatchDetail;
                bool hasAiRequirement = requiredServices.Any(x => x == ServiceKind.ChatGPT || x == ServiceKind.Gemini);
                if (hasAiRequirement && status == CandidateHealth.Compatible)
                    detail += "; AI 登录网络链路检测通过，未代替账号内真实对话";
                string text = StatusReport.Format(clock.UtcNow, actual, status, reportedScore, decision, detail);
                StatusReport.WriteAtomic(Path.Combine(config.RootPath, "current-status.txt"), text);
            }
            store.Save(state, candidates.Select(x => x.Name));
            qualityStore.Save(qualityHistory);
            string selectionSummary = experience.SelectionSummary(actual, firstCompletedCycle && actual == cycleStartNode && !switched);
            firstCompletedCycle = false;
            MonitorSnapshot completed = MonitorSnapshot.CreateRunning(actual, actualScan, reportedScore, decision, clock.UtcNow,
                clock.UtcNow.Add(nextInterval))
                .WithSelectionReason(selectionSummary);
            return pathHealth != null && pathHealth.Mismatch
                ? completed.WithState(MonitorRunState.Degraded, decision, clock.UtcNow.Add(nextInterval)) : completed;
        }
        finally
        {
            SaveRegionEligibility();
            cycleTimer = null;
            System.Threading.Volatile.Write(ref running, 0);
            MemoryTrimmer.TrimIdleWorkingSet();
        }
    }

    private Dictionary<string, int> MeasureLiveDelays(IEnumerable<CandidateNode> candidates)
    {
        List<Task<KeyValuePair<string, int>>> tasks = (candidates ?? Enumerable.Empty<CandidateNode>())
            .Select(candidate => Task.Factory.StartNew(() => {
                try
                {
                    return new KeyValuePair<string, int>(candidate.Name,
                        mihomo.GetDelay(candidate.Name, config.DelayProbeUrl, 2500));
                }
                catch (IOException) { return new KeyValuePair<string, int>(candidate.Name, Int32.MaxValue); }
                catch (TimeoutException) { return new KeyValuePair<string, int>(candidate.Name, Int32.MaxValue); }
                catch (ArgumentException) { return new KeyValuePair<string, int>(candidate.Name, Int32.MaxValue); }
            })).ToList();
        Task.WaitAll(tasks.Cast<Task>().ToArray());
        return tasks.Select(x => x.Result).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
    }

    private void RememberRegionEligibility(string scope, CandidateScanResult scan)
    {
        if (scan == null) return;
        regionEligibility.TryRemember(scope, scan.Name, scan.ExitFingerprint,
            scan.ExitCountryCode, clock.UtcNow);
    }

    private void SaveRegionEligibility()
    {
        try { regionEligibilityStore.Save(regionEligibility); }
        catch (IOException ex) { LogRegionEligibilitySaveFailure(ex); }
        catch (UnauthorizedAccessException ex) { LogRegionEligibilitySaveFailure(ex); }
        catch (ArgumentException ex) { LogRegionEligibilitySaveFailure(ex); }
    }

    private void LogRegionEligibilitySaveFailure(Exception error)
    {
        try { logger.Write("region eligibility save failed " + error.GetType().Name); }
        catch { }
    }

    private DecisionTransition ApplyDecision(ConnectionAssurance assurance,
        AutomaticDecisionEvent value, AutomaticDecisionContext context)
    {
        DecisionTransition transition = assurance.Apply(value, context);
        DateTime now = context == null || context.NowUtc == DateTime.MinValue
            ? clock.UtcNow : context.NowUtc;
        IList<AutomaticSwitchRecord> history = assurance.AutomaticSwitches ??
            new List<AutomaticSwitchRecord>();
        int switches10 = history.Count(x => x != null && x.Utc <= now &&
            x.Utc > now.AddMinutes(-10));
        int switches30 = history.Count(x => x != null && x.Utc <= now &&
            x.Utc > now.AddMinutes(-30));
        AutomaticDecisionTransaction transaction = transition.Transaction ??
            assurance.Decision ?? new AutomaticDecisionTransaction();
        logger.Write("decision transition state_from=" + transition.PreviousState +
            " state_to=" + transaction.State + " event=" + value +
            " evidence=" + transaction.Evidence + " directive=" + transition.Directive +
            " accepted=" + transition.Accepted.ToString().ToLowerInvariant() +
            " switches_10m=" + switches10 + " switches_30m=" + switches30 +
            " reason=" + transition.Reason);
        return transition;
    }

    private bool ApplyAutomaticSwitch(ConnectionAssurance assurance,
        DecisionTransition authorization, string expectedSource, string target,
        string reason, double baselineResponse, bool quality, bool provisional)
    {
        if (authorization == null || !authorization.Accepted ||
            (authorization.Directive != AutomaticDecisionDirective.Switch &&
             authorization.Directive != AutomaticDecisionDirective.Rollback))
            return false;
        if (!String.Equals(mihomo.GetSelected(config.SharedGroup), expectedSource,
            StringComparison.Ordinal))
        {
            ApplyDecision(assurance, AutomaticDecisionEvent.SwitchFailed,
                new AutomaticDecisionContext { NowUtc = clock.UtcNow,
                    Current = expectedSource, Target = target,
                    Reason = "selector changed before authorized switch" });
            return false;
        }

        try
        {
            SelectRecorded(expectedSource, target, reason);
            assurance.RecordAutomaticSwitch(expectedSource, target, reason, clock.UtcNow);
            assurance.Begin(expectedSource, target, baselineResponse, quality,
                provisional, clock.UtcNow);
            previousSelectedNode = expectedSource;
            controller.RecordSwitch();
            return true;
        }
        catch
        {
            ApplyDecision(assurance, AutomaticDecisionEvent.SwitchFailed,
                new AutomaticDecisionContext { NowUtc = clock.UtcNow,
                    Current = expectedSource, Target = target,
                    Reason = "authorized selector write failed" });
            throw;
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
        FollowGeneralNode();
    }

    private bool FollowGeneralNode(CandidateScanResult verifiedScan = null)
    {
        if (String.IsNullOrWhiteSpace(config.GeneralGroup)) return false;
        try
        {
            bool changed = verifiedScan == null
                ? SelectorFollower.Synchronize(mihomo, config.SharedGroup, config.GeneralGroup)
                : SelectorFollower.SynchronizeVerified(mihomo, config.SharedGroup, config.GeneralGroup, verifiedScan);
            if (changed) logger.Write("general selector aligned with stable node=" + SafeName(mihomo.GetSelected(config.SharedGroup)));
            return changed;
        }
        catch (IOException ex) { logger.Write("general selector sync skipped " + ex.GetType().Name); }
        catch (TimeoutException ex) { logger.Write("general selector sync skipped " + ex.GetType().Name); }
        catch (InvalidOperationException ex) { logger.Write("general selector sync skipped " + ex.GetType().Name); }
        return false;
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
