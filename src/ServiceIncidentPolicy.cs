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

public sealed class ServiceIncidentConsensus
{
    public bool Passed { get; set; }
    public int DistinctFingerprintCount { get; set; }
    public int DistinctCountryCount { get; set; }
    public int DistinctAsnCount { get; set; }
    public string RejectionReason { get; set; }
}

public static class ServiceIncidentPolicy
{
    public static bool HasConsensus(CandidateScanResult current,
        IEnumerable<CandidateScanResult> alternatives, ServiceKind service)
    {
        return EvaluateConsensus(current, alternatives, service).Passed;
    }

    public static ServiceIncidentConsensus EvaluateConsensus(CandidateScanResult current,
        IEnumerable<CandidateScanResult> alternatives, ServiceKind service)
    {
        FailureEvidence currentEvidence;
        if (!TryDefiniteFailureEvidence(current, service, out currentEvidence))
            return Rejected("current-not-definite", new FailureEvidence[0]);
        var distinct = (alternatives ?? Enumerable.Empty<CandidateScanResult>())
            .Where(x => x != null && !String.Equals(x.Name, current.Name, StringComparison.Ordinal))
            .GroupBy(x => x.Name, StringComparer.Ordinal).Select(x => x.First()).ToList();
        if (distinct.Count < 2)
            return Rejected("insufficient-alternatives", new[] { currentEvidence });
        var evidence = new List<FailureEvidence> { currentEvidence };
        foreach (CandidateScanResult alternative in distinct)
        {
            FailureEvidence item;
            if (!TryDefiniteFailureEvidence(alternative, service, out item) ||
                item.FailureKind != currentEvidence.FailureKind)
                return Rejected("failure-kind-mismatch", evidence);
            evidence.Add(item);
        }
        ServiceIncidentConsensus result = Counts(evidence);
        if (result.DistinctFingerprintCount < 3)
        {
            result.RejectionReason = "insufficient-fingerprints";
            return result;
        }
        bool everyAsnKnown = evidence.All(x => x.ExitAsn.HasValue && x.ExitAsn.Value > 0);
        if (everyAsnKnown)
        {
            if (result.DistinctAsnCount < 2)
            {
                result.RejectionReason = "insufficient-asn-diversity";
                return result;
            }
        }
        else if (result.DistinctCountryCount < 2)
        {
            result.RejectionReason = "insufficient-country-diversity";
            return result;
        }
        result.Passed = true;
        result.RejectionReason = "none";
        return result;
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
        var observations = new Dictionary<ServiceKind, ServiceObservation>(scan.ServiceObservations);
        DateTime observedUtc = observations.Count == 0 ? DateTime.UtcNow :
            observations.Values.Where(x => x != null).Select(x => x.ObservedUtc)
                .DefaultIfEmpty(DateTime.UtcNow).Max();
        foreach (var item in results)
            if (!observations.ContainsKey(item.Key)) observations[item.Key] = ServiceObservation.FromProbe(
                item.Key, item.Value, observedUtc, scan.ExitFingerprint, scan.ExitCountryCode, scan.ExitAsn);
        var unprobed = blocked.Where(service => !results.ContainsKey(service)).ToList();
        foreach (ServiceKind service in blocked)
        {
            ServiceObservation observation;
            if (observations.TryGetValue(service, out observation) && observation != null)
                observations[service] = observation.WithNodeHealthCounting(false);
            else
            {
                ProbeResult notProbed = ProbeResult.Unverified("服务端点暂时熔断，本轮未探测，不归因于节点");
                results[service] = notProbed;
                observations[service] = ServiceObservation.FromProbe(service, notProbed, observedUtc,
                    scan.ExitFingerprint, scan.ExitCountryCode, scan.ExitAsn).WithNodeHealthCounting(false);
            }
        }
        bool failedServiceIsSuppressed = scan.FailedService.HasValue && unprobed.Contains(scan.FailedService.Value);
        bool definiteNodeFailure = !failedServiceIsSuppressed && (scan.Health == CandidateHealth.RegionBlocked ||
            scan.Health == CandidateHealth.ServiceFailed || scan.Health == CandidateHealth.Transient);
        return new CandidateScanResult(scan.Name,
            unprobed.Count == 0 || definiteNodeFailure ? scan.Health : CandidateHealth.Unknown,
            unprobed.Count == 0 || definiteNodeFailure ? scan.FailedService : null,
            unprobed.Count == 0 || definiteNodeFailure ? scan.Detail : "服务端点暂时熔断，已继续检测其他服务",
            scan.TotalMilliseconds, scan.ProbeCount, results, scan.ExitFingerprint, scan.ExitCountryCode,
            observations, scan.ExitAsn);
    }

