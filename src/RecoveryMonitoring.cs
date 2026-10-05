using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

public sealed partial class MonitorWorker
{
    private MonitorSnapshot RunOnce(bool dryRun, UserPreferences preferences, MonitorCycleTrigger trigger)
    {
        if (preferences == null) throw new ArgumentNullException("preferences");
        UserPreferencePolicy.NormalizeRecoverySettings(preferences);
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            return MonitorSnapshot.CreateState(MonitorRunState.Checking, "检测正在进行", clock.UtcNow,
                clock.UtcNow.AddSeconds(preferences.CheckIntervalSeconds));
        try
        {
            cycleTimer = Stopwatch.StartNew();
            lastPreferences = preferences;
            RuntimeSnapshot runtime = runtimeSnapshotProvider();
            ConflictResult conflict = new ConflictDetector().Evaluate(runtime);
            if (conflict.Paused || !runtime.LocalLinkAvailable)
            {
                RecoveryState interrupted = experience.Assurance.Recovery;
                if (interrupted != null)
                {
                    interrupted.ResetCounts(interrupted.Node, interrupted.Scope);
                    SaveExperience(clock.UtcNow);
                }
                return MonitorSnapshot.CreateState(MonitorRunState.Degraded,
                    conflict.Paused ? conflict.Reason : "本机网络未连接，等待网络恢复", clock.UtcNow,
                    clock.UtcNow.AddSeconds(preferences.CheckIntervalSeconds));
            }

            BindSelection();
            IList<CandidateNode> candidates = DiscoverCandidates();
            var services = UserPreferencePolicy.NormalizeServices(preferences.RequiredServices);
            IList<string> keys = nodeIdentitySource == null ? candidates.Select(x => x.Name).ToList() :
                candidates.Where(x => NodeIdentity.IsStrong(x.NodeId)).Select(x => x.NodeId).Distinct().ToList();
            string fingerprint = Fingerprint(keys);
            string scope = experience.ResolveScope(fingerprint, keys, String.Join(",", services.OrderBy(x => x)));
            ConnectionAssurance assurance = experience.Assurance;
            // Old observations, pending optimizations and standby caches remain historical only.
            assurance.SetScope(scope);
            RecoveryState recovery = assurance.Recovery ?? (assurance.Recovery = new RecoveryState());
            string current = ReadCurrentSelection();
            if (!dryRun && !String.IsNullOrEmpty(recovery.PendingWritten))
            {
                RollbackRecovery(recovery);
                current = ReadCurrentSelection();
            }
            if (!String.Equals(experience.LastNode, current, StringComparison.Ordinal))
                experience.RecordChange(experience.LastNode, current, "检测到外部选择变更", clock.UtcNow);
            if (recovery.CooldownUntilUtc != DateTime.MinValue && clock.UtcNow >= recovery.CooldownUntilUtc)
            {
                logger.Write("recovery cooldown ended");
                recovery.CooldownUntilUtc = DateTime.MinValue;
            }
            CandidateNode currentNode = candidates.FirstOrDefault(x => x.Name == current) ?? new CandidateNode(current, null);
            var observed = new List<RecoveryCheck>();
            Report("正在检测基础网络、ChatGPT 和 Gemini：" + current, null);
            RecoveryCheck check = CheckRecoveryNode(currentNode, services);
            observed.Add(check);
            recovery.Observe(current, scope + "\n" + SelectionKey, check, clock.UtcNow, preferences.CheckIntervalSeconds);
            bool confirmFailure = recovery.NeedsFailureConfirmation(preferences.FailureThreshold);
            bool confirmSlow = recovery.SlowChecks >= 3;
            string decision = check.Healthy ? "当前节点正常，保持连接" : "证据不足或尚未达到失败阈值，保留当前节点";
            if (recovery.SlowChecks > 0)
                decision = "核心或基础响应超过 500 ms（连续 " + recovery.SlowChecks + "/3），继续检测";
            if (!check.Healthy || confirmSlow)
                logger.Write("recovery counts node=" + SafeName(current) + " basic=" + recovery.BasicFailures +
                    " chatgpt=" + recovery.ChatGptFailures + " gemini=" + recovery.GeminiFailures +
                    " slow=" + recovery.SlowChecks + " evidence=" + check.RejectionReason);
            bool confirmed = false;
            bool hardFailure = false;
            if (confirmFailure || confirmSlow)
            {
                Report("连续异常达到阈值，正在确认当前节点", null);
                RecoveryCheck confirmation = CheckRecoveryNode(currentNode, CoreWebsitePolicy.Required);
                observed.Add(confirmation);
                hardFailure = confirmFailure && recovery.ConfirmsFailure(confirmation, preferences.FailureThreshold);
                confirmed = hardFailure || (confirmSlow && confirmation.Healthy && confirmation.ResponseMilliseconds > 500);
                logger.Write("recovery confirmation confirmed=" + confirmed + " hard_failure=" + hardFailure +
                    " evidence=" + confirmation.RejectionReason + " response_ms=" + confirmation.ResponseMilliseconds);
                check = confirmation;
                if (!confirmed)
                {
                    recovery.ResetCounts(current, scope);
                    decision = "确认检测已恢复或证据不足，保留当前节点";
                }
            }
            if (confirmed)
            {
                decision = "故障已确认，等待可用候选";
                if (!preferences.AutomaticRecovery) decision = "故障已确认，自动故障恢复已关闭";
                else if (dryRun) decision = "故障已确认，只读检测未执行切换";
                else if (!hardFailure && clock.UtcNow < recovery.CooldownUntilUtc)
                {
                    decision = "持续高延迟已确认，冷却期间保持节点";
                    logger.Write("recovery latency blocked by cooldown until=" + recovery.CooldownUntilUtc.ToString("o"));
                }
                else if (ReadCurrentSelection() == current)
                {
                    RecoveryCheck replacement = RecoverConfirmed(current, candidates, recovery, observed, hardFailure);
                    if (replacement != null)
                    {
                        check = replacement;
                        decision = "故障恢复完成，新节点复验通过；冷却至 " + recovery.CooldownUntilUtc.ToLocalTime().ToString("HH:mm:ss");
                    }
                    else decision = "未找到复验通过的候选，已保留或安全回滚原选择";
                }
            }
            string actual = ReadCurrentSelection();
            if (actual != check.Scan.Name)
            {
                recovery.ResetCounts(actual, scope);
                decision = "节点选择已变化，等待下一轮检测";
                check = new RecoveryCheck { Basic = RecoveryEvidence.Unknown,
                    Scan = new CandidateScanResult(actual, CandidateHealth.Unknown, null, decision) };
            }
            experience.RecordChange(experience.LastNode, actual, "检测期间发现外部选择变更", clock.UtcNow);
            SaveRecoveryObservations(observed, scope, candidates, fingerprint);
            SaveExperience(clock.UtcNow);
            CandidateScanResult display = check.DisplayScan();
            decision = "基础网络：" + (check.Basic == RecoveryEvidence.Healthy ?
                "正常（" + check.BasicMilliseconds + " ms）" : check.Basic == RecoveryEvidence.Failed ? "失败" : "待验证") + " · " + decision;
            double? score = null;
            var quality = new QualityStateStore(config.QualityStatePath).Load();
            QualitySample latest = quality.LastOrDefault(x => x.Name == actual);
            if (latest != null) score = QualityScorer.Score(latest, quality, quality, clock.UtcNow).Score;
            string summary = experience.SelectionSummary(actual, firstCompletedCycle);
            firstCompletedCycle = false;
            if (!dryRun)
            {
                Exception error;
                if (!StatusReport.TryWriteLatest(Path.Combine(config.RootPath, "current-status.txt"),
                    StatusReport.Format(clock.UtcNow, actual, display.Health, score, decision, display.Detail), out error))
                    logger.TryWrite("status update skipped " + error.GetType().Name);
            }
            return MonitorSnapshot.CreateRunning(actual, display, score, decision, clock.UtcNow,
                clock.UtcNow.AddSeconds(preferences.CheckIntervalSeconds)).WithSelectionReason(summary)
                .WithCandidateLatencies(candidateLatencyStore.Current);
        }
        finally
        {
            SaveRegionEligibility();
            cycleTimer = null;
            Volatile.Write(ref running, 0);
            MemoryTrimmer.TrimIdleWorkingSet();
        }
    }

