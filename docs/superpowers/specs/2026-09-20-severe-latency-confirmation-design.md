# Severe-Latency Confirmation Design

## Goal

Prevent a single slow service measurement from causing an unnecessary node switch, while preserving immediate recovery from definite region blocks and service failures.

## Current Problem

`QualityPolicy.SeverelySlowService` treats any passed service response above 2000 ms as severe latency. `StartupRecovery.FastFailoverService` then routes that measurement into emergency selection, but `RequiresRepeatConfirmation` currently asks for a retry only when the whole scan is `Transient`.

As a result, a `BasicCompatible` or `Compatible` current node with one successful response above 2000 ms is considered confirmed immediately. Live preview.6 logs show this path repeatedly switching among otherwise usable nodes after isolated ChatGPT, Gemini, GitHub, or Steam API latency spikes.

## Selected Behavior

- Definite `RegionBlocked` and `ServiceFailed` results retain the existing immediate failover behavior.
- Existing `Transient` failures retain their current fast confirmation.
- A passed service response above 2000 ms receives one focused retry on the current node before candidate discovery.
- If the retry passes at or below 2000 ms, merge that successful result into the current scan and keep the current node.
- If the retry still exceeds 2000 ms, fails with usable evidence, or remains transient, continue through the existing bounded emergency candidate scan.
- An unknown retry must not manufacture failure evidence; it keeps the current node and allows the next scheduled cycle to try again.
- Candidate ordering, official-region filtering, selected-service full validation, post-switch observation, and safe rollback remain unchanged.

## Design

The policy layer will distinguish why confirmation is required rather than inferring it only from `CandidateHealth`:

- definite failure: no extra current-node retry;
- transient failure: focused retry, using the existing success-merge behavior;
- severe latency: focused retry, with recovery defined as a passed result at or below 2000 ms.

`MonitorWorker` will use the focused retry result to make the final confirmation decision. Severe latency will no longer set `currentFailureConfirmed` to `true` unconditionally. The retry will probe only the triggering service, so the added cost is bounded and does not repeat the all-node Mihomo delay round.

The diagnostic log will continue to identify `severe-latency`, but its action will be `confirm`. It will record whether the retry confirmed the degradation or recovered.

## Safety and Timing

This adds one service probe before emergency candidate discovery only for successful responses above 2000 ms. Definite failures remain on the fastest path. The monitor's 30/60-second scheduler, five-minute proactive opportunity optimization, service-incident handling, and user-requested checks are not changed.

If the current node changes externally during confirmation, the existing selection guards prevent a stale switch.

## Testing

Pure policy tests will prove:

- `BasicCompatible` and `Compatible` severe latency require confirmation;
- definite region and service failures do not gain an extra retry;
- a recovered severe-latency result is merged and no longer qualifies for failover;
- a repeated response above 2000 ms remains confirmed;
- an unknown retry does not become confirmed failure.

Worker orchestration tests will prove:

- one severe-latency sample performs a focused current-node retry before any all-node delay scan;
- a recovered retry keeps the current node and performs no candidate scan or switch;
- repeated severe latency performs exactly one all-node delay round and may switch after full target validation;
- definite region blocking still enters candidate discovery immediately;
- automatic and requested cycles use the same confirmation rule.

The full build, enhancement tests, pure-Clash tests, release tests, and `git diff --check` remain required before packaging or installation.

## Acceptance Criteria

- A single passed response above 2000 ms cannot switch nodes by itself.
- Two consecutive severe measurements of the triggering service can still initiate fast failover.
- Definite failures retain current recovery speed.
- No duplicate all-node delay scan is introduced.
- No Clash configuration or subscription file is modified.