    public static CandidateScanResult ForNodeHealth(CandidateScanResult scan)
    {
        if (scan == null) throw new ArgumentNullException("scan");
        if (scan.ServiceObservations == null || scan.ServiceObservations.Count == 0) return scan;
        var counted = scan.ServiceObservations.Where(x => x.Value != null &&
            x.Value.CountedForNodeHealth).ToDictionary(x => x.Key, x => x.Value);
        var results = scan.ServiceResults.Where(x => counted.ContainsKey(x.Key))
            .ToDictionary(x => x.Key, x => x.Value);
        ServiceObservation failure = counted.Values.Where(x => x.Outcome == ServiceOutcome.Failure)
            .OrderBy(x => x.Service).FirstOrDefault();
        CandidateHealth health;
        ServiceKind? failedService = null;
        string detail;
        if (failure != null)
        {
            failedService = failure.Service;
            health = failure.FailureKind == ProbeFailureKind.Region ? CandidateHealth.RegionBlocked :
                failure.FailureKind == ProbeFailureKind.Transient ? CandidateHealth.Transient :
                CandidateHealth.ServiceFailed;
            detail = failure.Detail;
        }
        else if (counted.Count == 0 || counted.Values.Any(x => x.Outcome == ServiceOutcome.Unknown))
        {
            health = CandidateHealth.Unknown;
            detail = "没有可归因于节点的完整服务证据";
        }
        else
        {
            health = CandidateHealth.Compatible;
            detail = "ok";
        }
        return new CandidateScanResult(scan.Name, health, failedService, detail,
            results.Values.Sum(x => x.ElapsedMilliseconds), results.Count, results,
            scan.ExitFingerprint, scan.ExitCountryCode, scan.ServiceObservations, scan.ExitAsn);
    }

    public static ProbeFailureKind FailureKind(CandidateScanResult scan, ServiceKind service)
    {
        FailureEvidence evidence;
        return TryDefiniteFailureEvidence(scan, service, out evidence) ?
            evidence.FailureKind : ProbeFailureKind.Unverified;
    }

    public static bool IsSuppressedFailure(CandidateScanResult scan,
        IEnumerable<ServiceKind> suppressed)
    {
        if (scan == null || !scan.FailedService.HasValue ||
            !(suppressed ?? Enumerable.Empty<ServiceKind>()).Contains(scan.FailedService.Value)) return false;
        ProbeResult result;
        return TryDefiniteFailure(scan, scan.FailedService.Value, out result);
    }

    private static bool TryDefiniteFailure(CandidateScanResult scan, ServiceKind service, out ProbeResult result)
    {
        result = null;
        if (scan == null || scan.ServiceResults == null || !scan.ServiceResults.TryGetValue(service, out result)) return false;
        return !result.Passed && result.FailureKind != ProbeFailureKind.Unverified && result.FailureKind != ProbeFailureKind.Partial;
    }

    private static bool TryDefiniteFailureEvidence(CandidateScanResult scan, ServiceKind service,
        out FailureEvidence evidence)
    {
        evidence = null;
        ServiceObservation observation;
        if (scan != null && scan.ServiceObservations != null &&
            scan.ServiceObservations.TryGetValue(service, out observation) && observation != null)
        {
            if (observation.Outcome != ServiceOutcome.Failure ||
                observation.FailureKind == ProbeFailureKind.Unverified ||
                observation.FailureKind == ProbeFailureKind.Partial) return false;
            evidence = new FailureEvidence { FailureKind = observation.FailureKind,
                ExitFingerprint = observation.ExitFingerprint ?? "",
                ExitCountryCode = observation.ExitCountryCode ?? "", ExitAsn = observation.ExitAsn };
            return true;
        }
        ProbeResult result;
        if (!TryDefiniteFailure(scan, service, out result)) return false;
        evidence = new FailureEvidence { FailureKind = result.FailureKind,
            ExitFingerprint = scan.ExitFingerprint ?? "", ExitCountryCode = scan.ExitCountryCode ?? "",
            ExitAsn = scan.ExitAsn };
        return true;
    }

    private static ServiceIncidentConsensus Rejected(string reason, IEnumerable<FailureEvidence> evidence)
    {
        ServiceIncidentConsensus result = Counts(evidence);
        result.RejectionReason = reason;
        return result;
    }

    private static ServiceIncidentConsensus Counts(IEnumerable<FailureEvidence> evidence)
    {
        List<FailureEvidence> items = (evidence ?? Enumerable.Empty<FailureEvidence>())
            .Where(x => x != null).ToList();
        return new ServiceIncidentConsensus {
            Passed = false,
            DistinctFingerprintCount = items.Select(x => x.ExitFingerprint)
                .Where(x => !String.IsNullOrEmpty(x)).Distinct(StringComparer.Ordinal).Count(),
            DistinctCountryCount = items.Select(x => x.ExitCountryCode)
                .Where(x => !String.IsNullOrEmpty(x)).Distinct(StringComparer.Ordinal).Count(),
            DistinctAsnCount = items.Where(x => x.ExitAsn.HasValue && x.ExitAsn.Value > 0)
                .Select(x => x.ExitAsn.Value).Distinct().Count(),
            RejectionReason = "unknown"
        };
    }

    private sealed class FailureEvidence
    {
        public ProbeFailureKind FailureKind { get; set; }
        public string ExitFingerprint { get; set; }
        public string ExitCountryCode { get; set; }
        public long? ExitAsn { get; set; }
    }
}