    private RecoveryCheck CheckRecoveryNode(CandidateNode node, IEnumerable<ServiceKind> services,
        int measuredBasicMilliseconds = Int32.MaxValue, TimeSpan? probeTimeout = null,
        bool firstPass = false)
    {
        CheckStop();
        bool measuredBasic = measuredBasicMilliseconds > 0 && measuredBasicMilliseconds < Int32.MaxValue;
        RecoveryEvidence basic = measuredBasic ? RecoveryEvidence.Healthy : RecoveryEvidence.Failed;
        int latency = measuredBasic ? measuredBasicMilliseconds : 0;
        // Independent reachability endpoints: an optional Google/Steam service failure is never a gate.
        foreach (string url in measuredBasic ? Enumerable.Empty<string>() : firstPass
            ? new[] { "https://cp.cloudflare.com/generate_204" }
            : new[] { config.DelayProbeUrl, "https://cp.cloudflare.com/generate_204" }.Distinct())
        {
            try
            {
                int measured;
                var detailed = mihomo as IRecoveryConnectivityClient;
                RecoveryEvidence result;
                int basicTimeout = firstPass ? 1500 : 3000;
                if (detailed != null) result = detailed.CheckConnectivity(node.Name, url, basicTimeout, out measured);
                else
                {
                    measured = mihomo.GetDelay(node.Name, url, basicTimeout);
                    result = measured >= 0 && measured < Int32.MaxValue ? RecoveryEvidence.Healthy : RecoveryEvidence.Unknown;
                }
                if (result == RecoveryEvidence.Healthy)
                { basic = result; latency = measured; break; }
                if (result == RecoveryEvidence.Unknown) basic = result;
            }
            catch (TimeoutException) { basic = RecoveryEvidence.Unknown; }
            catch (IOException) { basic = RecoveryEvidence.Unknown; }
            catch (InvalidOperationException) { basic = RecoveryEvidence.Unknown; }
        }
        if (firstPass && basic != RecoveryEvidence.Healthy)
            return new RecoveryCheck { Basic = basic, BasicMilliseconds = latency,
                Scan = new CandidateScanResult(node.Name, CandidateHealth.Unknown, null,
                    "Clash 延迟和备用基础连通性均未通过，跳过核心服务复检") };
        CandidateScanResult scan;
        try { scan = scanner.ScanSelected(node, services, probeTimeout ?? TimeSpan.FromSeconds(5)); }
        catch (Exception ex)
        {
            if (!(ex is IOException || ex is TimeoutException || ex is InvalidOperationException ||
                ex is System.Net.Http.HttpRequestException || ex is KeyNotFoundException)) throw;
            logger.Write("recovery probe evidence unknown node=" + SafeName(node.Name) + " error=" + ex.GetType().Name);
            scan = new CandidateScanResult(node.Name, CandidateHealth.Unknown, null, "探测未完成：" + ex.GetType().Name);
        }
        RememberRegionEligibility(experience.ActiveScope, scan);
        return new RecoveryCheck { Scan = scan, Basic = basic, BasicMilliseconds = latency };
    }

