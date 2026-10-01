using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

public sealed partial class MonitorWorker
{
    private const int PreferredCoreLatencyMilliseconds = 800;

    private enum FastSwitchResult { Verified, Rejected, ExternalChange }

    private MonitorSnapshot RunContinuousOptimization(UserPreferences preferences,
        MonitorCycleTrigger trigger)
    {
        if (preferences == null) throw new ArgumentNullException("preferences");
        UserPreferencePolicy.NormalizeRecoverySettings(preferences);
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            return MonitorSnapshot.CreateState(MonitorRunState.Checking, "全节点寻优正在进行",
                clock.UtcNow, clock.UtcNow.AddSeconds(preferences.CheckIntervalSeconds));

        RecoveryState recovery = null;
        bool transactionCompleted = false;
        var observations = new List<RecoveryCheck>();
        IList<CandidateNode> candidates = new List<CandidateNode>();
        string scope = "";
        string fingerprint = "";
        string original = "";
        RecoveryCheck display = null;
        string decision = "全节点寻优未找到延迟和地区均合格的节点";
        try
        {
            cycleTimeBudget = TimeSpan.FromMinutes(7.5);
            cycleTimer = Stopwatch.StartNew();
            lastPreferences = preferences;
            RuntimeSnapshot runtime = runtimeSnapshotProvider();
            ConflictResult conflict = new ConflictDetector().Evaluate(runtime);
            if (conflict.Paused || !runtime.LocalLinkAvailable)
                return MonitorSnapshot.CreateState(MonitorRunState.Degraded,
                    conflict.Paused ? conflict.Reason : "本机网络未连接，暂停全节点寻优", clock.UtcNow,
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
            if (!String.IsNullOrEmpty(recovery.PendingWritten)) RollbackRecovery(recovery);
            original = ReadCurrentSelection();
            experience.RecordChange(experience.LastNode, original, "检测到外部选择变更", clock.UtcNow);

            bool forced = trigger == MonitorCycleTrigger.ManualOptimization;
            int currentDelay = Int32.MaxValue;
            if (!forced)
            {
                CandidateNode current = candidates.FirstOrDefault(x => x.Name == original);
                if (current != null)
                {
                    Report("正在复检当前节点是否达到 800 ms 标准", null);
                    Dictionary<string, int> currentDelays = MeasureLiveDelays(new[] { current });
                    currentDelays.TryGetValue(original, out currentDelay);
                    display = CheckWebsiteLatencyNode(current, currentDelay,
                        MeasureSingleWebsiteDelay(current, ServiceKind.ChatGPT),
                        MeasureSingleWebsiteDelay(current, ServiceKind.Gemini));
                    observations.Add(display);
                    if (IsPreferredCoreNode(display))
                    {
                        SaveExperience(clock.UtcNow);
                        return ContinuousOptimizationSnapshot(display,
                            "当前节点双核心延迟均不超过 800 ms，保持连接并暂停全节点寻优", preferences);
                    }
                }
            }

            Report("正在批量测量全部节点的基础、ChatGPT 和 Gemini 网站延迟", null);
            Dictionary<string, int> delays = MeasureLiveDelays(forced ? candidates :
                candidates.Where(x => x.Name != original));
            if (!forced && candidates.Any(x => x.Name == original)) delays[original] = currentDelay;
            Dictionary<string, int> chatGptDelays = MeasureUrlDelays(candidates,
                HttpServiceProbe.Endpoint(ServiceKind.ChatGPT).AbsoluteUri, 1800);
            Dictionary<string, int> geminiDelays = MeasureUrlDelays(candidates,
                HttpServiceProbe.Endpoint(ServiceKind.Gemini).AbsoluteUri, 1800);
            List<CandidateNode> ordered = candidates.OrderBy(x => CoreWebsiteDelayMaximum(
                    x.Name, chatGptDelays, geminiDelays))
                .ThenBy(x => CoreWebsiteDelayTotal(x.Name, chatGptDelays, geminiDelays))
                .ThenBy(x => delays.ContainsKey(x.Name) ? delays[x.Name] : Int32.MaxValue)
                .ThenBy(x => x.Name, StringComparer.Ordinal).ToList();
            var eligible = new List<RecoveryCheck>();
            var diagnostics = new Dictionary<string, string>(StringComparer.Ordinal);
            int index = 0;
            foreach (CandidateNode node in ordered)
            {
                CheckStop();
                if (ReadCurrentSelection() != original)
                    return ContinuousOptimizationSnapshot(display,
                        "用户已更改节点，取消本轮自动写入", preferences);
                index++;
                Report("按核心网站预排序复检 " + index + "/" + ordered.Count + "：" + node.Name, null);
                int measuredDelay;
                delays.TryGetValue(node.Name, out measuredDelay);
                RecoveryCheck check = !forced && node.Name == original && display != null ? display :
                    CheckWebsiteLatencyNode(node, measuredDelay,
                        chatGptDelays[node.Name], geminiDelays[node.Name]);
                if (check != display) observations.Add(check);
                if (String.Equals(node.Name, original, StringComparison.Ordinal)) display = check;
                if (IsLatencyEligible(check))
                {
                    eligible.Add(check);
                    diagnostics[node.Name] = "双核心网站延迟与地区要求通过（未验证登录）";
                    if (!forced && IsPreferredCoreNode(check))
                    {
                        if (node.Name == original)
                        {
                            PublishContinuousOptimizationDiagnostics(ordered, delays, observations, diagnostics, eligible);
                            SaveExperience(clock.UtcNow);
                            return ContinuousOptimizationSnapshot(check,
                                "当前节点双核心延迟均不超过 800 ms，保持连接并暂停全节点寻优", preferences);
                        }
                        RecoveryCheck verified;
                        FastSwitchResult result = TryFastContinuousSwitch(node, original, recovery,
                            scope, observations, out verified);
                        if (result == FastSwitchResult.ExternalChange)
                            return ContinuousOptimizationSnapshot(display,
                                "用户已更改节点，取消本轮自动写入", preferences);
                        if (result == FastSwitchResult.Verified)
                        {
                            transactionCompleted = true;
                            PublishContinuousOptimizationDiagnostics(ordered, delays, observations,
                                diagnostics, eligible);
                            SaveExperience(clock.UtcNow);
                            return ContinuousOptimizationSnapshot(verified,
                                "已切换至双核心延迟均不超过 800 ms 的节点，暂停后续寻优", preferences);
                        }
                        diagnostics[node.Name] = "切换后复验未达到 800 ms 标准，已回滚并继续寻找";
                        eligible.Remove(check);
                    }
                }
                else
                {
                    diagnostics[node.Name] = "淘汰：" +
                        (!AiRegionPolicy.SupportsBoth(check.Scan.ExitCountryCode)
                            ? "实际出口不符合 ChatGPT/Gemini 共同地区要求或无法确认"
                            : "基础、ChatGPT 或 Gemini 网站延迟不可测");
                    logger.Write("continuous optimization rejected node=" + SafeName(node.Name) +
                        " reason=" + diagnostics[node.Name]);
                }
            }

            List<RecoveryCheck> ranking = eligible
                .OrderBy(x => CoreMaximumMilliseconds(x))
                .ThenBy(x => CoreTotalMilliseconds(x))
                .ThenBy(x => delays.ContainsKey(x.Scan.Name) ? delays[x.Scan.Name] : Int32.MaxValue)
                .ThenBy(x => x.Scan.Name, StringComparer.Ordinal).ToList();
            PublishContinuousOptimizationDiagnostics(ordered, delays, observations, diagnostics, ranking);
            if (ranking.Count == 0)
            {
                SaveExperience(clock.UtcNow);
                return ContinuousOptimizationSnapshot(display, decision, preferences);
            }

            string expected = original;
            foreach (RecoveryCheck ranked in ranking)
            {
                CheckStop();
                CandidateNode target = candidates.First(x => x.Name == ranked.Scan.Name);
                if (ReadCurrentSelection() != expected)
                    return ContinuousOptimizationSnapshot(display,
                        "用户已更改节点，取消本轮自动写入", preferences);
                if (String.Equals(target.Name, expected, StringComparison.Ordinal))
                {
                    display = ranked;
                    decision = "全节点实测完成，当前节点排名第一";
                    transactionCompleted = true;
                    break;
                }

                recovery.PendingOriginal = original;
                recovery.PendingSelectorKey = SelectionKey;
                recovery.PendingPreviousWrite = expected == original ? null : expected;
                recovery.PendingWritten = target.Name;
                SaveExperience(clock.UtcNow);
                if (ReadCurrentSelection() != expected)
                {
                    recovery.ClearTransaction();
                    return ContinuousOptimizationSnapshot(display,
                        "用户已更改节点，取消本轮自动写入", preferences);
                }
                logger.Write("continuous optimization switch attempt from=" + SafeName(expected) +
                    " to=" + SafeName(target.Name));
                try { WriteSelectedNode(expected, target.Name); }
                catch (Exception ex)
                {
                    if (!(ex is IOException || ex is TimeoutException || ex is InvalidOperationException)) throw;
                    logger.Write("continuous optimization selector write uncertain " + ex.GetType().Name);
                }
                string selected = ReadCurrentSelection();
                if (selected != target.Name)
                {
                    if (selected != expected)
                        return ContinuousOptimizationSnapshot(display,
                            "用户已更改节点，取消本轮自动写入", preferences);
                    recovery.PendingWritten = expected == original ? null : expected;
                    continue;
                }
                expected = target.Name;
                RecoveryCheck verified = RecheckWebsiteLatencyNode(target);
                observations.Add(verified);
                if (ReadCurrentSelection() != expected)
                    return ContinuousOptimizationSnapshot(display,
                        "用户已更改节点，取消本轮自动写入", preferences);
                if (!IsLatencyEligible(verified))
                {
                    logger.Write("continuous optimization post-switch verification failed node=" +
                        SafeName(expected) + " " + verified.RejectionReason);
                    continue;
                }

                recovery.ResetCounts(expected, scope);
                recovery.ClearTransaction();
                previousSelectedNode = original;
                experience.Assurance.Previous = original;
                experience.Assurance.RecordAutomaticSwitch(original, expected,
                    "持续全节点寻优，双核心服务排名第一且复验通过", clock.UtcNow);
                experience.RecordChange(original, expected,
                    "持续全节点寻优，双核心服务排名第一且复验通过", clock.UtcNow);
                SaveExperience(clock.UtcNow);
                RunStatistics.SelectionConfirmed();
                FollowGeneralNodeAfterRecovery(original, expected);
                display = verified;
                decision = "全节点实测完成，已切换至 ChatGPT/Gemini 综合第一名";
                transactionCompleted = true;
                logger.Write("continuous optimization verified node=" + SafeName(expected));
                break;
            }

            if (!transactionCompleted) RollbackRecovery(recovery);
            SaveExperience(clock.UtcNow);
            return ContinuousOptimizationSnapshot(display, decision, preferences);
        }
        finally
        {
            try
            {
                if (!transactionCompleted && recovery != null) RollbackRecovery(recovery);
                SaveRegionEligibility();
            }
            finally
            {
                cycleTimer = null;
                cycleTimeBudget = TimeSpan.FromMinutes(3);
                Volatile.Write(ref running, 0);
                MemoryTrimmer.TrimIdleWorkingSet();
            }
        }
    }

    private static bool IsPreferredCoreNode(RecoveryCheck check)
    {
        return IsLatencyEligible(check) &&
            CoreMaximumMilliseconds(check) <= PreferredCoreLatencyMilliseconds;
    }

    private static bool IsLatencyEligible(RecoveryCheck check)
    {
        return check != null && check.Basic == RecoveryEvidence.Healthy && check.Scan != null &&
            AiRegionPolicy.SupportsBoth(check.Scan.ExitCountryCode) &&
            CoreMaximumMilliseconds(check) < Double.MaxValue;
    }

    private int MeasureSingleWebsiteDelay(CandidateNode node, ServiceKind service)
    {
        Dictionary<string, int> values = MeasureUrlDelays(new[] { node },
            HttpServiceProbe.Endpoint(service).AbsoluteUri, 1800);
        int value;
        return values.TryGetValue(node.Name, out value) ? value : Int32.MaxValue;
    }

    private RecoveryCheck RecheckWebsiteLatencyNode(CandidateNode node)
    {
        Dictionary<string, int> basics = MeasureLiveDelays(new[] { node });
        int basic;
        return CheckWebsiteLatencyNode(node, basics.TryGetValue(node.Name, out basic) ? basic : Int32.MaxValue,
            MeasureSingleWebsiteDelay(node, ServiceKind.ChatGPT),
            MeasureSingleWebsiteDelay(node, ServiceKind.Gemini));
    }

    private RecoveryCheck CheckWebsiteLatencyNode(CandidateNode node, int basic, int chatGpt, int gemini)
    {
        CheckStop();
        bool basicReady = basic > 0 && basic < Int32.MaxValue;
        bool chatReady = chatGpt > 0 && chatGpt < Int32.MaxValue;
        bool geminiReady = gemini > 0 && gemini < Int32.MaxValue;
        var sites = new Dictionary<ServiceKind, ProbeResult> {
            { ServiceKind.ChatGPT, chatReady ? ProbeResult.Partial("仅网站延迟，未验证登录或对话", chatGpt)
                : ProbeResult.Unverified("网站延迟不可测") },
            { ServiceKind.Gemini, geminiReady ? ProbeResult.Partial("仅网站延迟，未验证登录或对话", gemini)
                : ProbeResult.Unverified("网站延迟不可测") }
        };
        CandidateScanResult identity = null;
        if (basicReady && chatReady && geminiReady)
        {
            try { identity = scanner.ScanSelected(node, new ServiceKind[0], TimeSpan.FromSeconds(3)); }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is TimeoutException || ex is InvalidOperationException ||
                    ex is KeyNotFoundException)) throw;
                logger.Write("latency region probe unavailable node=" + SafeName(node.Name) +
                    " error=" + ex.GetType().Name);
            }
        }
        string country = identity == null ? "" : identity.ExitCountryCode;
        CandidateHealth health = !basicReady || !chatReady || !geminiReady ? CandidateHealth.Unknown :
            AiRegionPolicy.SupportsBoth(country) ? CandidateHealth.BasicCompatible :
                String.IsNullOrWhiteSpace(country) ? CandidateHealth.Unknown : CandidateHealth.RegionBlocked;
        return new RecoveryCheck { Basic = basicReady ? RecoveryEvidence.Healthy : RecoveryEvidence.Unknown,
            BasicMilliseconds = basicReady ? basic : 0,
            Scan = new CandidateScanResult(node.Name, health, null,
                "仅依据网站延迟和实际出口地区；未验证登录或对话",
                (long)(chatReady ? chatGpt : 0) + (geminiReady ? gemini : 0), 2, sites,
                identity == null ? "" : identity.ExitFingerprint, country,
                null, identity == null ? null : identity.ExitAsn) };
    }

    private static long CoreWebsiteDelayMaximum(string name, IDictionary<string, int> chatGpt,
        IDictionary<string, int> gemini)
    {
        int first, second;
        if (!chatGpt.TryGetValue(name, out first) || !gemini.TryGetValue(name, out second) ||
            first <= 0 || second <= 0 || first == Int32.MaxValue || second == Int32.MaxValue)
            return Int64.MaxValue;
        return Math.Max(first, second);
    }

    private static long CoreWebsiteDelayTotal(string name, IDictionary<string, int> chatGpt,
        IDictionary<string, int> gemini)
    {
        int first, second;
        if (!chatGpt.TryGetValue(name, out first) || !gemini.TryGetValue(name, out second) ||
            first <= 0 || second <= 0 || first == Int32.MaxValue || second == Int32.MaxValue)
            return Int64.MaxValue;
        return (long)first + second;
    }

    private FastSwitchResult TryFastContinuousSwitch(CandidateNode target, string original,
        RecoveryState recovery, string scope, IList<RecoveryCheck> observations,
        out RecoveryCheck verified)
    {
        verified = null;
        CheckStop();
        if (ReadCurrentSelection() != original) return FastSwitchResult.ExternalChange;
        recovery.PendingOriginal = original;
        recovery.PendingSelectorKey = SelectionKey;
        recovery.PendingPreviousWrite = null;
        recovery.PendingWritten = target.Name;
        SaveExperience(clock.UtcNow);
        if (ReadCurrentSelection() != original)
        {
            recovery.ClearTransaction();
            SaveExperience(clock.UtcNow);
            return FastSwitchResult.ExternalChange;
        }
        logger.Write("continuous optimization preferred switch attempt from=" + SafeName(original) +
            " to=" + SafeName(target.Name));
        try { WriteSelectedNode(original, target.Name); }
        catch (Exception ex)
        {
            if (!(ex is IOException || ex is TimeoutException || ex is InvalidOperationException)) throw;
            logger.Write("continuous optimization preferred selector write uncertain " + ex.GetType().Name);
        }
        string selected = ReadCurrentSelection();
        if (selected != target.Name)
        {
            if (selected != original) return FastSwitchResult.ExternalChange;
            recovery.ClearTransaction();
            SaveExperience(clock.UtcNow);
            return FastSwitchResult.Rejected;
        }
        verified = RecheckWebsiteLatencyNode(target);
        observations.Add(verified);
        if (ReadCurrentSelection() != target.Name) return FastSwitchResult.ExternalChange;
        if (!IsPreferredCoreNode(verified))
        {
            logger.Write("continuous optimization preferred post-switch verification failed node=" +
                SafeName(target.Name) + " " + verified.RejectionReason);
            RollbackRecovery(recovery);
            return FastSwitchResult.Rejected;
        }
        recovery.ResetCounts(target.Name, scope);
        recovery.ClearTransaction();
        previousSelectedNode = original;
        experience.Assurance.Previous = original;
        experience.Assurance.RecordAutomaticSwitch(original, target.Name,
            "双核心延迟不超过 800 ms，切换后复验通过", clock.UtcNow);
        experience.RecordChange(original, target.Name,
            "双核心延迟不超过 800 ms，切换后复验通过", clock.UtcNow);
        SaveExperience(clock.UtcNow);
        RunStatistics.SelectionConfirmed();
        FollowGeneralNodeAfterRecovery(original, target.Name);
        logger.Write("continuous optimization preferred verified node=" + SafeName(target.Name));
        return FastSwitchResult.Verified;
    }

    private static double CoreMaximumMilliseconds(RecoveryCheck check)
    {
        return CoreWebsitePolicy.Required.Max(service => CoreMilliseconds(check, service));
    }

    private static double CoreTotalMilliseconds(RecoveryCheck check)
    {
        return CoreWebsitePolicy.Required.Sum(service => CoreMilliseconds(check, service));
    }

    private static double CoreMilliseconds(RecoveryCheck check, ServiceKind service)
    {
        ProbeResult result;
        return check != null && check.Scan != null && check.Scan.ServiceResults.TryGetValue(service, out result) &&
            result != null && result.Passed &&
            (result.FailureKind == ProbeFailureKind.None || result.FailureKind == ProbeFailureKind.Partial)
            ? result.ElapsedMilliseconds : Double.MaxValue;
    }

    private MonitorSnapshot ContinuousOptimizationSnapshot(RecoveryCheck check, string decision,
        UserPreferences preferences)
    {
        string actual = ReadCurrentSelection();
        CandidateScanResult scan = check == null || check.Scan == null || check.Scan.Name != actual
            ? new CandidateScanResult(actual, CandidateHealth.Unknown, null, decision)
            : check.Scan;
        Exception error;
        if (!StatusReport.TryWriteLatest(Path.Combine(config.RootPath, "current-status.txt"),
            StatusReport.Format(clock.UtcNow, actual, scan.Health, null, decision, scan.Detail), out error))
            logger.TryWrite("status update skipped " + error.GetType().Name);
        string summary = experience.SelectionSummary(actual, firstCompletedCycle);
        firstCompletedCycle = false;
        return MonitorSnapshot.CreateRunning(actual, scan, null, decision, clock.UtcNow,
            clock.UtcNow.AddSeconds(preferences.CheckIntervalSeconds))
            .WithSelectionReason(summary)
            .WithCandidateLatencies(candidateLatencyStore.Current);
    }

    private void PublishContinuousOptimizationDiagnostics(IList<CandidateNode> ordered,
        IDictionary<string, int> delays, IList<RecoveryCheck> observations,
        IDictionary<string, string> diagnostics, IList<RecoveryCheck> ranking)
    {
        int generation = candidateLatencyStore.BeginMeasurement();
        var latest = observations.Where(x => x != null && x.Scan != null)
            .GroupBy(x => x.Scan.Name, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Last(), StringComparer.Ordinal);
        var ranks = ranking.Select((value, index) => new { value.Scan.Name, Rank = index + 1 })
            .ToDictionary(x => x.Name, x => x.Rank, StringComparer.Ordinal);
        var rows = new List<CandidateLatencyMeasurement>();
        foreach (CandidateNode node in ordered)
        {
            RecoveryCheck check;
            int delay;
            string detail;
            latest.TryGetValue(node.Name, out check);
            if (!delays.TryGetValue(node.Name, out delay)) delay = Int32.MaxValue;
            if (ranks.ContainsKey(node.Name)) detail = "网站延迟排名第 " + ranks[node.Name] + "（未验证登录）";
            else if (!diagnostics.TryGetValue(node.Name, out detail)) detail = "实测未完成";
            rows.Add(new CandidateLatencyMeasurement(node.Name,
                check == null ? "" : check.Scan.ExitCountryCode, delay,
                check == null ? new ServiceMeasurement[0] : check.Scan.ServiceResults.OrderBy(x => x.Key)
                    .Select(x => new ServiceMeasurement(x.Key, x.Value.Passed,
                        x.Value.ElapsedMilliseconds, x.Value.Detail, x.Value.FailureKind)),
                clock.UtcNow, detail));
        }
        IList<CandidateLatencyMeasurement> published;
        candidateLatencyStore.TryPublish(generation, rows.OrderBy(x => ranks.ContainsKey(x.Node)
                ? ranks[x.Node] : Int32.MaxValue)
            .ThenBy(x => x.MihomoMilliseconds).ThenBy(x => x.Node, StringComparer.Ordinal)
            .ToList().AsReadOnly(), out published);
    }
}
