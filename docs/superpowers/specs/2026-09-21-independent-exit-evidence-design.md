# Independent Exit Incident Evidence Design

## Goal

Prevent false public-service-incident suppression when several node names share one real egress, while preserving every raw probe fact separately from whether it affects node-health history.

## Scope

This phase implements two connected capabilities:

1. incident consensus based on independent exit-network evidence rather than node names;
2. explicit three-state service observations whose raw outcome is never rewritten by suppression.

Candidate exploration, five-sample quality statistics, stable subscription-derived node identity, generalized scheduling jitter, and further UI redesign remain separate phases.

## Exit network evidence

`ExitIdentity` is extended with an optional ASN and observation time. The existing Cloudflare trace request remains authoritative for the current exit IP and country. The raw IP is used only in memory to create the existing keyed SHA-256 fingerprint and to compare two providers; it is never logged, serialized, or displayed.

ASN enrichment uses `https://ipwho.is/` through the same isolated candidate proxy. The request does not put an IP address in the URL. Its returned IP is fingerprinted with the same local key and must match the Cloudflare fingerprint before its ASN is accepted. A country mismatch also rejects the enrichment result.

ASN enrichment is lazy and cached by exit fingerprint for 60 minutes. A known, unexpired ASN avoids another enrichment request. Cache entries contain only:

```text
ExitFingerprint
CountryCode
Asn
ObservedUtc
ExpiresUtc
```

An ASN timeout, malformed response, rate limit, provider failure, fingerprint mismatch, or country mismatch produces unknown ASN evidence. It does not change region eligibility, service health, or the raw Cloudflare exit identity.

## Independent incident consensus

The current node and at least two alternatives must show the same definite failure kind for the same service. Consensus additionally requires three distinct, nonempty exit fingerprints.

Diversity is accepted using the following deterministic rule:

1. if all three observations have known ASN values, at least two distinct ASNs are required;
2. otherwise, at least two distinct known countries are required;
3. if neither condition is met, incident consensus is rejected and no service circuit opens.

Duplicate node names are still deduplicated, but distinct names never substitute for distinct exit fingerprints. More than three observations may participate; every considered definite failure must match the current failure kind, and the distinct-exit/diversity rules apply to the resulting evidence set.

The incident decision trace records only safe values: number of distinct fingerprints, countries, ASNs, whether diversity passed, and the rejection reason. It never records raw IPs or full node names.

## Raw service observation model

Add an explicit outcome enum:

```text
Success
Failure
Unknown
```

Add a serializable `ServiceObservation` for each selected service:

```text
Service
Outcome
FailureKind
Detail
ElapsedMilliseconds
ObservedUtc
ExitFingerprint
ExitCountryCode
ExitAsn
CountedForNodeHealth
```

`ProbeResult` remains the low-level probe return type. `CompatibilityScanner` converts every probe result into a `ServiceObservation` after exit evidence is known. Mapping is strict:

- a passed, non-partial result is `Success`;
- a definite region, service, or transport failure is `Failure`;
- unverified, parsing, insufficient-evidence, environment-interference, and partial reachability results are `Unknown` unless an existing explicit failure kind makes them definite.

`CandidateScanResult.ServiceResults` remains available as a compatibility view during this phase. New decision and history code prefers `ServiceObservations`.

## Suppression and history separation

Service-incident suppression never changes a probed observation's raw outcome, failure kind, latency, detail, or exit evidence. It returns a scan view where the affected observation has `CountedForNodeHealth = false`.

If a service was intentionally not probed, the scan contains an `Unknown` observation with `CountedForNodeHealth = false` and an explicit not-probed reason. It is not represented as success or failure.

Node health, quality samples, success rate, standby memory, and stability history consume only observations whose `CountedForNodeHealth` is true. Presentation and decision traces consume raw observations regardless of that flag. A shared service incident therefore remains visible without reducing or improving any node's history.

The current-node automatic cycle continues probing every user-selected service even while a circuit is active. Candidate scans may omit a suppressed service only where the existing policy deliberately avoids that endpoint; full recovery and rollback validation still probe all required services.

## Compatibility and persistence

Existing serialized state lacking ASN or service observations loads safely with empty ASN and an empty observation map. Existing `ServiceResults` are converted to observations at scan creation boundaries, not by speculative migration of old historical records.

ASN cache loading rejects blank or malformed fingerprints, invalid country codes, nonpositive ASN values, future observations beyond clock tolerance, and expired entries. The existing bounded-cache and atomic-write conventions are retained.

## Error handling

- ASN provider errors are evidence absence, not node failure.
- A raw probe exception already translated to transient/unknown evidence remains represented as such.
- Unknown evidence cannot participate in public-incident consensus.
- An incident circuit cannot open without three definite, same-kind failures and independent-exit diversity.
- A real failure that becomes incident-suppressed remains eligible for current-cycle UI and decision diagnostics but is excluded from node-health history.

## Testing

Pure tests cover three-state mapping, observation serialization, suppression flags, distinct-fingerprint requirements, ASN diversity, country fallback, unknown ASN rejection, and mismatch handling.

Scanner tests prove every selected service produces one raw observation and that raw IP never reaches serialized scan evidence. Cache tests cover 60-minute expiry, fingerprint changes, malformed data, bounded persistence, and provider failure.

Worker orchestration tests prove:

- three node names sharing one fingerprint cannot open a circuit;
- three fingerprints from one known ASN cannot open a circuit;
- matching failures across at least two ASNs open a circuit;
- country diversity is used only when ASN evidence is incomplete;
- displayed failures remain failures while `CountedForNodeHealth` is false;
- suppressed failures do not alter health, quality, or experience history;
- full recovery and rollback checks still include all required services.

## Release acceptance

All C# tests, Clash script tests, release tests, `git diff --check`, and Serena diagnostics must pass. The phase is released as a new non-overwriting preview version, packaged with SHA-256, installed locally as exactly one process, verified through at least two scheduled automatic cycles, and copied to the D-drive product directory. Protected Clash configuration and subscription files must remain unchanged.
