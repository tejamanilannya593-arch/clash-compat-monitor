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
    public bool ProvisionalSwitch { get; set; }
    public DateTime StartedUtc { get; set; }
    public int VerificationCount { get; set; }
    public int FailedChecks { get; set; }
    public int SlowerChecks { get; set; }
    public DateTime HoldUntilUtc { get; set; }
    public DateTime RefreshUtc { get; set; }
    public int StableCycles { get; set; }
    public PendingOptimization PendingOptimization { get; set; }
    public DateTime LastOpportunityScanUtc { get; set; }
    public AutomaticDecisionTransaction Decision { get; set; }
    public List<AutomaticSwitchRecord> AutomaticSwitches { get; set; }
    public ConnectionAssurance()
    {
        Standbys = new List<StandbyNode>();
        AutomaticSwitches = new List<AutomaticSwitchRecord>();
        Decision = new AutomaticDecisionTransaction { State = AutomaticDecisionState.Healthy };
    }
    public static bool Passed(CandidateScanResult scan)
    { return ServiceEvidencePolicy.CanHold(scan); }
    public void SetScope(string scope)
    {
        if (Scope == scope) return;
        Scope = scope; Standbys.Clear(); Target = null; Previous = null; StartedUtc = DateTime.MinValue; StableCycles = 0;
        RefreshUtc = DateTime.MinValue; PendingOptimization = null; LastOpportunityScanUtc = DateTime.MinValue;
        Decision = new AutomaticDecisionTransaction {
            State = AutomaticDecisionState.Healthy, Scope = scope, Revision = 1
        };
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
    public void Begin(string previous, string target, double response, bool quality, bool provisional = false,
        DateTime? startedUtc = null)
    {
        Previous = previous; Target = target; PreviousResponse = response; QualitySwitch = quality;
        ProvisionalSwitch = provisional; StartedUtc = startedUtc ?? DateTime.UtcNow;
        VerificationCount = 0; FailedChecks = 0; SlowerChecks = 0; StableCycles = 0;
        PendingOptimization = null;
        if (Decision != null && Decision.State == AutomaticDecisionState.Switching)
        {
            DecisionTransition completed = AutomaticDecisionStateMachine.Transition(Decision,
                AutomaticDecisionEvent.SwitchSucceeded, new AutomaticDecisionContext {
                    NowUtc = StartedUtc, Current = target, Previous = previous, Target = target,
                    BaselineResponse = response,
                    Reason = quality ? "quality switch observing" : "recovery switch observing"
                });
            if (completed.Accepted)
            {
                Decision = completed.Transaction;
                return;
            }
        }
        long revision = Decision == null ? 1 : Decision.Revision + 1;
        Decision = new AutomaticDecisionTransaction {
            State = AutomaticDecisionState.Observing, Scope = Scope, Current = target,
            Previous = previous, Target = target, BaselineResponse = response,
            StartedUtc = StartedUtc, Revision = revision,
            Reason = quality ? "quality switch observing" : "recovery switch observing"
        };
    }
    public void ClearPendingOptimization()
    {
        PendingOptimization = null;
        if (Decision != null && Decision.State == AutomaticDecisionState.ConfirmingOptimization)
        {
            Decision.State = AutomaticDecisionState.Degraded;
            Decision.Target = null;
            Decision.Reason = "pending optimization cleared";
            Decision.Revision++;
        }
    }

    public AutomaticDecisionTransaction EnsureDecisionState(string current, DateTime now)
    {
        if (AutomaticSwitches == null) AutomaticSwitches = new List<AutomaticSwitchRecord>();
        bool legacy = Decision == null || (Decision.Revision == 0 &&
            Decision.State == AutomaticDecisionState.Healthy &&
            Decision.StartedUtc == DateTime.MinValue && String.IsNullOrEmpty(Decision.Reason));
        if (Decision == null) Decision = new AutomaticDecisionTransaction();
        if (legacy && PendingOptimization != null)
        {
            Decision = new AutomaticDecisionTransaction {
                State = AutomaticDecisionState.ConfirmingOptimization,
                Scope = PendingOptimization.Scope,
                Current = PendingOptimization.Current,
                Target = PendingOptimization.Target,
                BaselineResponse = PendingOptimization.BaselineResponse,
                TargetResponse = PendingOptimization.TargetResponse,
                StartedUtc = PendingOptimization.CreatedUtc,
                ExpiresUtc = PendingOptimization.CreatedUtc.Add(OpportunityOptimizationPolicy.PendingLifetime),
                Reason = "migrated pending optimization", Revision = 1
            };
        }
        else if (legacy && !String.IsNullOrEmpty(Target))
        {
            Decision = new AutomaticDecisionTransaction {
                State = AutomaticDecisionState.Observing, Scope = Scope,
                Current = current, Target = Target, Previous = Previous,
                BaselineResponse = PreviousResponse, StartedUtc = StartedUtc,
                Reason = "migrated switch observation", Revision = 1
            };
        }
        else if (legacy && HoldUntilUtc > now)
        {
            Decision = new AutomaticDecisionTransaction {
                State = AutomaticDecisionState.Cooldown, Scope = Scope,
                Current = current, StartedUtc = now, ExpiresUtc = HoldUntilUtc,
                Reason = "migrated optimization hold", Revision = 1
            };
        }
        else if (legacy)
        {
            Decision = new AutomaticDecisionTransaction {
                State = AutomaticDecisionState.Healthy, Scope = Scope,
                Current = current, StartedUtc = now,
                Reason = "initialized automatic decision state", Revision = 1
            };
        }

        if (Decision.State == AutomaticDecisionState.ConfirmingFailure ||
            Decision.State == AutomaticDecisionState.Recovering ||
            Decision.State == AutomaticDecisionState.SearchingOptimization ||
            Decision.State == AutomaticDecisionState.Switching)
        {
            Decision.State = AutomaticDecisionState.Degraded;
            Decision.Target = null;
            Decision.Previous = null;
            Decision.Reason = "interrupted automatic transaction normalized";
            Decision.Revision++;
        }
        if ((Decision.State == AutomaticDecisionState.Cooldown ||
             Decision.State == AutomaticDecisionState.Stabilization) &&
            Decision.ExpiresUtc != DateTime.MinValue && now >= Decision.ExpiresUtc)
        {
            Decision.State = AutomaticDecisionState.Healthy;
            Decision.ExpiresUtc = DateTime.MinValue;
            Decision.Reason = "automatic hold expired";
            Decision.Revision++;
        }
        if (String.IsNullOrEmpty(Decision.Current)) Decision.Current = current;
        return Decision;
    }

    public DecisionTransition Apply(AutomaticDecisionEvent value,
        AutomaticDecisionContext context)
    {
        DateTime now = context == null || context.NowUtc == DateTime.MinValue
            ? DateTime.UtcNow : context.NowUtc;
        bool needsInitialization = Decision == null || (Decision.Revision == 0 &&
            Decision.State == AutomaticDecisionState.Healthy &&
            Decision.StartedUtc == DateTime.MinValue && String.IsNullOrEmpty(Decision.Reason));
        if (needsInitialization)
            EnsureDecisionState(context == null ? null : context.Current, now);
        DecisionTransition transition = AutomaticDecisionStateMachine.Transition(Decision, value, context);
        if (transition.Accepted) Decision = transition.Transaction;
        return transition;
    }

    public SwitchBudgetDecision AutomaticSwitchBudget(DateTime now)
    {
        if (AutomaticSwitches == null) AutomaticSwitches = new List<AutomaticSwitchRecord>();
        return SwitchBudgetPolicy.CanSwitch(AutomaticSwitches, now);
    }

    public bool HasRecentAutomaticSwitch(DateTime now)
    {
        return (AutomaticSwitches ?? new List<AutomaticSwitchRecord>())
            .Any(x => x != null && x.Utc <= now && x.Utc > now.AddMinutes(-10));
    }

    public void RecordAutomaticSwitch(string from, string to, string reason, DateTime now)
    {
        AutomaticSwitches = SwitchBudgetPolicy.Record(AutomaticSwitches,
            new AutomaticSwitchRecord { Utc = now, From = from, To = to, Reason = reason });
    }
    public bool CanUserFeedbackRollback(string current, DateTime now)
    {
        return !String.IsNullOrWhiteSpace(Previous) && String.Equals(Target, current, StringComparison.Ordinal) &&
            StartedUtc != DateTime.MinValue && StartedUtc <= now && StartedUtc >= now.AddMinutes(-10);
    }
    public bool ShouldRecheckPreviousAfterBrowserResult(string current,
        BrowserVerificationOutcome outcome, bool messageSent, DateTime now)
    {
        return messageSent &&
            (outcome == BrowserVerificationOutcome.ConversationError ||
             outcome == BrowserVerificationOutcome.GenerationTimeout) &&
            CanUserFeedbackRollback(current, now);
    }
    public bool NeedsRollback(CandidateScanResult scan)
    {
        VerificationCount++;
        FailedChecks = !Passed(scan) && scan.Health != CandidateHealth.Unknown ? FailedChecks + 1 : 0;
        SlowerChecks = Passed(scan) && QualitySwitch && PreviousResponse > 0 &&
            QualityMeasurement.ResponseMilliseconds(scan, 5000) > PreviousResponse * 1.25 ? SlowerChecks + 1 : 0;
        return ((QualitySwitch || ProvisionalSwitch) && FailedChecks >= 1) || FailedChecks >= 2 || SlowerChecks >= 2;
    }
    public TimeSpan Interval(CandidateScanResult scan)
    {
        StableCycles = Passed(scan) ? Math.Min(5, StableCycles + 1) : 0;
        if (!String.IsNullOrEmpty(Target) || !Passed(scan)) return TimeSpan.FromSeconds(30);
        return TimeSpan.FromMinutes(1);
    }
}
