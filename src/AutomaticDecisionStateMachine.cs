using System;

public enum DecisionEvidenceClass
{
    Unknown,
    Healthy,
    NormalDegradation,
    SevereDegradation,
    HardFailure
}

public static class DecisionEvidencePolicy
{
    public static DecisionEvidenceClass Classify(CandidateScanResult scan,
        bool confirmedSevereLatency)
    {
        if (scan == null || scan.Health == CandidateHealth.Unknown)
            return DecisionEvidenceClass.Unknown;
        if (scan.Health == CandidateHealth.RegionBlocked ||
            scan.Health == CandidateHealth.ServiceFailed ||
            scan.Health == CandidateHealth.Transient)
            return DecisionEvidenceClass.HardFailure;
        if (!ServiceEvidencePolicy.CanHold(scan))
            return DecisionEvidenceClass.Unknown;
        if (confirmedSevereLatency && QualityPolicy.SeverelySlowService(scan).HasValue)
            return DecisionEvidenceClass.SevereDegradation;
        return QualityMeasurement.ResponseMilliseconds(scan, 5000) >
            QualityPolicy.OptimizationResponseMilliseconds
            ? DecisionEvidenceClass.NormalDegradation
            : DecisionEvidenceClass.Healthy;
    }
}

public sealed class MaterialImprovementDecision
{
    public bool Accepted { get; set; }
    public double RelativeImprovement { get; set; }
    public double AbsoluteImprovementMilliseconds { get; set; }
    public double RequiredRelativeImprovement { get; set; }
    public double RequiredAbsoluteImprovementMilliseconds { get; set; }
    public string Reason { get; set; }
}

public static class MaterialImprovementPolicy
{
    public const double NormalRelativeImprovement = 0.20;
    public const double RecentSwitchRelativeImprovement = 0.30;
    public const double AbsoluteImprovementMilliseconds = 200.0;

    public static MaterialImprovementDecision Evaluate(double baseline,
        double target, bool recentAutomaticSwitch)
    {
        double requiredRelative = recentAutomaticSwitch
            ? RecentSwitchRelativeImprovement : NormalRelativeImprovement;
        var decision = new MaterialImprovementDecision {
            RequiredRelativeImprovement = requiredRelative,
            RequiredAbsoluteImprovementMilliseconds = AbsoluteImprovementMilliseconds
        };
        if (Double.IsNaN(baseline) || Double.IsInfinity(baseline) || baseline <= 0 ||
            Double.IsNaN(target) || Double.IsInfinity(target) || target < 0)
        {
            decision.Reason = "invalid response evidence";
            return decision;
        }
        decision.AbsoluteImprovementMilliseconds = baseline - target;
        decision.RelativeImprovement = decision.AbsoluteImprovementMilliseconds / baseline;
        bool relativePassed = decision.RelativeImprovement >= requiredRelative;
        bool absolutePassed = decision.AbsoluteImprovementMilliseconds >= AbsoluteImprovementMilliseconds;
        decision.Accepted = relativePassed && absolutePassed;
        decision.Reason = decision.Accepted ? "material improvement confirmed" :
            !relativePassed ? "relative improvement below threshold" :
            "absolute improvement below threshold";
        return decision;
    }
}
