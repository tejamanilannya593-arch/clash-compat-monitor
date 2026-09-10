public static class StartupRecovery
{
    public static bool NeedsImmediateConfirmation(CandidateScanResult scan)
    {
        return scan != null && scan.FailedService.HasValue &&
            (scan.Health == CandidateHealth.Transient || scan.Health == CandidateHealth.ServiceFailed ||
             scan.Health == CandidateHealth.RegionBlocked);
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