    private RecoveryCheck RecoverConfirmed(string original, IList<CandidateNode> candidates,
        RecoveryState recovery, List<RecoveryCheck> observations, bool hardFailure)
    {
        if (!lastPreferences.AutomaticRecovery || (!hardFailure && clock.UtcNow < recovery.CooldownUntilUtc)) return null;
        string expected = original;
        bool completed = false;
        try
        {
            Report("故障已确认，正在初筛候选节点", null);
            var alternatives = candidates.Where(x => x.Name != original).ToList();
            Dictionary<string, int> delays = MeasureLiveDelays(alternatives);
            var eligible = new List<RecoveryCheck>();
            foreach (CandidateNode node in alternatives.OrderBy(x => delays.ContainsKey(x.Name) ? delays[x.Name] : Int32.MaxValue))
            {
                CheckStop();
                if (ReadCurrentSelection() != expected) return null;
                int delay;
                if (!delays.TryGetValue(node.Name, out delay) || delay <= 0 || delay == Int32.MaxValue)
                { logger.Write("recovery candidate rejected node=" + SafeName(node.Name) + " reason=Clash delay unavailable"); continue; }
                RecoveryCheck tested = CheckRecoveryNode(node, CoreWebsitePolicy.Required);
                observations.Add(tested);
                if (tested.Healthy) eligible.Add(tested);
                else logger.Write("recovery candidate rejected node=" + SafeName(node.Name) + " " + tested.RejectionReason);
                // Leave time for final rechecks, verification and rollback in this bounded cycle.
                if (cycleTimer.Elapsed > TimeSpan.FromSeconds(90)) break;
            }
            foreach (RecoveryCheck candidate in eligible.OrderBy(x => x.ResponseMilliseconds))
            {
                CheckStop();
                if (ReadCurrentSelection() != expected) return null;
                CandidateNode node = candidates.First(x => x.Name == candidate.Scan.Name);
                RecoveryCheck fresh = CheckRecoveryNode(node, CoreWebsitePolicy.Required);
                observations.Add(fresh);
                if (!fresh.Healthy || (!hardFailure && fresh.ResponseMilliseconds > 500))
                { logger.Write("recovery final candidate rejected node=" + SafeName(node.Name) + " " + fresh.RejectionReason); continue; }
                CheckStop();
                if (ReadCurrentSelection() != expected) return null;
                recovery.PendingOriginal = original;
                recovery.PendingSelectorKey = SelectionKey;
                recovery.PendingPreviousWrite = expected == original ? null : expected;
                recovery.PendingWritten = node.Name;
                SaveExperience(clock.UtcNow); // Journal before writing so restart can safely recover.
                if (ReadCurrentSelection() != expected)
                { recovery.ClearTransaction(); return null; }
                logger.Write("recovery switch attempt from=" + SafeName(expected) + " to=" + SafeName(node.Name));
                try { WriteSelectedNode(expected, node.Name); }
                catch (Exception ex)
                {
                    if (!(ex is IOException || ex is TimeoutException || ex is InvalidOperationException)) throw;
                    logger.Write("recovery selector write uncertain " + ex.GetType().Name);
                }
                string selected = ReadCurrentSelection();
                if (selected != node.Name)
                {
                    if (selected != expected) return null;
                    recovery.PendingWritten = expected == original ? null : expected;
                    continue;
                }
                expected = node.Name;
                RecoveryCheck verified;
                try { verified = CheckRecoveryNode(node, CoreWebsitePolicy.Required); }
                catch (Exception ex)
                {
                    if (!(ex is IOException || ex is TimeoutException || ex is InvalidOperationException)) throw;
                    logger.Write("recovery post-switch verification error " + ex.GetType().Name);
                    continue;
                }
                observations.Add(verified);
                if (ReadCurrentSelection() != expected) return null;
                if (!verified.Healthy || (!hardFailure && verified.ResponseMilliseconds > 500))
                { logger.Write("recovery post-switch verification failed node=" + SafeName(expected) + " " + verified.RejectionReason); continue; }
                recovery.CooldownUntilUtc = clock.UtcNow.AddMinutes(lastPreferences.RecoveryCooldownMinutes);
                recovery.ResetCounts(expected, experience.ActiveScope);
                recovery.ClearTransaction();
                previousSelectedNode = original;
                experience.Assurance.Previous = original;
                experience.Assurance.RecordAutomaticSwitch(original, expected, "确认故障恢复，切换后复验通过", clock.UtcNow);
                experience.RecordChange(original, expected, "确认故障恢复，切换后复验通过", clock.UtcNow);
                SaveExperience(clock.UtcNow);
                completed = true;
                RunStatistics.SelectionConfirmed();
                // Synchronize only after a verified recovery, never during a healthy check.
                FollowGeneralNodeAfterRecovery(original, expected);
                logger.Write("recovery verified node=" + SafeName(expected) + " cooldown_started until=" + recovery.CooldownUntilUtc.ToString("o"));
                return verified;
            }
            return null;
        }
        finally
        {
            if (!completed) RollbackRecovery(recovery);
        }
    }

