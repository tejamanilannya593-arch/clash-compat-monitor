# Automatic Decision State Machine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the selector one persisted automatic-decision state, separate hard failure from severe latency, and prevent churn with dual improvement gates, cooldown, hysteresis, and switch budgets.

**Architecture:** Add a pure state/policy module whose serializable transaction is stored by `ConnectionAssurance`. `MonitorWorker` classifies evidence, asks the state machine for directives, performs probes, and routes every automatic selector write through one authorization method; probe classes remain evidence-only.

**Tech Stack:** C#/.NET Framework 4, `JavaScriptSerializer`, repository console tests, Mihomo named-pipe client, PowerShell and Node.js regression tests.

---

### Task 1: Add pure decision evidence and improvement policies

**Files:**
- Create: `src/AutomaticDecisionStateMachine.cs`
- Modify: `tests/Tests.cs`

- [ ] **Step 1: Write failing classification and improvement tests**

Add `AutomaticDecisionPolicyBehavior()` to the main test sequence. Build representative `CandidateScanResult` values and assert:

```csharp
Equal(DecisionEvidenceClass.HardFailure,
    DecisionEvidencePolicy.Classify(regionBlocked, false),
    "region rejection is a hard failure");
Equal(DecisionEvidenceClass.HardFailure,
    DecisionEvidencePolicy.Classify(serviceFailed, false),
    "definite service rejection is a hard failure");
Equal(DecisionEvidenceClass.SevereDegradation,
    DecisionEvidencePolicy.Classify(stillSlowButPassed, true),
    "confirmed two-second latency is degradation not failure");
Equal(DecisionEvidenceClass.Unknown,
    DecisionEvidencePolicy.Classify(unknown, false),
    "missing evidence remains unknown");
Equal(DecisionEvidenceClass.Healthy,
    DecisionEvidencePolicy.Classify(healthy, false),
    "usable bounded evidence is healthy");

Equal(false, MaterialImprovementPolicy.Evaluate(200, 150, false).Accepted,
    "small absolute gain cannot switch despite relative gain");
Equal(true, MaterialImprovementPolicy.Evaluate(1200, 800, false).Accepted,
    "normal improvement requires twenty percent and two hundred milliseconds");
Equal(false, MaterialImprovementPolicy.Evaluate(1200, 850, true).Accepted,
    "recent switch raises relative requirement to thirty percent");
Equal(true, MaterialImprovementPolicy.Evaluate(1200, 800, true).Accepted,
    "recent switch accepts a thirty-percent material improvement");
```

- [ ] **Step 2: Run the build and verify RED**

Run `& .\build.ps1`.

Expected: compilation fails because the decision policy types do not exist.

- [ ] **Step 3: Implement the minimal policy types**

Create:

```csharp
public enum DecisionEvidenceClass
{
    Unknown, Healthy, NormalDegradation, SevereDegradation, HardFailure
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
```

`DecisionEvidencePolicy.Classify(scan, confirmedSevereLatency)` returns `HardFailure` only for definite failed evidence, returns `SevereDegradation` only when the triggering service still passed above 2000 ms, and preserves `Unknown`.

`MaterialImprovementPolicy.Evaluate(baseline, target, recentAutomaticSwitch)` uses 20 percent normally, 30 percent after a switch in ten minutes, and always requires at least 200 ms absolute improvement. Non-positive or non-finite inputs are rejected.

- [ ] **Step 4: Run the build and commit**

Run `& .\build.ps1`. Expected: all tests pass.

```powershell
git add src/AutomaticDecisionStateMachine.cs tests/Tests.cs
git commit -m "feat: classify automatic decision evidence"
```

### Task 2: Add the pure state machine and switch budget

**Files:**
- Modify: `src/AutomaticDecisionStateMachine.cs`
- Modify: `tests/Tests.cs`

- [ ] **Step 1: Write failing state-transition tests**

Add assertions covering these transitions:

```text
Healthy + normal degradation -> Degraded / none
Healthy + hard failure -> ConfirmingFailure / ConfirmCurrent
ConfirmingFailure + confirmed -> Recovering / SearchRecovery
SearchingOptimization + target prepared -> ConfirmingOptimization / Wait
ConfirmingOptimization + hard failure -> ConfirmingFailure / ConfirmCurrent
Switching + success -> Observing / Observe
Observing + complete -> Cooldown / none
Cooldown + expiry -> Healthy / none
any automatic transaction + manual node change -> Cooldown / cancel
budget exhausted -> Stabilization / none
Stabilization + hard failure -> ConfirmingFailure / ConfirmCurrent
```

Assert invalid or stale target events retain the current state and return a rejection reason instead of silently mutating transaction data.

- [ ] **Step 2: Write failing switch-budget tests**

Use fixed UTC timestamps and assert:

```csharp
Equal(true, SwitchBudgetPolicy.CanSwitch(historyWithOneInTenMinutes, now).Allowed,
    "second switch inside ten minutes remains allowed");
Equal(false, SwitchBudgetPolicy.CanSwitch(historyWithTwoInTenMinutes, now).Allowed,
    "third switch inside ten minutes enters stabilization");
Equal(false, SwitchBudgetPolicy.CanSwitch(historyWithFourInThirtyMinutes, now).Allowed,
    "fifth switch inside thirty minutes enters stabilization");
Equal(true, SwitchBudgetPolicy.CanSwitch(historyWhoseOldestSlotExpired, now).Allowed,
    "budget automatically releases when the rolling slot expires");
```

- [ ] **Step 3: Run the build and verify RED**

Run `& .\build.ps1`.

Expected: compilation fails because state, event, directive, transaction, and budget types do not exist.

- [ ] **Step 4: Implement serializable state and transition types**

Add:

```csharp
public enum AutomaticDecisionState
{
    Healthy, Degraded, EnvironmentBlocked, ConfirmingFailure, Recovering,
    SearchingOptimization, ConfirmingOptimization, Switching, Observing,
    Cooldown, OutageSuppressed, Stabilization
}

public enum AutomaticDecisionEvent
{
    HealthyEvidence, NormalDegradation, SevereDegradation, HardFailure,
    FailureRecovered, FailureConfirmed, RecoveryTargetReady, NoRecoveryTarget,
    OptimizationDue, OptimizationTargetPrepared, OptimizationRejected,
    SwitchAuthorized, SwitchSucceeded, SwitchFailed, ObservationComplete,
    RollbackRequired, CooldownExpired, OutageDetected, OutageExpired,
    BudgetExhausted, BudgetReleased, EnvironmentBlocked, EnvironmentRestored,
    ManualNodeChanged
}

public enum AutomaticDecisionDirective
{
    None, ConfirmCurrent, SearchRecovery, SearchOptimization,
    ConfirmOptimization, Switch, Observe, Rollback, Cancel
}
```

`AutomaticDecisionTransaction` stores state, scope, current/target/previous nodes, triggering service, evidence class, baseline/target response, start/expiry times, reason, and a monotonically increasing revision.

`AutomaticDecisionStateMachine.Transition(transaction, event, context)` returns a `DecisionTransition` containing a new transaction snapshot and directive. It must be a pure function and must prioritize hard failure and manual cancellation over optimization.

- [ ] **Step 5: Implement switch-budget policy**

Add serializable `AutomaticSwitchRecord` and `SwitchBudgetDecision`. `SwitchBudgetPolicy.CanSwitch` counts records newer than ten and thirty minutes, rejects when the next switch would exceed 2/10 or 4/30, and returns the earliest time at which both windows permit another switch. `Record` prunes entries older than thirty minutes and bounds retained records.

- [ ] **Step 6: Run the build and commit**

Run `& .\build.ps1`. Expected: all state and budget tests pass.

```powershell
git add src/AutomaticDecisionStateMachine.cs tests/Tests.cs
git commit -m "feat: add automatic decision state machine"
```

### Task 3: Persist state and migrate preview.6 assurance data

**Files:**
- Modify: `src/ConnectionAssurance.cs`
- Modify: `tests/Tests.cs`

- [ ] **Step 1: Write failing persistence and migration tests**

Round-trip an assurance object containing `ConfirmingOptimization`, an active target, an expiry, and two switch-budget records. Assert every field survives serialization.

