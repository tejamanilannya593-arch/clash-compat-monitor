# Opportunity Candidate Exploration Design

Date: 2026-09-21

## Goal

Improve scheduled performance optimization so it does not permanently favor only the nodes with the lowest Mihomo synthetic delay. Each optimization round should deliberately include recent proven performers and one under-tested node while preserving current safety limits and ranking every tested candidate by fresh service evidence.

This change applies only to performance optimization while the current node is usable. Hard-failure recovery and severe-degradation rescue continue to follow the current live Mihomo-delay order without exploration.

## Candidate composition

Introduce a pure `OpportunityCandidatePlanner` that receives:

- the current subscription-scope candidates;
- the current selected node;
- the complete live Mihomo-delay results for this round;
- the current scope's experience records;
- the current UTC time.

The planner returns at most eight distinct candidate names drawn from these source quotas:

- five nodes with the lowest valid live Mihomo delay;
- two recent high-quality nodes recommended by the existing experience policy;
- one under-tested exploration node.

The selected current node is never returned. A node may satisfy several categories but appears only once. When a category cannot supply enough distinct nodes, the remaining capacity is filled from the live-delay ranking. The planner never invents a node absent from the current candidate set.

## Exploration policy

The exploration candidate is selected deterministically from nodes not already selected by the live-delay or history categories. Ordering is:

1. nodes with no experience record;
2. lower experience sample count;
3. older last observation time;
4. ordinal node-name order as the stable final tie-breaker.

No history is interpreted as `Unknown`, not success or failure. Exploration only grants a candidate one fresh measurement opportunity.

If experience data is absent, corrupt, outside the active scope, or otherwise unusable, planning safely degrades toward the live-delay ranking.

## Scan order and bounds

The planner interleaves distinct source selections so the diversity candidates are evaluated before the existing early-stop rule can permanently exclude them:

1. live-delay candidate 1;
2. historical candidate 1;
3. exploration candidate 1;
4. live-delay candidate 2;
5. historical candidate 2;
6. remaining live-delay candidates.

Missing categories are skipped and their capacity is filled from the remaining live-delay order. The existing bounds remain unchanged:

- no more than eight candidates are service-probed;
- stop after three eligible candidates have been found;
- each quick service validation keeps the existing two-second limit;
- Mihomo delay is measured once for all alternative leaf nodes and is not repeated.

## Ranking and decision boundaries

Candidate origin influences only which nodes are tested and their test order. It never supplies compatibility evidence and never overrides fresh observations.

After quick scans, the existing ranking remains authoritative:

- fresh real-service response P75 is the primary ordering signal;
- live Mihomo delay is only a tie-breaker;
- the chosen target still receives the complete selected-service validation;
- material-improvement, confirmation, hysteresis, cooldown, switch-budget, observation, and rollback rules remain unchanged.

Manual rechecks do not gain a separate exploration transaction. Hard-failure and severe-degradation recovery continue using the existing pure live-delay order.

## Decision trace

The optimization trace records:

- counts selected from live-delay, historical, exploration, and fill sources;
- the safe fingerprint of each scanned candidate and its source category;
- whether duplicate removal or live-delay fill was required;
- the existing checked, eligible, ranking, threshold, and final-decision evidence.

Logs must not contain raw exit IP addresses or unredacted node names.

## Failure handling

The planner is side-effect free. It does not probe services, switch nodes, update experience, or persist state. Empty or incomplete input produces the best bounded live-delay plan available. A planning failure must not block the ordinary scheduled current-node health scan and must not alter failure-recovery behavior.

## Verification

Pure policy tests cover:

- standard `5 + 2 + 1` membership and interleaved ordering;
- overlap between live, historical, and exploration selections;
- deterministic under-tested ordering;
- missing history and missing exploration candidates;
- live-delay fill and the eight-candidate maximum;
- exclusion of the current node and nodes outside the current candidate set.

MonitorWorker orchestration tests cover:

- scheduled optimization actually probes historical and exploration candidates;
- three eligible candidates still stop further service probes;
- historical quality can change test inclusion but cannot defeat a better fresh service result;
- hard-failure and severe-degradation rescue retain strict live-delay order;
- trace output contains only safe candidate identities and source categories.

The release gate includes the complete build, C# test suite, both Clash JavaScript suites, release tests, `git diff --check`, installed automatic-cycle verification, single-process and startup-shortcut checks, and before/after hashes for Clash configuration and subscription files.

## Release

After implementation and verification, publish immutable version `0.7.0-preview.9`, replace the locally installed version, and copy the release folder, ZIP, and SHA-256 sidecar to the established D-drive product directory without overwriting previous releases.
