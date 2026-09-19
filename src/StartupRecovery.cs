using System;
using System.Linq;

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
        return scan != null && scan.Health == CandidateHealth.Transient;
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
        return new[] { ServiceKind.ChatGPT, ServiceKind.Gemini, ServiceKind.Google, ServiceKind.GitHub }
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
            .OrderBy(x => QualityMeasurement.ResponseMilliseconds(x, 5000))
            .ThenBy(x => delays != null && delays.ContainsKey(x.Name) ? delays[x.Name] : Int32.MaxValue)
            .Select(x => x.Name).ToArray();
    }

    public static bool IsEligibleQuickScan(CandidateScanResult scan, ServiceKind failedService)
    {
        return scan != null && AiRegionPolicy.SupportsBoth(scan.ExitCountryCode) &&
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
