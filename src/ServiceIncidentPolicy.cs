using System;
using System.Collections.Generic;
using System.Linq;

public sealed class ServiceIncidentRecord
{
    public ServiceKind Service { get; set; }
    public ProbeFailureKind FailureKind { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime UntilUtc { get; set; }
}

public static class ServiceIncidentPolicy
{
    public static bool HasConsensus(CandidateScanResult current,
        IEnumerable<CandidateScanResult> alternatives, ServiceKind service)
    {
        ProbeResult currentResult;
        if (!TryDefiniteFailure(current, service, out currentResult)) return false;
        var distinct = (alternatives ?? Enumerable.Empty<CandidateScanResult>())
            .Where(x => x != null && !String.Equals(x.Name, current.Name, StringComparison.Ordinal))
            .GroupBy(x => x.Name, StringComparer.Ordinal).Select(x => x.First()).ToList();
        if (distinct.Count < 2) return false;
        foreach (CandidateScanResult alternative in distinct)
        {
            ProbeResult result;
            if (!TryDefiniteFailure(alternative, service, out result) ||
                result.FailureKind != currentResult.FailureKind) return false;
        }
        return true;
    }

    public static void Open(IList<ServiceIncidentRecord> incidents, ServiceKind service,
        ProbeFailureKind failureKind, DateTime nowUtc, TimeSpan duration)
    {
        if (incidents == null) throw new ArgumentNullException("incidents");
        for (int i = incidents.Count - 1; i >= 0; i--)
            if (incidents[i] == null || incidents[i].Service == service) incidents.RemoveAt(i);
        incidents.Add(new ServiceIncidentRecord { Service = service, FailureKind = failureKind,
            StartedUtc = nowUtc, UntilUtc = nowUtc.Add(duration) });
    }

    public static bool IsActive(IEnumerable<ServiceIncidentRecord> incidents, ServiceKind service, DateTime nowUtc)
    {
        return (incidents ?? Enumerable.Empty<ServiceIncidentRecord>()).Any(x =>
            x != null && x.Service == service && x.StartedUtc <= nowUtc && x.UntilUtc > nowUtc);
    }

    public static IList<ServiceKind> ServicesToProbe(IEnumerable<ServiceKind> required,
        IEnumerable<ServiceIncidentRecord> incidents, DateTime nowUtc)
    {
        return (required ?? Enumerable.Empty<ServiceKind>()).Distinct()
            .Where(x => !IsActive(incidents, x, nowUtc)).ToList();
    }

    public static CandidateScanResult AttachSuppressed(CandidateScanResult scan,
        IEnumerable<ServiceKind> suppressed)
    {
        if (scan == null) throw new ArgumentNullException("scan");
        var blocked = (suppressed ?? Enumerable.Empty<ServiceKind>()).Distinct().ToList();
        if (blocked.Count == 0) return scan;
        var results = new Dictionary<ServiceKind, ProbeResult>(scan.ServiceResults);
        foreach (ServiceKind service in blocked)
            results[service] = ProbeResult.Unverified("服务端点暂时熔断，本轮不归因于节点");
        bool failedServiceIsSuppressed = scan.FailedService.HasValue && blocked.Contains(scan.FailedService.Value);
        bool definiteNodeFailure = !failedServiceIsSuppressed && (scan.Health == CandidateHealth.RegionBlocked ||
            scan.Health == CandidateHealth.ServiceFailed || scan.Health == CandidateHealth.Transient);
        return new CandidateScanResult(scan.Name,
            definiteNodeFailure ? scan.Health : CandidateHealth.Unknown,
            definiteNodeFailure ? scan.FailedService : null,
            definiteNodeFailure ? scan.Detail : "服务端点暂时熔断，已继续检测其他服务",
            scan.TotalMilliseconds, scan.ProbeCount, results);
    }

    public static ProbeFailureKind FailureKind(CandidateScanResult scan, ServiceKind service)
    {
        ProbeResult result;
        return TryDefiniteFailure(scan, service, out result) ? result.FailureKind : ProbeFailureKind.Unverified;
    }

    private static bool TryDefiniteFailure(CandidateScanResult scan, ServiceKind service, out ProbeResult result)
    {
        result = null;
        if (scan == null || scan.ServiceResults == null || !scan.ServiceResults.TryGetValue(service, out result)) return false;
        return !result.Passed && result.FailureKind != ProbeFailureKind.Unverified && result.FailureKind != ProbeFailureKind.Partial;
    }
}
