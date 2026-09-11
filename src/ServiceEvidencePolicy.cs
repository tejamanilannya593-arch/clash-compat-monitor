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

    public static bool CanQualitySwitch(CandidateScanResult scan, ExperienceData data, string scope,
        IEnumerable<ServiceKind> requiredServices, DateTime now)
    {
        if (!CanEmergencySwitch(scan)) return false;
        foreach (ServiceKind service in (requiredServices ?? Enumerable.Empty<ServiceKind>())
            .Where(x => x == ServiceKind.ChatGPT || x == ServiceKind.Gemini).Distinct())
        {
            if (!AccountVerificationMemory.IsValid(data, scope, scan.Name, scan.ExitFingerprint,
                service, now, AccountVerificationMemory.CurrentRuleVersion)) return false;
        }
        return true;
    }
}
