using System;
using System.Collections.Generic;
using System.Linq;

public sealed class PendingOptimization
{
    public string Scope { get; set; }
    public string Current { get; set; }
    public string CurrentNodeId { get; set; }
    public string Target { get; set; }
    public string TargetNodeId { get; set; }
    public double BaselineResponse { get; set; }
    public double TargetResponse { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public static class OpportunityOptimizationPolicy
{
    public static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan ConfirmationInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(2);

    public static bool ShouldScan(bool enabled, bool observing,
        IEnumerable<double> currentResponses, DateTime lastScanUtc, DateTime nowUtc)
    {
        return enabled && !observing && QualityPolicy.CurrentNeedsOptimization(currentResponses) &&
            (lastScanUtc == DateTime.MinValue || nowUtc - lastScanUtc >= ScanInterval);
    }

    public static bool IsPerformanceComparable(CandidateScanResult scan,
        IEnumerable<ServiceKind> requiredServices)
    {
        if (scan == null || !AiRegionPolicy.SupportsBoth(scan.ExitCountryCode) ||
            !ServiceEvidencePolicy.CanHold(scan) || !QualityPolicy.ServicesWithinLimit(scan)) return false;
        var required = (requiredServices ?? Enumerable.Empty<ServiceKind>()).Distinct().ToList();
        if (required.Count == 0 || required.Any(service => !scan.ServiceResults.ContainsKey(service))) return false;
        return required.All(service => {
            ProbeResult result = scan.ServiceResults[service];
            return result != null && (result.Passed || result.FailureKind == ProbeFailureKind.Partial);
        });
    }

    public static bool MateriallyBetter(double baseline, double target)
    {
        return baseline > QualityPolicy.OptimizationResponseMilliseconds &&
            target <= QualityPolicy.OptimizationResponseMilliseconds && target <= baseline * 0.80;
    }

    public static bool IsFresh(PendingOptimization pending, string scope,
        string current, DateTime nowUtc)
    {
        return pending != null && String.Equals(pending.Scope, scope, StringComparison.Ordinal) &&
            String.Equals(pending.Current, current, StringComparison.Ordinal) &&
            pending.CreatedUtc <= nowUtc && pending.CreatedUtc >= nowUtc - PendingLifetime;
    }
}