    private void RollbackRecovery(RecoveryState recovery)
    {
        if (String.IsNullOrEmpty(recovery.PendingWritten)) return;
        if (selectionBinding != null && (recovery.PendingSelectorKey != SelectionKey || !SelectionStillOwned()))
        {
            logger.Write("recovery rollback skipped: monitored selector path changed");
            recovery.ClearTransaction();
            SaveExperience(clock.UtcNow);
            return;
        }
        // Cleanup must also run after cancellation/time-budget expiry; do not call CheckStop here.
        string selected = ReadCurrentSelection();
        if ((selected == recovery.PendingWritten || selected == recovery.PendingPreviousWrite) &&
            mihomo.GetSelected(config.SharedGroup) == selected && !String.IsNullOrEmpty(recovery.PendingOriginal) &&
            mihomo.GetChoices(config.SharedGroup).Contains(recovery.PendingOriginal))
        {
            if (ReadCurrentSelection() != selected) return;
            WriteSelectedNode(selected, recovery.PendingOriginal);
            if (ReadCurrentSelection() != recovery.PendingOriginal)
                throw new IOException("回滚未确认，将保留恢复事务供下一轮处理");
            experience.RecordChange(selected, recovery.PendingOriginal, "候选复验失败，安全回滚原节点", clock.UtcNow);
            logger.Write("recovery rollback original=" + SafeName(recovery.PendingOriginal));
        }
        else logger.Write("recovery rollback skipped: selector changed externally or original absent");
        recovery.ClearTransaction();
        SaveExperience(clock.UtcNow);
    }