Construct legacy assurance values and assert:

```text
PendingOptimization -> ConfirmingOptimization
Target -> Observing
future HoldUntilUtc -> Cooldown
no legacy transaction -> Healthy
interrupted SearchingOptimization after restart -> Degraded
expired Cooldown after restart -> Healthy
scope change -> Healthy with no target or previous rollback transaction
```

- [ ] **Step 2: Run the build and verify RED**

Run `& .\build.ps1`.

Expected: the new transaction and switch history are not persisted or migrated.

- [ ] **Step 3: Extend `ConnectionAssurance`**

Add:

```csharp
public AutomaticDecisionTransaction Decision { get; set; }
public List<AutomaticSwitchRecord> AutomaticSwitches { get; set; }
```

Initialize both in the constructor. Add `EnsureDecisionState(current, now)` to migrate legacy preview.6 fields once and normalize non-resumable or expired states. Update `SetScope`, `Begin`, and pending-state helpers so the transaction state is authoritative while legacy fields remain readable during migration.

Add helpers:

```csharp
public DecisionTransition Apply(AutomaticDecisionEvent value,
    AutomaticDecisionContext context)
public SwitchBudgetDecision AutomaticSwitchBudget(DateTime now)
public bool HasRecentAutomaticSwitch(DateTime now)
public void RecordAutomaticSwitch(string from, string to, string reason, DateTime now)
```

- [ ] **Step 4: Run the build and commit**

Run `& .\build.ps1`. Expected: all persistence, migration, and existing assurance tests pass.

```powershell
git add src/ConnectionAssurance.cs tests/Tests.cs
git commit -m "feat: persist automatic decision transactions"
```

### Task 4: Integrate hard-failure and severe-degradation recovery

**Files:**
- Modify: `src/StartupRecovery.cs`
- Modify: `src/MonitorWorker.cs`
- Modify: `tests/Tests.cs`

- [ ] **Step 1: Write failing recovery orchestration tests**

Extend the existing orchestrated worker tests to prove:

- a pending optimization is cancelled when the current scan becomes a hard failure;
- hard failure enters recovery even during cooldown or stabilization;
- repeated latency above 2000 ms is classified as `SevereDegradation`;
- severe degradation keeps the current node when the best fully validated target improves by less than 20 percent or less than 200 ms;
- severe degradation switches only when both thresholds pass;
- the state sequence is `ConfirmingFailure -> Recovering -> Switching -> Observing` for hard failure and `Degraded -> Recovering -> Switching -> Observing` for accepted severe degradation;
- no candidate scan or probe directly writes the shared selector.

- [ ] **Step 2: Run the build and verify RED**

Run `& .\build.ps1`.

Expected: severe latency still shares the unconditional failure switch rule and no explicit transaction sequence is available.

- [ ] **Step 3: Add one automatic switch boundary**

Add a private `ApplyAutomaticSwitch` method to `MonitorWorker`. It accepts an authorized `DecisionTransition`, validates `Directive == Switch` or `Rollback`, verifies the selector still matches the expected source, performs `SelectRecorded`, confirms Mihomo selected the target, records the automatic switch and budget, and transitions to `Observing` only after success.

Replace automatic calls in fast recovery, pending optimization confirmation, ordinary quality switching, and observation rollback with this method. User feedback operations first apply `ManualNodeChanged` or an explicit rollback transition and never reuse stale automatic state.

- [ ] **Step 4: Separate severe degradation from hard failure**

After a focused retry, classify the result with `DecisionEvidencePolicy`:

- hard failure starts or continues hard recovery without a performance gate;
- severe degradation may reuse bounded candidate discovery, but its fully validated target must pass `MaterialImprovementPolicy.Evaluate` using recent-switch hysteresis;
- no materially better target ends in `Degraded` and keeps the current node;
- recovered confirmation transitions to `Healthy` and merges the successful evidence.

Log the state transition, classification, baseline/target values, both improvement values, and rejection reason.

- [ ] **Step 5: Run the build and commit**

Run `& .\build.ps1`. Expected: all recovery orchestration tests and prior failover tests pass.

