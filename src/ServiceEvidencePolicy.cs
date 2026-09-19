using System;
using System.Collections.Generic;
using System.Linq;

public static class ServiceEvidencePolicy
{
    public static bool CanHold(CandidateScanResult scan)
    {
        return scan != null && (scan.Health == CandidateHealth.Compatible ||
            scan.Health == CandidateHealth.BasicCompatible);
    }

    public static bool CanEmergencySwitch(CandidateScanResult scan)
    {
        return scan != null && scan.Health == CandidateHealth.Compatible;
    }

    public static bool CanFastFailoverTarget(CandidateScanResult scan, ServiceKind failedService)
    {
        if (!CanHold(scan)) return false;
        ProbeResult failedServiceResult;
        return scan.ServiceResults.TryGetValue(failedService, out failedServiceResult) &&
            failedServiceResult.Passed &&
            !scan.ServiceResults.Values.Any(x => !x.Passed &&
                x.FailureKind != ProbeFailureKind.Unverified) &&
            QualityPolicy.ServicesWithinFastFailoverLimit(scan);
    }

    public static bool CanQualitySwitch(CandidateScanResult scan, ExperienceData data, string scope,
        IEnumerable<ServiceKind> requiredServices, DateTime now)
    {
        return CanEmergencySwitch(scan);
    }

    public static bool CanRestoreAfterReload(CandidateScanResult scan, ExperienceData data, string scope,
        IEnumerable<ServiceKind> requiredServices, DateTime now)
    {
        return CanQualitySwitch(scan, data, scope, requiredServices, now);
    }
}
