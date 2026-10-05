using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

public sealed partial class MonitorWorker
{
    private MonitorSnapshot RunManualOptimization(UserPreferences preferences)
    {
        if (preferences == null) throw new ArgumentNullException("preferences");
        UserPreferencePolicy.NormalizeRecoverySettings(preferences);
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            return MonitorSnapshot.CreateState(MonitorRunState.Checking, "检测正在进行", clock.UtcNow,
                clock.UtcNow.AddSeconds(preferences.CheckIntervalSeconds));

        RecoveryState recovery = null;
        bool completed = false;
        var observations = new List<RecoveryCheck>();
        IList<CandidateNode> candidates = new List<CandidateNode>();
        string scope = "";
        string fingerprint = "";
        string original = "";
        RecoveryCheck display = null;
        string decision = "快速检查未找到实测更快且通过核心检测的候选，保持当前节点";
        try
        {
            cycleTimer = Stopwatch.StartNew();
            lastPreferences = preferences;
            RuntimeSnapshot runtime = runtimeSnapshotProvider();
            ConflictResult conflict = new ConflictDetector().Evaluate(runtime);
            if (conflict.Paused || !runtime.LocalLinkAvailable)
                return MonitorSnapshot.CreateState(MonitorRunState.Degraded,
                    conflict.Paused ? conflict.Reason : "本机网络未连接，无法强制寻优", clock.UtcNow,
                    clock.UtcNow.AddSeconds(preferences.CheckIntervalSeconds));

            BindSelection();
            candidates = DiscoverCandidates();
            IList<string> keys = nodeIdentitySource == null ? candidates.Select(x => x.Name).ToList() :
                candidates.Where(x => NodeIdentity.IsStrong(x.NodeId)).Select(x => x.NodeId).Distinct().ToList();
            fingerprint = Fingerprint(keys);
            scope = experience.ResolveScope(fingerprint, keys,
                String.Join(",", UserPreferencePolicy.NormalizeServices(preferences.RequiredServices).OrderBy(x => x)));
            experience.Assurance.SetScope(scope);
            recovery = experience.Assurance.Recovery ?? (experience.Assurance.Recovery = new RecoveryState());
            original = ReadCurrentSelection();
            if (!String.IsNullOrEmpty(recovery.PendingWritten)) RollbackRecovery(recovery);
            original = ReadCurrentSelection();
            CandidateNode originalNode = candidates.FirstOrDefault(x => x.Name == original) ??
                new CandidateNode(original, null);

            Report("用户已强制寻优：正在复检当前节点", null);
            RecoveryCheck baseline = CheckRecoveryNode(originalNode, CoreWebsitePolicy.Required);
            observations.Add(baseline);
            display = baseline;
            if (ReadCurrentSelection() != original)
                return ManualOptimizationSnapshot(display, "用户已更改节点，取消本次强制寻优", preferences);

            Report("正在用 Clash 延迟初筛候选节点", null);
            List<CandidateNode> alternatives = candidates.Where(x => x.Name != original).ToList();
            Dictionary<string, int> delays = MeasureLiveDelays(alternatives);
            var diagnostics = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (CandidateNode node in alternatives)
            {
                int delay;
                if (!delays.TryGetValue(node.Name, out delay) || delay <= 0 || delay == Int32.MaxValue)
                    diagnostics[node.Name] = "淘汰：Clash 延迟不可用";
            }
            List<CandidateNode> shortlist = alternatives
                .Where(x => delays.ContainsKey(x.Name) && delays[x.Name] > 0 && delays[x.Name] < Int32.MaxValue)
                .OrderBy(x => delays[x.Name]).ThenBy(x => x.Name, StringComparer.Ordinal)
                .Take(6).ToList();
            string expected = original;
            int checkedCandidates = 0;
            foreach (CandidateNode node in shortlist)
            {
                CheckStop();
                if (ReadCurrentSelection() != expected)
                    return ManualOptimizationSnapshot(display, "用户已更改节点，取消本次强制寻优", preferences);
                checkedCandidates++;
                Report("快速复检候选 " + checkedCandidates + "/" + shortlist.Count + "：" + node.Name, null);
                RecoveryCheck tested = CheckRecoveryNode(node, CoreWebsitePolicy.Required);
                observations.Add(tested);
                if (!tested.Healthy)
                {
                    diagnostics[node.Name] = "淘汰：" + tested.RejectionReason;
                    logger.Write("manual optimization rejected node=" + SafeName(node.Name) + " " + tested.RejectionReason);
                    if (cycleTimer.Elapsed > TimeSpan.FromSeconds(30)) break;
                    continue;
                }
                RecoveryCheck currentFresh = CheckRecoveryNode(originalNode, CoreWebsitePolicy.Required);
                observations.Add(currentFresh);
                if (currentFresh.Healthy && tested.ResponseMilliseconds >= currentFresh.ResponseMilliseconds)
                {
                    diagnostics[node.Name] = "复检通过，但不比当前节点快";
                    logger.Write("manual optimization candidate not faster node=" + SafeName(node.Name) +
                        " candidate_ms=" + tested.ResponseMilliseconds + " current_ms=" + currentFresh.ResponseMilliseconds);
                    if (cycleTimer.Elapsed > TimeSpan.FromSeconds(30)) break;
                    continue;
                }
                if (ReadCurrentSelection() != expected)
                    return ManualOptimizationSnapshot(display, "用户已更改节点，取消本次强制寻优", preferences);

                recovery.PendingOriginal = original;
                recovery.PendingSelectorKey = SelectionKey;
                recovery.PendingPreviousWrite = expected == original ? null : expected;
                recovery.PendingWritten = node.Name;
                SaveExperience(clock.UtcNow);
                if (ReadCurrentSelection() != expected)
                { recovery.ClearTransaction(); return ManualOptimizationSnapshot(display, "用户已更改节点，取消本次强制寻优", preferences); }
                logger.Write("manual optimization switch attempt from=" + SafeName(expected) + " to=" + SafeName(node.Name));
                try { WriteSelectedNode(expected, node.Name); }
                catch (Exception ex)
                {
                    if (!(ex is IOException || ex is TimeoutException || ex is InvalidOperationException)) throw;
                    logger.Write("manual optimization selector write uncertain " + ex.GetType().Name);
                }
                string selected = ReadCurrentSelection();
                if (selected != node.Name)
                {
                    if (selected != expected) return ManualOptimizationSnapshot(display,
                        "用户已更改节点，取消本次强制寻优", preferences);
                    recovery.PendingWritten = expected == original ? null : expected;
                    continue;
                }
                expected = node.Name;
                RecoveryCheck verified = CheckRecoveryNode(node, CoreWebsitePolicy.Required);
                observations.Add(verified);
                if (ReadCurrentSelection() != expected)
                    return ManualOptimizationSnapshot(display, "用户已更改节点，取消本次强制寻优", preferences);
                if (!verified.Healthy)
                {
                    diagnostics[node.Name] = "切换后复验失败，继续下一候选";
                    logger.Write("manual optimization post-switch verification failed node=" + SafeName(expected) + " " + verified.RejectionReason);
                    if (cycleTimer.Elapsed > TimeSpan.FromSeconds(30)) break;
                    continue;
                }

                recovery.CooldownUntilUtc = clock.UtcNow.AddMinutes(preferences.RecoveryCooldownMinutes);
                recovery.ResetCounts(expected, scope);
                recovery.ClearTransaction();
                previousSelectedNode = original;
                experience.Assurance.Previous = original;
                experience.Assurance.RecordAutomaticSwitch(original, expected,
                    "用户强制寻优，切换后复验通过", clock.UtcNow);
                experience.RecordChange(original, expected, "用户强制寻优，切换后复验通过", clock.UtcNow);
                SaveExperience(clock.UtcNow);
                RunStatistics.SelectionConfirmed();
                FollowGeneralNodeAfterRecovery(original, expected);
                logger.Write("manual optimization verified node=" + SafeName(expected));
                display = verified;
                decision = "用户强制寻优完成，新节点复验通过";
                completed = true;
                break;
            }

            PublishManualOptimizationDiagnostics(alternatives, delays, observations, diagnostics);
            if (!completed) RollbackRecovery(recovery);
            SaveRecoveryObservations(observations, scope, candidates, fingerprint);
            SaveExperience(clock.UtcNow);
            return ManualOptimizationSnapshot(display, decision, preferences);
        }
        finally
        {
            try
            {
                if (!completed && recovery != null) RollbackRecovery(recovery);
                SaveRegionEligibility();
            }
            finally
            {
                cycleTimer = null;
                Volatile.Write(ref running, 0);
                MemoryTrimmer.TrimIdleWorkingSet();
            }
        }
    }

