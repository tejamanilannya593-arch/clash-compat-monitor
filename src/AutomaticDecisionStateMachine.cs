using System;
using System.Collections.Generic;
using System.Linq;

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

public enum AutomaticDecisionState
{
    Healthy,
    Degraded,
    EnvironmentBlocked,
    ConfirmingFailure,
    Recovering,
    SearchingOptimization,
    ConfirmingOptimization,
    Switching,
    Observing,
    Cooldown,
    OutageSuppressed,
    Stabilization
}

public enum AutomaticDecisionEvent
{
    HealthyEvidence,
    NormalDegradation,
    SevereDegradation,
    HardFailure,
    FailureRecovered,
    FailureConfirmed,
    RecoveryTargetReady,
    NoRecoveryTarget,
    OptimizationDue,
    OptimizationTargetPrepared,
    OptimizationRejected,
    SwitchAuthorized,
    SwitchSucceeded,
    SwitchFailed,
    ObservationComplete,
    RollbackRequired,
    CooldownExpired,
    OutageDetected,
    OutageExpired,
    BudgetExhausted,
    BudgetReleased,
    EnvironmentBlocked,
    EnvironmentRestored,
    ManualNodeChanged
}

public enum AutomaticDecisionDirective
{
    None,
    ConfirmCurrent,
    SearchRecovery,
    SearchOptimization,
    ConfirmOptimization,
    Switch,
    Observe,
    Rollback,
    Cancel
}

public sealed class AutomaticDecisionTransaction
{
    public AutomaticDecisionState State { get; set; }
    public string Scope { get; set; }
    public string Current { get; set; }
    public string Target { get; set; }
    public string Previous { get; set; }
    public ServiceKind? Service { get; set; }
    public DecisionEvidenceClass Evidence { get; set; }
    public double BaselineResponse { get; set; }
    public double TargetResponse { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public string Reason { get; set; }
    public long Revision { get; set; }

    public AutomaticDecisionTransaction Copy()
    {
        return new AutomaticDecisionTransaction {
            State = State, Scope = Scope, Current = Current, Target = Target,
            Previous = Previous, Service = Service, Evidence = Evidence,
            BaselineResponse = BaselineResponse, TargetResponse = TargetResponse,
            StartedUtc = StartedUtc, ExpiresUtc = ExpiresUtc, Reason = Reason,
            Revision = Revision
        };
    }
}

public sealed class AutomaticDecisionContext
{
    public DateTime NowUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public string Scope { get; set; }
    public string Current { get; set; }
    public string Target { get; set; }
    public string Previous { get; set; }
    public ServiceKind? Service { get; set; }
    public DecisionEvidenceClass Evidence { get; set; }
    public double BaselineResponse { get; set; }
    public double TargetResponse { get; set; }
    public string Reason { get; set; }
}

public sealed class DecisionTransition
{
    public AutomaticDecisionState PreviousState { get; set; }
    public AutomaticDecisionEvent Event { get; set; }
    public AutomaticDecisionTransaction Transaction { get; set; }
    public AutomaticDecisionDirective Directive { get; set; }
    public bool Accepted { get; set; }
    public string Reason { get; set; }
}

public static class AutomaticDecisionStateMachine
{
    private static AutomaticDecisionTransaction Initial()
    {
        return new AutomaticDecisionTransaction {
            State = AutomaticDecisionState.Healthy,
            Evidence = DecisionEvidenceClass.Unknown
        };
    }

