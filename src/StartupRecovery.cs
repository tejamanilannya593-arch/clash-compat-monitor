using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

internal static class CandidateDelayMeasurement
{
    public static Dictionary<string, int> Measure(IMihomoClient mihomo,
        IEnumerable<CandidateNode> candidates, string url, int timeoutMilliseconds,
        int maximumConcurrency)
    {
        if (mihomo == null) throw new ArgumentNullException("mihomo");
        if (String.IsNullOrWhiteSpace(url)) throw new ArgumentException("A delay URL is required.", "url");
        if (timeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException("timeoutMilliseconds");
        if (maximumConcurrency <= 0) throw new ArgumentOutOfRangeException("maximumConcurrency");

        List<CandidateNode> nodes = (candidates ?? Enumerable.Empty<CandidateNode>())
            .Where(x => x != null && !String.IsNullOrWhiteSpace(x.Name))
            .GroupBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => x.First()).ToList();
        var values = Enumerable.Repeat(Int32.MaxValue, nodes.Count).ToArray();
        int nextIndex = -1;
        int workerCount = Math.Min(maximumConcurrency, nodes.Count);
        Task[] workers = Enumerable.Range(0, workerCount).Select(worker =>
            Task.Factory.StartNew(() => {
                while (true)
                {
                    int index = Interlocked.Increment(ref nextIndex);
                    if (index >= nodes.Count) return;
                    try
                    {
                        int delay = mihomo.GetDelay(nodes[index].Name, url, timeoutMilliseconds);
                        values[index] = delay > 0 ? delay : Int32.MaxValue;
                    }
                    catch (OutOfMemoryException) { throw; }
                    catch (StackOverflowException) { throw; }
                    catch (ThreadAbortException) { throw; }
                    catch (Exception) { values[index] = Int32.MaxValue; }
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning,
                TaskScheduler.Default)).ToArray();
        if (workers.Length > 0) Task.WaitAll(workers);

        return nodes.Select((node, index) => new { node.Name, Delay = values[index] })
            .ToDictionary(x => x.Name, x => x.Delay, StringComparer.Ordinal);
    }
}

public static class StartupRecovery
{
    public static bool NeedsImmediateConfirmation(CandidateScanResult scan)
    {
        return scan != null && scan.FailedService.HasValue &&
            (scan.Health == CandidateHealth.Transient || scan.Health == CandidateHealth.ServiceFailed ||
             scan.Health == CandidateHealth.RegionBlocked);
    }

    public static bool RequiresRepeatConfirmation(CandidateScanResult scan)
    {
        if (scan != null && scan.FailedService == ServiceKind.ChatGPT) return false;
        return scan != null && (scan.Health == CandidateHealth.Transient ||
            QualityPolicy.SeverelySlowService(scan).HasValue);
    }

    public static string[] RankImmediateChatGptTargets(
        System.Collections.Generic.IEnumerable<CandidateNode> candidates,
        System.Collections.Generic.IDictionary<string, int> clashDelays,
        System.Collections.Generic.IDictionary<string, int> chatGptDelays,
        string current)
    {
        return (candidates ?? new CandidateNode[0])
            .Where(x => x != null && x.Name != current &&
                HasLiveDelay(clashDelays, x.Name))
            .OrderBy(x => DelayOrMaximum(chatGptDelays, x.Name))
            .ThenBy(x => clashDelays[x.Name])
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => x.Name).ToArray();
    }

    public static string[] PreferFreshRecoveryTargets(IEnumerable<string> rankedTargets,
        IEnumerable<AutomaticSwitchRecord> switches, ServiceKind failedService, DateTime nowUtc)
    {
        var recentlyFailed = new HashSet<string>((switches ?? Enumerable.Empty<AutomaticSwitchRecord>())
            .Where(x => x != null && x.FailureService == failedService &&
                x.Utc <= nowUtc && x.Utc > nowUtc.AddMinutes(-10) &&
                !String.IsNullOrWhiteSpace(x.From))
            .Select(x => x.From), StringComparer.Ordinal);
        return (rankedTargets ?? Enumerable.Empty<string>())
            .Where(x => !String.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => recentlyFailed.Contains(x) ? 1 : 0)
            .ToArray();
    }

    public static double ChatGptResponseMilliseconds(CandidateScanResult scan, double fallback)
    {
        ProbeResult result;
        return scan != null && scan.ServiceResults != null &&
            scan.ServiceResults.TryGetValue(ServiceKind.ChatGPT, out result) && result != null &&
            result.ElapsedMilliseconds >= 0 ? result.ElapsedMilliseconds : fallback;
    }

    private static bool HasLiveDelay(
        System.Collections.Generic.IDictionary<string, int> delays, string node)
    {
        int value;
        return delays != null && delays.TryGetValue(node, out value) &&
            value > 0 && value < Int32.MaxValue;
    }

    private static int DelayOrMaximum(
        System.Collections.Generic.IDictionary<string, int> delays, string node)
    {
        int value;
        return delays != null && delays.TryGetValue(node, out value) && value > 0
            ? value : Int32.MaxValue;
    }

    public static bool ConfirmsSevereLatency(CandidateScanResult confirmation, ServiceKind service)
    {
        if (confirmation == null || confirmation.Health == CandidateHealth.Unknown ||
            confirmation.ServiceResults == null) return false;
        ProbeResult result;
        if (!confirmation.ServiceResults.TryGetValue(service, out result) || result == null ||
            result.FailureKind == ProbeFailureKind.Unverified) return false;
        return !result.Passed ||
            result.ElapsedMilliseconds > QualityPolicy.SevereServiceResponseMilliseconds;
    }

    public static ServiceKind? FastFailoverService(CandidateScanResult scan)
    {
        if (NeedsImmediateConfirmation(scan)) return scan.FailedService;
        if (!ServiceEvidencePolicy.CanHold(scan)) return null;
        return QualityPolicy.SeverelySlowService(scan);
    }

    public static CandidateScanResult ApplySuccessfulConfirmation(CandidateScanResult original,
        CandidateScanResult confirmation, ServiceKind service)
    {
        if (original == null || confirmation == null) return original;
        ProbeResult confirmed;
        if (!confirmation.ServiceResults.TryGetValue(service, out confirmed) || !confirmed.Passed) return original;
        var results = new System.Collections.Generic.Dictionary<ServiceKind, ProbeResult>(original.ServiceResults);
        ProbeResult previous;
        results.TryGetValue(service, out previous);
        results[service] = confirmed;
        ServiceKind? failedService = null;
        ProbeResult failure = null;
        bool pending = false;
        bool partial = false;
        foreach (var item in results)
        {
            if (!item.Value.Passed && item.Value.FailureKind != ProbeFailureKind.Unverified && !failedService.HasValue)
            { failedService = item.Key; failure = item.Value; }
            else if (item.Value.FailureKind == ProbeFailureKind.Unverified) pending = true;
            else if (item.Value.FailureKind == ProbeFailureKind.Partial) partial = true;
        }
        CandidateHealth health;
        string detail;
        if (failedService.HasValue)
        {
            health = failure.FailureKind == ProbeFailureKind.Region ? CandidateHealth.RegionBlocked :
                failure.FailureKind == ProbeFailureKind.Transient ? CandidateHealth.Transient : CandidateHealth.ServiceFailed;
            detail = failure.Detail;
        }
        else if (pending) { health = CandidateHealth.Unknown; detail = "复检通过，仍有项目待验证"; }
        else if (partial) { health = CandidateHealth.BasicCompatible; detail = "复检通过，基础连接可用"; }
        else { health = CandidateHealth.Compatible; detail = "复检通过"; }
        long total = original.TotalMilliseconds - (previous == null ? 0 : previous.ElapsedMilliseconds) + confirmed.ElapsedMilliseconds;
        return new CandidateScanResult(original.Name, health, failedService, detail, System.Math.Max(0, total),
            original.ProbeCount, results, original.ExitFingerprint, original.ExitCountryCode);
    }

    public static string[] RankFastCandidates(System.Collections.Generic.IEnumerable<CandidateNode> candidates,
        System.Collections.Generic.IDictionary<string, int> delays, string current, int maximum)
    {
        return (candidates ?? new CandidateNode[0]).Where(x => x != null && x.Name != current)
            .OrderBy(x => delays != null && delays.ContainsKey(x.Name) && delays[x.Name] > 0 ? delays[x.Name] : Int32.MaxValue)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .Take(System.Math.Max(0, maximum)).Select(x => x.Name).ToArray();
    }

    public static ServiceKind[] FastProbeServices(System.Collections.Generic.IEnumerable<ServiceKind> required,
        ServiceKind failedService)
    {
        return new[] { ServiceKind.ChatGPT, ServiceKind.SteamApi,
                ServiceKind.Google, ServiceKind.GitHub }
            .Concat(new[] { failedService }).Distinct().ToArray();
    }

    public static ServiceKind[] FullFailoverProbeServices(
        System.Collections.Generic.IEnumerable<ServiceKind> required, ServiceKind failedService)
    {
        return FastProbeServices(required, failedService)
            .Concat(required ?? new ServiceKind[0]).Distinct().ToArray();
    }

    public static string[] RankVerifiedFastTargets(System.Collections.Generic.IEnumerable<CandidateScanResult> scans,
        System.Collections.Generic.IDictionary<string, int> delays, ServiceKind failedService)
    {
        return (scans ?? new CandidateScanResult[0])
            .Where(x => IsEligibleQuickScan(x, failedService))
            .OrderBy(x => WebsitePriorityLatency.Primary(x))
            .ThenBy(x => WebsitePriorityLatency.Secondary(x))
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => x.Name).ToArray();
    }