    private MonitorSnapshot ManualOptimizationSnapshot(RecoveryCheck check, string decision,
        UserPreferences preferences)
    {
        string actual = ReadCurrentSelection();
        CandidateScanResult scan = check == null || check.Scan == null || check.Scan.Name != actual
            ? new CandidateScanResult(actual, CandidateHealth.Unknown, null, decision)
            : check.DisplayScan();
        return MonitorSnapshot.CreateRunning(actual, scan, null, decision, clock.UtcNow,
            clock.UtcNow.AddSeconds(preferences.CheckIntervalSeconds))
            .WithCandidateLatencies(candidateLatencyStore.Current);
    }

    private void PublishManualOptimizationDiagnostics(IList<CandidateNode> alternatives,
        IDictionary<string, int> delays, IList<RecoveryCheck> observations,
        IDictionary<string, string> diagnostics)
    {
        int generation = candidateLatencyStore.BeginMeasurement();
        var latest = observations.Where(x => x != null && x.Scan != null)
            .GroupBy(x => x.Scan.Name, StringComparer.Ordinal).ToDictionary(x => x.Key,
                x => x.Last(), StringComparer.Ordinal);
        var rows = new List<CandidateLatencyMeasurement>();
        foreach (CandidateNode node in alternatives)
        {
            RecoveryCheck check;
            int delay;
            string detail;
            if (!latest.TryGetValue(node.Name, out check)) check = null;
            if (!delays.TryGetValue(node.Name, out delay)) delay = Int32.MaxValue;
            if (!diagnostics.TryGetValue(node.Name, out detail))
                detail = check != null && check.Healthy ? "核心检测通过，等待复检比较" : "未完成实测";
            rows.Add(new CandidateLatencyMeasurement(node.Name,
                check == null ? "" : check.Scan.ExitCountryCode, delay,
                check == null ? new ServiceMeasurement[0] : check.Scan.ServiceResults.OrderBy(x => x.Key)
                    .Select(x => new ServiceMeasurement(x.Key, x.Value.Passed,
                        x.Value.ElapsedMilliseconds, x.Value.Detail, x.Value.FailureKind)),
                clock.UtcNow, detail));
        }
        IList<CandidateLatencyMeasurement> published;
        candidateLatencyStore.TryPublish(generation, rows.OrderBy(x => {
            RecoveryCheck value; return latest.TryGetValue(x.Node, out value) && value.Healthy
                ? value.ResponseMilliseconds : Double.MaxValue;
        }).ThenBy(x => x.MihomoMilliseconds).ThenBy(x => x.Node, StringComparer.Ordinal)
            .ToList().AsReadOnly(), out published);
    }
}
