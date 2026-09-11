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
}
