# Automatic Decision State Machine Design

## Goal

Replace the monitor's overlapping automatic-decision flags with one explicit state machine, distinguish hard failures from severe latency degradation, and add material-improvement, hysteresis, cooldown, and switch-budget gates without weakening confirmed-failure recovery.

This is phase one of the broader selector redesign. Exit-independent incident evidence, raw probe evidence separation, exploration, richer statistics, stable node identity, and full decision tracing remain later phases.

## Current Problem

Automatic behavior is currently inferred from several independent values:

- local booleans such as `observing`, `fastSwitched`, and `currentFailureConfirmed`;
- `ConnectionAssurance.Target` for post-switch observation;
- `PendingOptimization` for delayed optimization confirmation;
- `HoldUntilUtc` for several unrelated holds;
- `FailoverController`'s private failure and switch timestamps;
- service-incident state stored separately in experience data.

These values can describe overlapping transactions. In addition, repeated service latency above 2000 ms currently enters the same candidate-switch path as a definite region or service failure. That path may select any eligible replacement even when it is not materially better.

## Scope

Phase one implements:

1. one explicit automatic-decision state and transaction;
2. `HardFailure`, `SevereDegradation`, `NormalDegradation`, `Healthy`, and `Unknown` classification;
3. relative and absolute improvement requirements;
4. stronger hysteresis after a recent automatic switch;
5. automatic-switch budgets and stabilization;
6. explicit cooldown after an automatic switch;
7. cancellation when the user changes the selected node;
8. a single decision-authorized automatic switch entry point.

It does not yet change the current three-sample history requirement, incident exit diversity, raw evidence schema, candidate exploration mix, node identity, region-cache schema, schedule jitter, or probe levels.

## State Model

`AutomaticDecisionState` contains the only primary automatic state:

```text
Healthy
Degraded
EnvironmentBlocked
ConfirmingFailure
Recovering
SearchingOptimization
ConfirmingOptimization
Switching
Observing
Cooldown
OutageSuppressed
Stabilization
```

Only one state is active at a time. A persisted `AutomaticDecisionTransaction` carries state payload instead of creating parallel state flags:

- scope;
- current node;
- target node;
- previous node;
- triggering service;
- degradation class;
- baseline and target response;
- start and expiry timestamps;
- observation counters and rollback data;
- transition reason.

Short-lived execution states (`ConfirmingFailure`, `Recovering`, `SearchingOptimization`, and `Switching`) are not resumed after process restart. Persisted recovery normalizes them to `Degraded` or `Healthy`. Multi-cycle states (`ConfirmingOptimization`, `Observing`, `Cooldown`, `OutageSuppressed`, and `Stabilization`) are restored when their scope, selected node, and expiry remain valid.

## Events and Priority

The pure state machine accepts events and returns a transition plus a directive. It does not call Mihomo, execute probes, write files, or switch nodes.

Important events are:

- environment blocked or restored;
- current scan classified;
- focused failure confirmation recovered or confirmed;
- recovery search started, target accepted, or no target found;
- optimization search started, target prepared, confirmed, or rejected;
- switch started, succeeded, or failed;
- observation passed, completed, or requires rollback;
- service outage consensus opened or expired;
- cooldown expired;
- switch budget exhausted or released;
- selected node changed externally.

Priority is fixed:

```text
manual node change
> environment safety block
> hard-failure recovery
> service-outage suppression
> observation/rollback
> severe degradation
> ordinary optimization
```

A `HardFailure` event cancels a pending or active optimization transaction before entering `ConfirmingFailure` or `Recovering`. An external selector change cancels automatic confirmation, switching, observation, and rollback state and enters `Cooldown` for ten minutes.

## Degradation Classification

`DecisionEvidencePolicy.Classify` maps a completed current-node scan to:

- `HardFailure`: `RegionBlocked`, definite `ServiceFailed`, confirmed proxy/service connection failure, or a focused retry that still cannot establish the service connection;
- `SevereDegradation`: the same service passes but remains above 2000 ms after one focused retry;
- `NormalDegradation`: usable evidence whose sustained performance is above the optimization target but is not severe;
- `Healthy`: usable evidence within normal bounds;
- `Unknown`: missing, malformed, interrupted, or otherwise inconclusive evidence.

`Unknown` never becomes failure merely because it is not successful.

Hard failure may use the existing fastest safe recovery flow. Severe degradation may scan candidates, but it may switch only when the target passes complete validation and satisfies the material-improvement policy.

## Material Improvement and Hysteresis

Ordinary optimization and severe-degradation switching require both:

```text
relative improvement >= 20%
absolute improvement >= 200 ms
```