    private void SaveRecoveryObservations(IEnumerable<RecoveryCheck> observations, string scope,
        IList<CandidateNode> candidates, string fingerprint)
    {
        var stateStore = new StateStore(config.StatePath);
        HealthState health = stateStore.Load();
        var qualityStore = new QualityStateStore(config.QualityStatePath);
        List<QualitySample> history = qualityStore.Load();
        health.ApplySubscriptionFingerprint(fingerprint);
        foreach (RecoveryCheck check in observations)
        {
            CandidateScanResult scan = check.DisplayScan();
            CandidateNode node = candidates.FirstOrDefault(x => x.Name == scan.Name);
            var sample = new QualitySample(scan.Name, node == null ? "" : node.NodeId, clock.UtcNow,
                check.Healthy, check.ResponseMilliseconds, 0, 0, node == null ? null : node.Multiplier);
            history.Add(sample);
            string key = EvidenceKey(scan.Name);
            if (key.Length > 0) experience.Observe(scope, key, scan, sample, clock.UtcNow);
            health.Records[scan.Name] = Record(scan);
            if (check.Healthy) health.RememberPreferred(scan.Name, node == null ? "" : node.NodeId, scan.Health, clock.UtcNow);
        }
        stateStore.Save(health, candidates.Select(x => x.Name));
        qualityStore.Save(history);
    }
}
