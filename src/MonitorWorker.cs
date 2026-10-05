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

    public void TryWrite(string message)
    {
        try { Write(message); }
        catch { }
    }
}

internal sealed class CandidateLatencyStore
{
    private readonly object gate = new object();
    private int generation;
    private IList<CandidateLatencyMeasurement> current = Empty();

    public IList<CandidateLatencyMeasurement> Current
    {
        get { lock (gate) return current; }
    }

    public int BeginMeasurement()
    {
        lock (gate) return generation;
    }

    public bool TryPublish(int measuredGeneration, IList<CandidateLatencyMeasurement> value,
        out IList<CandidateLatencyMeasurement> published)
    {
        lock (gate)
        {
            if (measuredGeneration != generation)
            {
                published = Empty();
                return false;
            }
            current = value ?? Empty();
            published = current;
            return true;
        }
    }

    public void Invalidate()
    {
        lock (gate)
        {
            generation++;
            current = Empty();
        }
    }

    private static IList<CandidateLatencyMeasurement> Empty()
    {
        return new List<CandidateLatencyMeasurement>().AsReadOnly();
    }
}

public sealed partial class MonitorWorker : IRestorableCycleRunner, IProgressCycleRunner, IAccountVerificationRunner,
    ITriggeredCycleRunner, ICandidateLatencyRunner
{
    private readonly MonitorConfiguration config;
    private readonly string preferredMonitorGroup;
    private MonitorSelectionBinding selectionBinding;
    private string selectionMode;
    private readonly IMihomoClient mihomo;
    private readonly CompatibilityScanner scanner;
    private readonly BoundedLogger logger;
    private readonly IClock clock;




    private readonly Func<RuntimeSnapshot> runtimeSnapshotProvider;
    private readonly INodeIdentitySource nodeIdentitySource;
    private IDictionary<string, CandidateNode> activeCandidatesByName =
        new Dictionary<string, CandidateNode>(StringComparer.Ordinal);
    private IDictionary<string, string> activeStrongIdsByName =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private IDictionary<string, string> activeNamesByStrongId =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private int running;
    private int accountCommandRunning;
    private string previousSelectedNode;
    private Stopwatch cycleTimer;
    private TimeSpan cycleTimeBudget = TimeSpan.FromMinutes(3);

    private UserPreferences lastPreferences = UserPreferences.Defaults();
    private readonly ExperienceStore experienceStore;
    private ExperienceData experience;
    private readonly RegionEligibilityStore regionEligibilityStore;
    private readonly RegionEligibilityCache regionEligibility;
    private readonly CandidateLatencyStore candidateLatencyStore = new CandidateLatencyStore();
    private bool firstCompletedCycle = true;
    public event Action<MonitorSnapshot> Progress;
    public Func<bool> ShouldStop { get; set; }
    private bool StopRequired() { return System.Threading.Volatile.Read(ref accountCommandRunning) == 0 &&
        ((ShouldStop != null && ShouldStop()) || (cycleTimer != null && cycleTimer.Elapsed > cycleTimeBudget)); }
    private void CheckStop() { if (StopRequired()) throw new OperationCanceledException("检测已暂停或达到本轮时间预算，将在下一轮继续"); }
    private void Report(string stage, MonitorSnapshot evidence)
    {
        CheckStop();
        var handler = Progress;
        if (handler != null) handler((evidence == null
            ? MonitorSnapshot.CreateState(MonitorRunState.Checking, stage, DateTime.MinValue, DateTime.MaxValue)
            : evidence.WithProgress(stage)).WithCandidateLatencies(candidateLatencyStore.Current));
    }

    public MonitorWorker(MonitorConfiguration config, IMihomoClient mihomo, IServiceProbe probe, BoundedLogger logger, IClock clock,
        IExitIdentityProbe exitIdentityProbe = null, IProxyPathHealthChecker pathHealthChecker = null,
        INodeIdentitySource nodeIdentitySource = null)
        : this(config, mihomo, probe, logger, clock, exitIdentityProbe, pathHealthChecker, null, nodeIdentitySource)
    {
    }

    internal MonitorWorker(MonitorConfiguration config, IMihomoClient mihomo, IServiceProbe probe,
        BoundedLogger logger, IClock clock, IExitIdentityProbe exitIdentityProbe,
        IProxyPathHealthChecker pathHealthChecker, Func<RuntimeSnapshot> runtimeSnapshotProvider,
        INodeIdentitySource nodeIdentitySource = null, ITrafficMeter trafficMeter = null)
    {
        this.config = config;
        preferredMonitorGroup = config.SharedGroup;
        this.mihomo = mihomo;
        this.scanner = new CompatibilityScanner(mihomo, probe, config.ProbeGroup, exitIdentityProbe, clock);
        scanner.ShouldStop = StopRequired;
        this.logger = logger;
        this.clock = clock;

        this.runtimeSnapshotProvider = runtimeSnapshotProvider ?? delegate { return RuntimeInspector.Capture(mihomo); };
        this.nodeIdentitySource = nodeIdentitySource;
        experienceStore = new ExperienceStore(Path.Combine(config.RootPath, "state", "experience.json"));
        experience = experienceStore.Load();
        regionEligibilityStore = new RegionEligibilityStore(
            Path.Combine(config.RootPath, "state", "region-eligibility.json"));
        regionEligibility = regionEligibilityStore.Load();
        if (experience.Assurance == null) experience.Assurance = new ConnectionAssurance();



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
        if (config.ContinuousOptimization && (trigger == MonitorCycleTrigger.Startup ||
            trigger == MonitorCycleTrigger.Scheduled || trigger == MonitorCycleTrigger.ManualOptimization))
            return RunContinuousOptimization(preferences, trigger);
        return trigger == MonitorCycleTrigger.ManualOptimization
            ? RunManualOptimization(preferences) : RunOnce(false, preferences, trigger);
    }

    public IList<CandidateLatencyMeasurement> MeasureCandidateLatencies(UserPreferences preferences)
    {
        if (preferences == null) throw new ArgumentNullException("preferences");
        if (preferences.RequiredServices == null || preferences.RequiredServices.Count == 0)
            throw new ArgumentException("At least one required service is needed.", "preferences");
        if (System.Threading.Interlocked.Exchange(ref running, 1) != 0) return candidateLatencyStore.Current;
        try
        {
            int generation = candidateLatencyStore.BeginMeasurement();
            cycleTimer = Stopwatch.StartNew();
            BindSelection();
            IList<CandidateNode> candidates = DiscoverCandidates();
            string current = ReadCurrentSelection();
            IList<CandidateLatencyMeasurement> measured = MeasureCandidateLatencies(candidates, current,
                UserPreferencePolicy.NormalizeServices(preferences.RequiredServices));
            IList<CandidateLatencyMeasurement> published;
            candidateLatencyStore.TryPublish(generation, measured, out published);
            return published;
        }
        finally
        {
            cycleTimer = null;
            System.Threading.Volatile.Write(ref running, 0);
            MemoryTrimmer.TrimIdleWorkingSet();
        }
    }

    public void InvalidateCandidateLatencies()
    {
        candidateLatencyStore.Invalidate();
    }

    public bool RestorePrevious()
    {
        if (System.Threading.Interlocked.CompareExchange(ref running, 1, 0) != 0) return false;
        RecoveryState recovery = experience.Assurance.Recovery ??
            (experience.Assurance.Recovery = new RecoveryState());
        try
        {
            cycleTimer = Stopwatch.StartNew();
            BindSelection();
            string target = previousSelectedNode ?? experience.Assurance.Previous;
            string current = ReadCurrentSelection();
            if (String.IsNullOrWhiteSpace(target) || target == current ||
                !mihomo.GetChoices(config.SharedGroup).Contains(target)) return false;
            var node = new CandidateNode(target, null);
            if (!IsLatencyEligible(RecheckWebsiteLatencyNode(node))) return false;
            CheckStop();
            if (ReadCurrentSelection() != current) return false;
            recovery.PendingOriginal = current; recovery.PendingWritten = target;
            recovery.PendingPreviousWrite = null;
            recovery.PendingSelectorKey = SelectionKey;
            SaveExperience(clock.UtcNow);
            if (ReadCurrentSelection() != current)
            { recovery.ClearTransaction(); return false; }
            WriteSelectedNode(current, target);
            if (ReadCurrentSelection() != target ||
                !IsLatencyEligible(RecheckWebsiteLatencyNode(node)) ||
                ReadCurrentSelection() != target) return false;
            recovery.ClearTransaction();
            recovery.ResetCounts(target, experience.ActiveScope);
            recovery.CooldownUntilUtc = clock.UtcNow.AddMinutes(lastPreferences.RecoveryCooldownMinutes);
            previousSelectedNode = current;
            experience.Assurance.Previous = current;
            experience.RecordChange(current, target, "用户恢复上一个节点，复验通过", clock.UtcNow);
            SaveExperience(clock.UtcNow);
            RunStatistics.SelectionConfirmed();
            FollowGeneralNodeAfterRecovery(current, target);
            return true;
        }
        finally
        {
            try { RollbackRecovery(recovery); }
            finally { cycleTimer = null; System.Threading.Volatile.Write(ref running, 0); }
        }
    }

    public bool RecordBrowserConversationProof(string node, string exitFingerprint,
        ServiceKind service, DateTime verifiedUtc, int protocolVersion)
    {
        if ((service != ServiceKind.ChatGPT && service != ServiceKind.Gemini) ||
            protocolVersion != BrowserConversationProof.CurrentProtocolVersion ||
            String.IsNullOrWhiteSpace(node) || String.IsNullOrWhiteSpace(exitFingerprint) ||
            !String.Equals(ReadCurrentSelection(), node, StringComparison.Ordinal) ||
            String.IsNullOrWhiteSpace(experience.ActiveScope)) return false;
        if (System.Threading.Interlocked.CompareExchange(ref running, 1, 0) != 0) return false;
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
                !String.Equals(ReadCurrentSelection(), node, StringComparison.Ordinal)) return false;
            AccountVerificationMemory.MarkBrowserConversation(experience, experience.ActiveScope, node,
                fresh.ExitFingerprint, service, verifiedUtc, protocolVersion);
            SaveExperience(verifiedUtc);
            logger.Write("browser conversation proof recorded node=" + SafeName(node) + " service=" + service);
            return true;
        }
        finally
        {
            cycleTimer = previousTimer;
            System.Threading.Volatile.Write(ref accountCommandRunning, 0);
            System.Threading.Volatile.Write(ref running, 0);
        }
    }

    public void ReportServiceFailure(string node, ServiceKind service, DateTime reportedUtc)
    {
        if (service != ServiceKind.ChatGPT && service != ServiceKind.Gemini) return;
        AccountVerificationMemory.Revoke(experience, experience.ActiveScope, node, service,
            reportedUtc, "user reported failure; detection requested");
        logger.Write("user feedback requests detection only node=" + SafeName(node) + " service=" + service);
        SaveExperience(reportedUtc);
    }
    public void ReportBrowserConversationFailure(string node, string exitFingerprint, ServiceKind service,
        BrowserVerificationOutcome outcome, bool messageSent, DateTime reportedUtc)
    {
        if (service != ServiceKind.ChatGPT && service != ServiceKind.Gemini) return;
        AccountVerificationMemory.RevokeBrowserFailure(experience, experience.ActiveScope, node,
            exitFingerprint, service, reportedUtc);
        logger.Write("browser feedback requests detection only node=" + SafeName(node) + " service=" + service);
        SaveExperience(reportedUtc);
    }
    public MonitorSnapshot RunOnce(bool dryRun, UserPreferences preferences)
    {
        return RunOnce(dryRun, preferences, MonitorCycleTrigger.Scheduled);
    }

    private Dictionary<string, int> MeasureLiveDelays(IEnumerable<CandidateNode> candidates)
    {
        return MeasureUrlDelays(candidates, config.DelayProbeUrl, 2500);
    }

    private Dictionary<string, int> MeasureUrlDelays(IEnumerable<CandidateNode> candidates,
        string url, int timeoutMilliseconds)
    {
        return CandidateDelayMeasurement.Measure(mihomo, candidates, url, timeoutMilliseconds,
            64);
    }

    private void RememberRegionEligibility(string scope, CandidateScanResult scan)
    {
        if (scan == null) return;
        string nodeId = EvidenceKey(scan.Name);
        if (NodeIdentity.IsStrong(nodeId))
            regionEligibility.TryRemember(scope, scan.Name, nodeId, scan.ExitFingerprint,
                scan.ExitCountryCode, clock.UtcNow);
        else if (nodeIdentitySource == null)
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

    private bool FollowGeneralNodeAfterRecovery(string original, string target)
    {
        if (String.IsNullOrWhiteSpace(config.GeneralGroup)) return false;
        try
        {
            if (mihomo.GetSelected(config.GeneralGroup) != original ||
                ReadCurrentSelection() != target) return false;
            bool changed = SelectorFollower.Synchronize(mihomo, config.SharedGroup, config.GeneralGroup, original);
            if (changed) logger.Write("general selector aligned with stable node=" + SafeName(ReadCurrentSelection()));
            return changed;
        }
        catch (IOException ex) { logger.Write("general selector sync skipped " + ex.GetType().Name); }
        catch (TimeoutException ex) { logger.Write("general selector sync skipped " + ex.GetType().Name); }
        catch (InvalidOperationException ex) { logger.Write("general selector sync skipped " + ex.GetType().Name); }
        return false;
    }

    private IList<CandidateNode> DiscoverCandidates()
    {
        IDictionary<string, string> runtimeTypes = null;
        var catalogClient = mihomo as MihomoPipeClient;
        if (catalogClient != null)
        {
            try { runtimeTypes = catalogClient.GetProxyTypes(); }
            catch (IOException ex) { logger.Write("runtime proxy kinds unavailable " + ex.GetType().Name); }
            catch (TimeoutException ex) { logger.Write("runtime proxy kinds unavailable " + ex.GetType().Name); }
            catch (ArgumentException ex) { logger.Write("runtime proxy kinds unavailable " + ex.GetType().Name); }
        }
        IList<CandidateNode> discovered = CandidateCatalog.Filter(
            mihomo.GetChoices(config.SharedGroup), runtimeTypes);
        IDictionary<string, ResolvedNodeIdentity> resolvedIdentities = null;
        if (nodeIdentitySource != null)
        {
            try { resolvedIdentities = nodeIdentitySource.Resolve(discovered.Select(x => x.Name)); }
            catch (IOException ex) { logger.Write("node identity unavailable " + ex.GetType().Name); }
            catch (UnauthorizedAccessException ex) { logger.Write("node identity unavailable " + ex.GetType().Name); }
            catch (ArgumentException ex) { logger.Write("node identity unavailable " + ex.GetType().Name); }
        }
        IList<CandidateNode> candidates = CandidateCatalog.Filter(
            discovered.Select(x => x.Name), runtimeTypes, resolvedIdentities);
        activeCandidatesByName = candidates.ToDictionary(x => x.Name, x => x, StringComparer.Ordinal);
        activeStrongIdsByName = candidates.Where(x => x.IdentityStrength == NodeIdentityStrength.Strong &&
            NodeIdentity.IsStrong(x.NodeId)).ToDictionary(x => x.Name, x => x.NodeId, StringComparer.Ordinal);
        activeNamesByStrongId = candidates.Where(x => x.IdentityStrength == NodeIdentityStrength.Strong &&
            NodeIdentity.IsStrong(x.NodeId)).GroupBy(x => x.NodeId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.OrderBy(y => y.Name.Length)
                .ThenBy(y => y.Name, StringComparer.Ordinal).First().Name, StringComparer.Ordinal);
        return candidates;
    }

    private IList<CandidateLatencyMeasurement> MeasureCandidateLatencies(
        IList<CandidateNode> candidates, string current, IList<ServiceKind> requiredServices)
    {
        List<CandidateNode> alternatives = (candidates ?? new List<CandidateNode>())
            .Where(x => !String.Equals(x.Name, current, StringComparison.Ordinal)).ToList();
        Dictionary<string, int> delays = MeasureLiveDelays(alternatives);
        string[] ranked = alternatives.Where(x => delays.ContainsKey(x.Name) &&
                delays[x.Name] > 0 && delays[x.Name] < Int32.MaxValue)
            .OrderBy(x => delays[x.Name]).ThenBy(x => x.Name, StringComparer.Ordinal)
            .Take(10).Select(x => x.Name).ToArray();
        var measured = new List<CandidateLatencyMeasurement>();
        foreach (string name in ranked)
        {
            CheckStop();
            CandidateNode candidate = alternatives.First(x => x.Name == name);
            CandidateScanResult scan = scanner.ScanSelected(candidate, requiredServices
                .Concat(new[] { ServiceKind.ChatGPT,
                    ServiceKind.SteamApi, ServiceKind.Google }).Distinct().ToArray(),
                TimeSpan.FromSeconds(2));
            int mihomoDelay;
            if (!delays.TryGetValue(name, out mihomoDelay)) mihomoDelay = Int32.MaxValue;
            measured.Add(new CandidateLatencyMeasurement(name, scan.ExitCountryCode,
                mihomoDelay, scan.ServiceResults.OrderBy(x => x.Key)
                    .Select(x => new ServiceMeasurement(x.Key, x.Value.Passed,
                        x.Value.ElapsedMilliseconds, x.Value.Detail, x.Value.FailureKind)),
                clock.UtcNow));
        }
        IList<CandidateLatencyMeasurement> result = measured
            .OrderBy(x => CoreCandidateResponse(x))
            .ThenBy(x => WebsitePriorityLatency.Secondary(x))
            .ThenBy(x => x.Node, StringComparer.Ordinal)
            .ToList().AsReadOnly();
        logger.Write("candidate latency ranking measured=" + result.Count +
            " services=" + requiredServices.Distinct().Count());
        return result;
    }

    private static double CoreCandidateResponse(CandidateLatencyMeasurement candidate)
    {
        if (candidate == null) return Double.MaxValue;
        var values = new List<double>();
        foreach (ServiceKind service in CoreWebsitePolicy.Required)
        {
            ServiceMeasurement measurement = candidate.Services.FirstOrDefault(x => x.Service == service);
            if (measurement == null || !measurement.Available || measurement.Milliseconds <= 0)
                return Double.MaxValue;
            values.Add(measurement.Milliseconds);
        }
        return values.Count == 0 ? Double.MaxValue : values.Max();
    }

    private void PublishCandidateDiagnostics(int generation,
        IList<CandidateLatencyMeasurement> ranking, IDictionary<string, string> diagnostics,
        string selectedNode, string selectedOutcome)
    {
        var values = new List<CandidateLatencyMeasurement>();
        foreach (CandidateLatencyMeasurement candidate in ranking ??
            new List<CandidateLatencyMeasurement>())
        {
            string detail;
            if (!String.IsNullOrEmpty(selectedNode) &&
                String.Equals(candidate.Node, selectedNode, StringComparison.Ordinal) &&
                !String.IsNullOrEmpty(selectedOutcome)) detail = selectedOutcome;
            else if (diagnostics == null || !diagnostics.TryGetValue(candidate.Node, out detail))
                detail = CandidateDecisionText.Initial(candidate);
            values.Add(candidate.WithDecisionDetail(detail));
        }
        IList<CandidateLatencyMeasurement> published;
        candidateLatencyStore.TryPublish(generation, values.AsReadOnly(), out published);
    }

    private void RememberCandidateDecision(CandidateScanResult scan, string detail)
    {
        if (scan == null || String.IsNullOrWhiteSpace(scan.Name)) return;
        int generation = candidateLatencyStore.BeginMeasurement();
        var values = candidateLatencyStore.Current.ToList();
        int index = values.FindIndex(x => String.Equals(x.Node, scan.Name, StringComparison.Ordinal));
        CandidateLatencyMeasurement measured;
        if (index >= 0) measured = values[index].WithDecisionDetail(detail);
        else
            measured = new CandidateLatencyMeasurement(scan.Name, scan.ExitCountryCode, Int32.MaxValue,
                scan.ServiceResults.OrderBy(x => x.Key).Select(x => new ServiceMeasurement(x.Key,
                    x.Value.Passed, x.Value.ElapsedMilliseconds, x.Value.Detail, x.Value.FailureKind)),
                clock.UtcNow, detail);
        if (index >= 0) values[index] = measured;
        else values.Insert(0, measured);
        IList<CandidateLatencyMeasurement> published;
        candidateLatencyStore.TryPublish(generation, values.Take(10).ToList().AsReadOnly(), out published);
    }

    private NodeHealthRecord Record(CandidateScanResult result)
    {
        DateTime cooldown = result.Health == CandidateHealth.Transient ? clock.UtcNow.AddMinutes(5) :
            result.Health == CandidateHealth.ServiceFailed || result.Health == CandidateHealth.RegionBlocked
                ? clock.UtcNow.AddMinutes(30) : clock.UtcNow;
        return new NodeHealthRecord(result.Name, EvidenceKey(result.Name), result.Health, clock.UtcNow,
            cooldown, false);
    }

    private void ReconcileHealthState(HealthState state)
    {
        if (state == null) return;
        var records = new Dictionary<string, NodeHealthRecord>(StringComparer.Ordinal);
        foreach (NodeHealthRecord record in state.Records.Values)
        {
            string name;
            if (record != null && NodeIdentity.IsStrong(record.NodeId) &&
                activeNamesByStrongId.TryGetValue(record.NodeId, out name))
            {
                CandidateNode candidate;
                if (activeCandidatesByName.TryGetValue(name, out candidate))
                    records[name] = new NodeHealthRecord(name, record.NodeId, record.Health,
                        record.CheckedUtc, record.CooldownUntilUtc, record.ExplicitLocalExclusion);
            }
        }
        state.Records.Clear();
        foreach (KeyValuePair<string, NodeHealthRecord> item in records) state.Records[item.Key] = item.Value;
        string preferredName;
        if (NodeIdentity.IsStrong(state.PreferredNodeId) &&
            activeNamesByStrongId.TryGetValue(state.PreferredNodeId, out preferredName))
            state.PreferredNode = preferredName;
        else
        {
            state.PreferredNode = "";
            state.PreferredNodeId = "";
            state.PreferredNodeVerifiedUtc = DateTime.MinValue;
        }
    }

    private static string Fingerprint(IEnumerable<string> nodeKeys)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(string.Join("\n", (nodeKeys ?? Enumerable.Empty<string>())
            .Where(x => !String.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal)
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

    private string EvidenceKey(string name)
    {
        CandidateNode candidate;
        if (activeCandidatesByName != null && activeCandidatesByName.TryGetValue(name ?? "", out candidate) &&
            candidate.IdentityStrength == NodeIdentityStrength.Strong && NodeIdentity.IsStrong(candidate.NodeId))
            return candidate.NodeId;
        return nodeIdentitySource == null ? name ?? "" : "";
    }

    private void SaveExperience(DateTime now)
    {
        if (nodeIdentitySource != null && activeStrongIdsByName.Count > 0)
        {
            experience.Assurance.CaptureIdentities(activeStrongIdsByName);
            ReconcileAccountVerificationsForSave();
        }
        experienceStore.Save(experience, now);
    }

    private void ReconcileAccountVerifications()
    {
        if (experience.AccountVerifications == null)
            experience.AccountVerifications = new List<AccountVerificationRecord>();
        experience.AccountVerifications = experience.AccountVerifications.Where(x => x != null &&
            NodeIdentity.IsStrong(x.NodeId) && activeNamesByStrongId.ContainsKey(x.NodeId)).ToList();
        foreach (AccountVerificationRecord item in experience.AccountVerifications)
            item.Node = activeNamesByStrongId[item.NodeId];
    }

    private void ReconcileAccountVerificationsForSave()
    {
        if (experience.AccountVerifications == null)
            experience.AccountVerifications = new List<AccountVerificationRecord>();
        foreach (AccountVerificationRecord item in experience.AccountVerifications)
        {
            string nodeId;
            item.NodeId = item != null && !String.IsNullOrEmpty(item.Node) &&
                activeStrongIdsByName.TryGetValue(item.Node, out nodeId) ? nodeId : "";
        }
        experience.AccountVerifications = experience.AccountVerifications.Where(x => x != null &&
            NodeIdentity.IsStrong(x.NodeId)).ToList();
    }

    private static bool QualityMatches(QualitySample sample, string nodeKey)
    {
        if (sample == null || String.IsNullOrEmpty(nodeKey)) return false;
        if (NodeIdentity.IsStrong(nodeKey)) return String.Equals(sample.NodeId, nodeKey, StringComparison.Ordinal);
        return !NodeIdentity.IsStrong(sample.NodeId) && String.Equals(sample.Name, nodeKey, StringComparison.Ordinal);
    }

    private static QualitySample Latest(IEnumerable<QualitySample> samples, string nodeKey)
    {
        return (samples ?? Enumerable.Empty<QualitySample>()).Where(x => QualityMatches(x, nodeKey))
            .OrderByDescending(x => x.CheckedUtc).FirstOrDefault();
    }

    private static double MedianResponse(IEnumerable<QualitySample> samples, string nodeKey, double current)
    {
        var values = samples.Where(x => QualityMatches(x, nodeKey) && x.ResponseMedianMs > 0)
            .OrderByDescending(x => x.CheckedUtc)
            .Take(LatencyWindowStatistics.MaximumSamples - 1).Select(x => x.ResponseMedianMs).Reverse()
            .Concat(new[] { current });
        return LatencyWindowStatistics.Summarize(values).MedianMilliseconds;
    }

    private static double Jitter(IEnumerable<QualitySample> samples, string nodeKey, double current)
    {
        var values = samples.Where(x => QualityMatches(x, nodeKey) && x.ResponseMedianMs > 0)
            .OrderByDescending(x => x.CheckedUtc)
            .Take(LatencyWindowStatistics.MaximumSamples - 1).Select(x => x.ResponseMedianMs).Reverse()
            .Concat(new[] { current });
        return LatencyWindowStatistics.Summarize(values).JitterMilliseconds;
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