When any automatic switch occurred in the previous ten minutes, the relative requirement becomes 30 percent. The 200 ms absolute requirement remains.

The existing 800 ms value remains an experience target and scan trigger, not the sole switching rule. A target above 800 ms may rescue a severely degraded node only when it is materially better and passes all required-service and region gates; it is never labelled a preferred node.

Hard-failure recovery does not require a performance improvement. It still requires a region-safe candidate that resolves the failed service and passes the existing complete service validation.

## Cooldown and Switch Budget

Every successful automatic switch is recorded with timestamp and cause. The bounded history retains the last 30 minutes.

Budgets are:

- at most two automatic switches in any ten-minute window;
- at most four automatic switches in any thirty-minute window.

When another switch would exceed either budget, the machine enters `Stabilization`. Stabilization lasts until the earliest recorded switch ages out enough for both budgets to permit another switch. During stabilization:

- performance optimization and severe-degradation switching are disabled;
- hard-failure recovery remains allowed;
- a hard-failure switch is still recorded and may extend stabilization.

After a successful switch and completed observation, the machine enters `Cooldown`. The existing thirty-minute optimization hold is preserved. Cooldown blocks ordinary optimization; hard failure remains allowed. Severe degradation may be considered only if the switch budget permits and the stronger recent-switch threshold is met.

## Orchestration Boundary

`MonitorWorker` remains responsible for executing I/O requested by directives:

- run the current-node probe;
- run a focused confirmation;
- measure candidate delays;
- run quick and complete candidate probes;
- apply an authorized selection;
- run observation and rollback checks.

Probe code returns evidence only. It cannot call the shared selector. All automatic selector writes pass through one `ApplyAutomaticSwitch` method that requires a state-machine switch directive and records the switch budget entry only after Mihomo confirms the selected node.

User-requested rollback and explicit user selector changes remain distinguishable from automatic switches. An externally selected node cancels the current automatic transaction before subsequent decisions.

## Persistence and Migration

The transaction and switch-budget timestamps are persisted inside `ConnectionAssurance`, which is already saved with experience state.

On loading legacy preview.6 state:

- a valid `PendingOptimization` becomes `ConfirmingOptimization`;
- a valid `Target` becomes `Observing`;
- a future `HoldUntilUtc` becomes `Cooldown`;
- otherwise the machine starts from `Healthy` and reclassifies on the next current scan.

Legacy fields remain readable for one migration version but are no longer authoritative after migration. Saving writes the new state model and clears obsolete parallel transaction fields where safe.

## Diagnostics

Every transition records:

- previous and next state;
- event;
- degradation class;
- safe node hashes;
- baseline, target, relative improvement, and absolute improvement when relevant;
- active relative and absolute thresholds;
- switch counts for ten- and thirty-minute windows;
- directive and rejection reason.

Phase one uses the existing bounded logger. A structured persistent decision-trace store is deferred to the later Decision Trace phase.

## Testing

Pure state-machine tests cover every allowed priority transition, invalid transition rejection, optimization cancellation by hard failure, manual-change cancellation, restart normalization, cooldown expiry, and stabilization release.

Policy tests cover:

- hard failure versus severe degradation;
- `Unknown` remaining inconclusive;
- 20 percent plus 200 ms normal threshold;
- 30 percent recent-switch threshold;
- 800 ms as a target rather than a universal switch requirement;
- two-in-ten-minute and four-in-thirty-minute budgets;
- hard failure bypassing cooldown and stabilization performance blocks.

Worker orchestration tests prove:

- probes never select the shared group;
- severe degradation keeps the current node when no materially better target exists;
- severe degradation switches only after complete target validation and both improvement gates;
- a hard failure cancels pending optimization and immediately enters recovery;
- an external selector change cancels confirmation and rollback state;
- all automatic selector writes pass through the decision-authorized switch path;
- successful switches enter observation, then cooldown;
- budget exhaustion enters stabilization without blocking hard-failure recovery.

Full build, Clash enhancement tests, pure-Clash tests, release tests, Serena diagnostics, and `git diff --check` are required before release work.

## Acceptance Criteria

- Exactly one primary automatic decision state exists at any time.
- No probe method can directly switch the shared selector.
- Hard failure always outranks performance optimization.
- Repeated latency above 2000 ms is not labelled as hard failure.
- Severe degradation never switches to a candidate that is not materially better.
- Ordinary and severe performance switches satisfy both relative and absolute thresholds.
- Recent switching increases hysteresis and switch budgets prevent loops.
- Stabilization blocks performance churn while retaining hard-failure recovery.
- Manual node changes cancel stale automatic transactions.
- Existing official-region, complete-service verification, observation, and safe-rollback protections remain intact.
