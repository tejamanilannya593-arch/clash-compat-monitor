# Five-Minute Automatic Optimization Design

## Goal

When automatic optimization is enabled and the current node is persistently slow, the monitor should discover, confirm, and switch to a materially better node within five minutes under normal network conditions, without requiring the user to click **立即复检**.

This change must preserve the existing official-region gate, full selected-service validation, conservative behavior for already-good nodes, post-switch observation, and safe rollback.

## Current Problem

The scheduler already checks the current node roughly every 60 seconds, and **立即复检** invokes the same `MonitorCoordinator.RequestCheck` path. The perceived difference comes from candidate discovery:

- healthy maintenance refreshes only three rotating candidates every five minutes;
- a 65-node subscription therefore needs roughly 108 minutes for one full rotation;
- proactive quality comparison accepts only `CandidateHealth.Compatible`;
- real ChatGPT and Gemini entrance checks commonly produce `BasicCompatible`, so maintenance logs repeatedly report `compatible=0`;
- a manual check appears effective only when it happens to observe a service above 2000 ms, which enters the separate fast-failover path that scans all nodes and permits a carefully bounded basic-compatible emergency target.

The fix is therefore a new proactive opportunity-scan path, not a shorter scheduler interval and not a special privilege for the manual button.

## User-Visible Behavior

- Automatic optimization remains opt-in through the existing preference.
- A current node whose rolling response is at or below 800 ms is retained.
- When the current node is persistently above 800 ms, the monitor starts an opportunity scan without user action.
- Opportunity scans are scheduled at least once every three minutes while the current node remains persistently slow.
- The monitor performs one concurrent Mihomo delay round across all eligible leaf nodes, then checks candidates in current live-delay order.
- At most eight candidates receive a quick service scan, and scanning stops after three performance-comparable candidates are found.
- The best candidate receives a complete scan of every selected service.
- The winner is stored as a pending optimization target rather than switched immediately.
- Thirty seconds later, the monitor rechecks the current node and the pending target. It switches only if the target still passes and remains materially better.
- The existing 30-second post-switch observation and safe rollback behavior remain unchanged.
- **立即复检** continues to request an ordinary cycle. It does not bypass evidence, hysteresis, or safety gates.

The five-minute objective applies when Mihomo is responsive, the monitor is not paused, no conflicting proxy/VPN condition exists, and foreground traffic protection is not actively postponing optimization.

## Evidence Tiers

`Compatible` keeps its current meaning and remains the strongest evidence tier.

A new performance-comparable predicate admits `BasicCompatible` only for proactive ranking when all of the following are true:

- the actual exit country is known and belongs to the dated ChatGPT and Gemini official supported-region intersection;
- every selected service was included in the complete scan;
- no selected service has a definite region, service, or transient failure;
- no selected service exceeds the existing per-service optimization ceiling;
- AI evidence is at least entrance/login-chain reachability rather than unknown evidence.

This predicate does not relabel the node as fully compatible, does not create account-proof claims, and does not weaken emergency failure classification.

## Trigger and Timing

The current node is persistently slow when the existing recent-response rule says optimization should be evaluated: three recent current-node samples exist and their median is above 800 ms.

While that condition holds and automatic optimization is enabled:

1. Run an opportunity scan when no such scan has completed in the previous three minutes.
2. Complete all-node delay prefiltering and bounded quick comparison in the current cycle.
3. Fully validate the best target and persist a pending target with its response, current-node baseline, and creation time.
4. Schedule the next cycle for 30 seconds later.
5. Recheck only the current node and pending target.
6. Switch when the target is still at or below 800 ms and improves the current rolling response by at least 20 percent.

Pending targets expire after two minutes, disappear when requirements/subscription scope changes, and are cancelled by an external manual node change, a definite target failure, active service-incident consensus, or traffic/path safety blocks.

## Components

### Opportunity policy

A small pure policy component decides:

- whether the current evidence is persistently slow;
- whether an opportunity scan is due;
- whether a scan is performance-comparable;
- whether a pending target still qualifies after confirmation;
- when a pending target expires or must be cancelled.

The policy receives data and returns decisions; it does not call Mihomo, perform HTTP probes, or write state.