    public static DecisionTransition Transition(AutomaticDecisionTransaction current,
        AutomaticDecisionEvent value, AutomaticDecisionContext context)
    {
        AutomaticDecisionTransaction source = current ?? Initial();
        AutomaticDecisionContext details = context ?? new AutomaticDecisionContext();
        AutomaticDecisionTransaction next = source.Copy();

        if (value == AutomaticDecisionEvent.ManualNodeChanged)
        {
            next.Current = details.Current ?? next.Current;
            next.Target = null;
            next.Previous = null;
            next.Service = null;
            next.Evidence = DecisionEvidenceClass.Unknown;
            next.ExpiresUtc = details.ExpiresUtc != DateTime.MinValue
                ? details.ExpiresUtc : details.NowUtc.AddMinutes(10);
            return Move(source, next, value, AutomaticDecisionState.Cooldown,
                AutomaticDecisionDirective.Cancel, details, "manual node change");
        }
        if (value == AutomaticDecisionEvent.HardFailure)
        {
            next.Target = null;
            next.Previous = null;
            next.Service = details.Service;
            next.Evidence = DecisionEvidenceClass.HardFailure;
            return Move(source, next, value, AutomaticDecisionState.ConfirmingFailure,
                AutomaticDecisionDirective.ConfirmCurrent, details, "hard failure requires confirmation");
        }
        if (value == AutomaticDecisionEvent.EnvironmentBlocked)
            return Move(source, next, value, AutomaticDecisionState.EnvironmentBlocked,
                AutomaticDecisionDirective.Cancel, details, "environment blocked");

        switch (value)
        {
            case AutomaticDecisionEvent.HealthyEvidence:
            case AutomaticDecisionEvent.FailureRecovered:
                return Move(source, next, value, AutomaticDecisionState.Healthy,
                    AutomaticDecisionDirective.None, details, "current evidence healthy");
            case AutomaticDecisionEvent.NormalDegradation:
                return Move(source, next, value, AutomaticDecisionState.Degraded,
                    AutomaticDecisionDirective.None, details, "normal degradation");
            case AutomaticDecisionEvent.SevereDegradation:
                return Move(source, next, value, AutomaticDecisionState.Recovering,
                    AutomaticDecisionDirective.SearchRecovery, details, "severe degradation search");
            case AutomaticDecisionEvent.FailureConfirmed:
                if (source.State == AutomaticDecisionState.ConfirmingFailure)
                    return Move(source, next, value, AutomaticDecisionState.Recovering,
                        AutomaticDecisionDirective.SearchRecovery, details, "failure confirmed");
                break;
            case AutomaticDecisionEvent.RecoveryTargetReady:
                if (source.State == AutomaticDecisionState.Recovering)
                    return PrepareSwitch(source, next, value, details, "recovery target ready");
                break;
            case AutomaticDecisionEvent.NoRecoveryTarget:
            case AutomaticDecisionEvent.OptimizationRejected:
            case AutomaticDecisionEvent.SwitchFailed:
                return Move(source, next, value, AutomaticDecisionState.Degraded,
                    AutomaticDecisionDirective.None, details, "automatic decision did not switch");
            case AutomaticDecisionEvent.OptimizationDue:
                if (source.State == AutomaticDecisionState.Healthy ||
                    source.State == AutomaticDecisionState.Degraded)
                    return Move(source, next, value, AutomaticDecisionState.SearchingOptimization,
                        AutomaticDecisionDirective.SearchOptimization, details, "optimization search due");
                break;
            case AutomaticDecisionEvent.OptimizationTargetPrepared:
                if (source.State == AutomaticDecisionState.SearchingOptimization)
                {
                    next.Target = details.Target;
                    next.BaselineResponse = details.BaselineResponse;
                    next.TargetResponse = details.TargetResponse;
                    return Move(source, next, value, AutomaticDecisionState.ConfirmingOptimization,
                        AutomaticDecisionDirective.ConfirmOptimization, details, "optimization target prepared");
                }
                break;
            case AutomaticDecisionEvent.SwitchAuthorized:
                if (source.State == AutomaticDecisionState.Recovering ||
                    source.State == AutomaticDecisionState.ConfirmingOptimization)
                    return PrepareSwitch(source, next, value, details, "switch authorized");
                break;
            case AutomaticDecisionEvent.SwitchSucceeded:
                if (source.State == AutomaticDecisionState.Switching)
                    return Move(source, next, value, AutomaticDecisionState.Observing,
                        AutomaticDecisionDirective.Observe, details, "switch succeeded");
                break;
            case AutomaticDecisionEvent.RollbackRequired:
                if (source.State == AutomaticDecisionState.Observing)
                {
                    next.Target = source.Previous;
                    return Move(source, next, value, AutomaticDecisionState.Switching,
                        AutomaticDecisionDirective.Rollback, details, "rollback required");
                }
                break;
            case AutomaticDecisionEvent.ObservationComplete:
                if (source.State == AutomaticDecisionState.Observing)
                {
                    next.Target = null;
                    next.Previous = null;
                    return Move(source, next, value, AutomaticDecisionState.Cooldown,
                        AutomaticDecisionDirective.None, details, "observation complete");
                }
                break;
            case AutomaticDecisionEvent.CooldownExpired:
                if (source.State == AutomaticDecisionState.Cooldown &&
                    (source.ExpiresUtc == DateTime.MinValue || details.NowUtc >= source.ExpiresUtc))
                    return Move(source, next, value, AutomaticDecisionState.Healthy,
                        AutomaticDecisionDirective.None, details, "cooldown expired");
                break;
            case AutomaticDecisionEvent.OutageDetected:
                return Move(source, next, value, AutomaticDecisionState.OutageSuppressed,
                    AutomaticDecisionDirective.Cancel, details, "service outage suppressed");
            case AutomaticDecisionEvent.OutageExpired:
                if (source.State == AutomaticDecisionState.OutageSuppressed)
                    return Move(source, next, value, AutomaticDecisionState.Healthy,
                        AutomaticDecisionDirective.None, details, "service outage expired");
                break;
            case AutomaticDecisionEvent.BudgetExhausted:
                return Move(source, next, value, AutomaticDecisionState.Stabilization,
                    AutomaticDecisionDirective.Cancel, details, "automatic switch budget exhausted");
            case AutomaticDecisionEvent.BudgetReleased:
                if (source.State == AutomaticDecisionState.Stabilization &&
                    (source.ExpiresUtc == DateTime.MinValue || details.NowUtc >= source.ExpiresUtc))
                    return Move(source, next, value, AutomaticDecisionState.Healthy,
                        AutomaticDecisionDirective.None, details, "automatic switch budget released");
                break;
            case AutomaticDecisionEvent.EnvironmentRestored:
                if (source.State == AutomaticDecisionState.EnvironmentBlocked)
                    return Move(source, next, value, AutomaticDecisionState.Healthy,
                        AutomaticDecisionDirective.None, details, "environment restored");
                break;
        }
        return new DecisionTransition {
            PreviousState = source.State, Event = value, Transaction = source.Copy(),
            Directive = AutomaticDecisionDirective.None, Accepted = false,
            Reason = "event not allowed from current state"
        };
    }

