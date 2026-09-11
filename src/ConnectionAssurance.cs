using System;
using System.Collections.Generic;
using System.Linq;

public sealed class StandbyNode
{
    public string Name { get; set; }
    public DateTime VerifiedUtc { get; set; }
}

public sealed class ConnectionAssurance
{
    public string Scope { get; set; }
    public List<StandbyNode> Standbys { get; set; }
    public string Previous { get; set; }
    public string Target { get; set; }
    public double PreviousResponse { get; set; }
    public bool QualitySwitch { get; set; }
    public int VerificationCount { get; set; }
    public int FailedChecks { get; set; }
    public int SlowerChecks { get; set; }
    public DateTime HoldUntilUtc { get; set; }
    public DateTime RefreshUtc { get; set; }
    public int StableCycles { get; set; }
    public ConnectionAssurance() { Standbys = new List<StandbyNode>(); }
    public static bool Passed(CandidateScanResult scan)
    { return ServiceEvidencePolicy.CanHold(scan); }
    public void SetScope(string scope)
    {
        if (Scope == scope) return;
        Scope = scope; Standbys.Clear(); Target = null; Previous = null; StableCycles = 0; RefreshUtc = DateTime.MinValue;
    }
    public void Remember(CandidateScanResult scan, string current, DateTime now)
    {
        Standbys.RemoveAll(x => x.Name == scan.Name || x.Name == current || x.VerifiedUtc < now.AddMinutes(-10));
        if (scan.Name != current && ServiceEvidencePolicy.CanEmergencySwitch(scan)) Standbys.Add(new StandbyNode { Name = scan.Name, VerifiedUtc = now });
        Standbys = Standbys.OrderByDescending(x => x.VerifiedUtc).Take(2).ToList();
    }
    public string[] Available(IEnumerable<string> eligible, string current, DateTime now)
    {
        var names = new HashSet<string>(eligible, StringComparer.Ordinal);
        return Standbys.Where(x => names.Contains(x.Name) && x.Name != current && x.VerifiedUtc <= now && x.VerifiedUtc >= now.AddMinutes(-10)).Select(x => x.Name).ToArray();
    }
    public void Begin(string previous, string target, double response, bool quality)
    { Previous = previous; Target = target; PreviousResponse = response; QualitySwitch = quality; VerificationCount = 0; FailedChecks = 0; SlowerChecks = 0; StableCycles = 0; }
    public bool NeedsRollback(CandidateScanResult scan)
    {
        VerificationCount++;
        FailedChecks = !Passed(scan) && scan.Health != CandidateHealth.Unknown ? FailedChecks + 1 : 0;
        SlowerChecks = Passed(scan) && QualitySwitch && PreviousResponse > 0 &&
            QualityMeasurement.ResponseMilliseconds(scan, 5000) > PreviousResponse * 1.25 ? SlowerChecks + 1 : 0;
        return (QualitySwitch && FailedChecks >= 1) || FailedChecks >= 2 || SlowerChecks >= 2;
    }
    public TimeSpan Interval(CandidateScanResult scan)
    {
        StableCycles = Passed(scan) ? Math.Min(5, StableCycles + 1) : 0;
        if (!String.IsNullOrEmpty(Target) || !Passed(scan)) return TimeSpan.FromSeconds(30);
        return TimeSpan.FromMinutes(StableCycles >= 3 ? 3 : 1);
    }
}