### Pending optimization state

`ConnectionAssurance` stores the pending target separately from an active post-switch transaction. The state contains the subscription/service scope, current node, target node, baseline response, target response, creation time, and confirmation count.

Separating pending optimization from post-switch observation prevents a not-yet-selected target from being mistaken for a rollback transaction.

### Monitor worker orchestration

`MonitorWorker` reuses the existing fast-selection mechanics:

- concurrent 2500 ms Mihomo delays for all leaf candidates;
- current live-delay ordering;
- cached actual-exit eligibility rejection;
- at most eight quick scans with a two-second per-candidate service budget;
- stop after three qualifying candidates;
- real-service P75 ranking;
- complete selected-service validation of the winner.

The opportunity path does not reuse the emergency switch decision itself. It creates pending state and returns a 30-second next-check time. On the confirmation cycle it probes only the current and target nodes, applies the material-improvement rule, and then uses the existing recorded selection, observation, and rollback operations.

### Coordinator and diagnostics

The coordinator continues to use one scheduling path for automatic and requested cycles. A trigger label (`startup`, `scheduled`, or `requested`) is recorded for diagnostics only and must not alter selection policy.

Logs record safe node hashes and these bounded fields:

- cycle trigger;
- opportunity scan elapsed time;
- checked and eligible counts;
- pending-target creation, confirmation, cancellation, or expiry reason;
- baseline and target P75 values;
- final switch or hold reason.

No raw exit IP or subscription content is logged.

## Traffic, Incident, and Failure Handling

- Foreground traffic protection may postpone proactive optimization, but never confirmed-failure failover.
- Active service-incident consensus cancels pending proactive optimization so a public outage cannot cause churn.
- Unknown actual exit, unknown required-service evidence, or a definite service failure rejects a proactive target.
- Mihomo delay or HTTP probe failures leave the current node unchanged and schedule a normal retry.
- An external user node selection cancels the pending target.
- A failed 30-second target confirmation clears pending state without starting another full-node delay round in the same cycle.
- Confirmed current-node failure always takes precedence and continues through the existing fast-failover path.

## Switching and Churn Protection

A proactive switch requires all of the following:

- automatic optimization is enabled;
- current-node three-sample median is above 800 ms;
- target complete validation is performance-comparable;
- target confirmation remains at or below 800 ms;
- target improves the current rolling response by at least 20 percent;
- the selector is still on the same current node;
- path, incident, traffic, cooldown, and hold checks allow a quality switch.

Only one pending target may exist. After switching, existing observation and rollback rules apply, followed by the existing optimization hold period. A target that fails confirmation cannot immediately become pending again until the next three-minute opportunity window.

## Testing

Pure policy tests cover the 800 ms boundary, three-minute due interval, 20 percent improvement, expiry, scope changes, manual node changes, incident cancellation, and unknown/definite-failure rejection.

Worker orchestration tests must prove:

- a scheduled cycle—not a requested cycle—starts opportunity discovery;
- a 65-node pool receives one concurrent all-node delay round;
- no more than eight quick candidates are checked and scanning stops at three qualifying candidates;
- a `BasicCompatible` candidate with known supported exit and no definite failures can become pending without being relabeled `Compatible`;
- all selected services participate in complete validation;
- the first scan does not switch;
- the 30-second confirmation switches a still-superior target;
- a recovered current node, slower target, external selection, incident, or foreground traffic cancels or postpones safely;
- automatic and manual cycles use identical decision gates;
- the five-minute acceptance scenario completes without `RequestCheck`.

The full build, enhancement tests, pure-Clash tests, release tests, and `git diff --check` remain required before packaging or installation.

## Acceptance Criteria

With automatic optimization enabled, no foreground traffic block, and a persistently slow current node:

- automatic discovery begins within three minutes;
- confirmation is scheduled 30 seconds after a target is prepared;
- a materially better, region-supported, fully checked target is selected within five minutes;
- no click on **立即复检** is required;
- a good current node at or below 800 ms is not switched;
- a single favorable candidate measurement cannot cause a proactive switch;
- existing confirmed-failure failover remains faster and unchanged;
- Clash configuration and subscription files are never modified.