    private static DecisionTransition PrepareSwitch(AutomaticDecisionTransaction source,
        AutomaticDecisionTransaction next, AutomaticDecisionEvent value,
        AutomaticDecisionContext details, string reason)
    {
        next.Previous = details.Previous ?? source.Current;
        next.Target = details.Target ?? source.Target;
        return Move(source, next, value, AutomaticDecisionState.Switching,
            AutomaticDecisionDirective.Switch, details, reason);
    }

    private static DecisionTransition Move(AutomaticDecisionTransaction source,
        AutomaticDecisionTransaction next, AutomaticDecisionEvent value,
        AutomaticDecisionState state, AutomaticDecisionDirective directive,
        AutomaticDecisionContext details, string fallbackReason)
    {
        next.State = state;
        if (!String.IsNullOrEmpty(details.Scope)) next.Scope = details.Scope;
        if (!String.IsNullOrEmpty(details.Current)) next.Current = details.Current;
        if (!String.IsNullOrEmpty(details.Target)) next.Target = details.Target;
        if (!String.IsNullOrEmpty(details.Previous)) next.Previous = details.Previous;
        if (details.Service.HasValue) next.Service = details.Service;
        if (details.Evidence != DecisionEvidenceClass.Unknown) next.Evidence = details.Evidence;
        if (details.BaselineResponse > 0) next.BaselineResponse = details.BaselineResponse;
        if (details.TargetResponse > 0) next.TargetResponse = details.TargetResponse;
        if (details.NowUtc != DateTime.MinValue) next.StartedUtc = details.NowUtc;
        if (details.ExpiresUtc != DateTime.MinValue) next.ExpiresUtc = details.ExpiresUtc;
        next.Reason = String.IsNullOrEmpty(details.Reason) ? fallbackReason : details.Reason;
        next.Revision = source.Revision + 1;
        return new DecisionTransition {
            PreviousState = source.State, Event = value, Transaction = next,
            Directive = directive, Accepted = true, Reason = next.Reason
        };
    }
}

public sealed class AutomaticSwitchRecord
{
    public DateTime Utc { get; set; }
    public string From { get; set; }
    public string To { get; set; }
    public string Reason { get; set; }
}

public sealed class SwitchBudgetDecision
{
    public bool Allowed { get; set; }
    public int TenMinuteCount { get; set; }
    public int ThirtyMinuteCount { get; set; }
    public DateTime AllowedAtUtc { get; set; }
    public string Reason { get; set; }
}

public static class SwitchBudgetPolicy
{
    public static SwitchBudgetDecision CanSwitch(IEnumerable<AutomaticSwitchRecord> records,
        DateTime nowUtc)
    {
        List<AutomaticSwitchRecord> recent = (records ?? Enumerable.Empty<AutomaticSwitchRecord>())
            .Where(x => x != null && x.Utc <= nowUtc && x.Utc > nowUtc.AddMinutes(-30))
            .OrderBy(x => x.Utc).ToList();
        List<AutomaticSwitchRecord> ten = recent.Where(x => x.Utc > nowUtc.AddMinutes(-10)).ToList();
        bool allowed = ten.Count < 2 && recent.Count < 4;
        DateTime allowedAt = nowUtc;
        if (ten.Count >= 2) allowedAt = Max(allowedAt, ten[ten.Count - 2].Utc.AddMinutes(10));
        if (recent.Count >= 4) allowedAt = Max(allowedAt, recent[recent.Count - 4].Utc.AddMinutes(30));
        return new SwitchBudgetDecision {
            Allowed = allowed,
            TenMinuteCount = ten.Count,
            ThirtyMinuteCount = recent.Count,
            AllowedAtUtc = allowedAt,
            Reason = allowed ? "automatic switch budget available" : "automatic switch budget exhausted"
        };
    }

    public static List<AutomaticSwitchRecord> Record(IEnumerable<AutomaticSwitchRecord> records,
        AutomaticSwitchRecord value)
    {
        DateTime now = value == null ? DateTime.UtcNow : value.Utc;
        var result = (records ?? Enumerable.Empty<AutomaticSwitchRecord>())
            .Where(x => x != null && x.Utc >= now.AddMinutes(-30)).ToList();
        if (value != null) result.Add(value);
        return result.OrderByDescending(x => x.Utc).Take(16).OrderBy(x => x.Utc).ToList();
    }

    private static DateTime Max(DateTime left, DateTime right)
    {
        return left >= right ? left : right;
    }
}