```powershell
git add src/StartupRecovery.cs src/MonitorWorker.cs tests/Tests.cs
git commit -m "refactor: route recovery through decision state"
```

### Task 5: Integrate optimization, observation, cooldown, and stabilization

**Files:**
- Modify: `src/OpportunityOptimization.cs`
- Modify: `src/ConnectionAssurance.cs`
- Modify: `src/MonitorWorker.cs`
- Modify: `tests/Tests.cs`

- [ ] **Step 1: Write failing optimization state tests**

Prove scheduled optimization follows:

```text
Healthy/Degraded -> SearchingOptimization
SearchingOptimization -> ConfirmingOptimization
ConfirmingOptimization -> Switching only after both improvement gates
Switching -> Observing
Observing -> Cooldown
Cooldown -> Healthy after expiry
```

Add cases for a 25 percent but 50 ms gain, recent-switch 20–29 percent gain, manual selector change during confirmation, two switches in ten minutes, and four switches in thirty minutes. Assert blocked switches enter `Stabilization`, while a hard failure can still recover.

- [ ] **Step 2: Run the build and verify RED**

Run `& .\build.ps1`.

Expected: optimization still relies on `PendingOptimization`, `observing`, and `HoldUntilUtc` gates rather than the state transaction and budget.

- [ ] **Step 3: Apply state directives to opportunity optimization**

Transition to `SearchingOptimization` before the all-node opportunity scan. A valid target transitions to `ConfirmingOptimization`; rejection returns to `Degraded` or `Healthy`. Confirmation uses `MaterialImprovementPolicy`, then checks `AutomaticSwitchBudget` before requesting `Switch`.

An external selector change applies `ManualNodeChanged`, clears pending target and rollback payload, and enters a ten-minute cooldown. A hard failure always cancels the optimization transaction before recovery.

- [ ] **Step 4: Apply cooldown and stabilization**

When observation completes successfully, transition to `Cooldown` with the existing thirty-minute hold. Derive ordinary optimization permission from the transaction state rather than independent booleans. If the switch budget rejects a performance switch, transition to `Stabilization` until `AllowedAtUtc`. Hard-failure recovery bypasses both performance blocks but remains recorded in the budget history.

- [ ] **Step 5: Run the build and commit**

Run `& .\build.ps1`. Expected: all optimization, observation, migration, and budget tests pass.

```powershell
git add src/OpportunityOptimization.cs src/ConnectionAssurance.cs src/MonitorWorker.cs tests/Tests.cs
git commit -m "feat: enforce cooldown and automatic switch budgets"
```

### Task 6: Complete diagnostics and regression verification

**Files:**
- Modify: `src/MonitorWorker.cs`
- Modify: `tests/Tests.cs`

- [ ] **Step 1: Add failing transition-log assertions**

Assert logs include safe values for `state_from`, `state_to`, `event`, `evidence`, `directive`, relative and absolute improvements, active thresholds, ten- and thirty-minute switch counts, and explicit keep/switch reasons. Assert raw node names and exit IPs are absent.

- [ ] **Step 2: Implement bounded transition logging**

Add one helper that logs a `DecisionTransition` and optional `MaterialImprovementDecision`. Reuse it for hard recovery, severe-degradation rejection, optimization preparation/confirmation, observation completion, cooldown, stabilization, and manual cancellation.

- [ ] **Step 3: Run complete verification**

Run sequentially:

```powershell
& .\build.ps1
node .\clash\enhancement.test.js
node .\clash\pure-clash.test.js
& .\tests\Release.Tests.ps1
git diff --check
```

Use Serena diagnostics on every changed C# file. Expected: all commands exit 0 and diagnostics contain no errors or warnings.

- [ ] **Step 4: Review acceptance criteria and commit**

Compare implementation and tests with every phase-one acceptance criterion in the design. Fix any gap before committing.

```powershell
git add src/AutomaticDecisionStateMachine.cs src/ConnectionAssurance.cs src/StartupRecovery.cs src/OpportunityOptimization.cs src/MonitorWorker.cs tests/Tests.cs
git commit -m "test: cover automatic decision state transitions"
```

Do not bump, package, install, or publish a new preview in this plan. Release work is a separate explicitly versioned task after live-safe review.