    public static string[] RankPerformanceComparableTargets(
        System.Collections.Generic.IEnumerable<CandidateScanResult> scans,
        System.Collections.Generic.IDictionary<string, int> delays)
    {
        return (scans ?? new CandidateScanResult[0])
            .OrderBy(x => WebsitePriorityLatency.Primary(x))
            .ThenBy(x => WebsitePriorityLatency.Secondary(x))
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => x.Name).ToArray();
    }

    public static bool IsEligibleQuickScan(CandidateScanResult scan, ServiceKind failedService)
    {
        return scan != null && CoreWebsitePolicy.AllReachable(scan) &&
            AiRegionPolicy.SupportsChatGpt(scan.ExitCountryCode) &&
            ServiceEvidencePolicy.CanFastFailoverTarget(scan, failedService);
    }

    public static bool ShouldStopAfterEligibleCandidates(int eligibleCandidates)
    {
        return eligibleCandidates >= 3;
    }

    public static bool ShouldStopAfterCheckedCandidates(int checkedCandidates)
    {
        return checkedCandidates >= 8;
    }

    public static bool ShouldStopImmediateChatGptBatch(int checkedCandidates)
    {
        return checkedCandidates >= 12;
    }

    public static string FastSelectionSummary(double responseMilliseconds)
    {
        return responseMilliseconds <= 800 ? "实测优质节点" :
            "当前合格候选中延迟最低，但未达到 800 ms 优质标准";
    }

    public static string NextFullValidationTarget(
        System.Collections.Generic.IEnumerable<string> rankedTargets,
        System.Collections.Generic.IEnumerable<string> attemptedTargets)
    {
        var attempted = new System.Collections.Generic.HashSet<string>(
            attemptedTargets ?? new string[0], StringComparer.Ordinal);
        return (rankedTargets ?? new string[0])
            .FirstOrDefault(x => !String.IsNullOrWhiteSpace(x) && !attempted.Contains(x));
    }

    public static string[] OrderFastCandidates(
        System.Collections.Generic.IEnumerable<string> liveRanked,
        System.Collections.Generic.IEnumerable<string> standbys,
        System.Collections.Generic.IEnumerable<string> history,
        System.Collections.Generic.IEnumerable<string> recommendations,
        System.Collections.Generic.IEnumerable<string> allCandidates,
        string current, int maximum)
    {
        return (liveRanked ?? new string[0])
            .Concat(standbys ?? new string[0])
            .Concat(history ?? new string[0])
            .Concat(recommendations ?? new string[0])
            .Concat(allCandidates ?? new string[0])
            .Where(x => !String.IsNullOrWhiteSpace(x) && x != current)
            .Distinct(StringComparer.Ordinal)
            .Take(System.Math.Max(0, maximum)).ToArray();
    }

    public static bool ShouldStopFastComparison(int checkedCandidates, int qualifiedCandidates,
        double bestResponseMilliseconds)
    {
        return checkedCandidates >= 12 ||
            (checkedCandidates >= 5 && qualifiedCandidates >= 2 && bestResponseMilliseconds <= 800);
    }

    public static int MaximumCandidates(CandidateScanResult scan)
    {
        return scan != null && !ConnectionAssurance.Passed(scan) && scan.Health != CandidateHealth.Unknown ? 4 : 3;
    }

    public static bool RequiresFinalRecheck(System.TimeSpan age)
    {
        return age < System.TimeSpan.Zero || age > System.TimeSpan.FromSeconds(15);
    }
}
