# Stable Node Identity Design

## Goal

Bind long-lived node evidence to the underlying proxy connection rather than its display name. A subscription update that reuses a name for a different server must not inherit the previous server's health, region, quality, standby, rollback, or verification history.

This change is read-only with respect to Clash. It must not modify generated configuration, subscription files, or Mihomo runtime configuration.

## Current problem

`CandidateNode` currently contains only a display name and multiplier. Subscription continuity, `NodeExperience`, health records, region eligibility, standby records, and several automatic-decision records use that name as their persistent key. A provider can therefore replace the endpoint behind an unchanged name and accidentally inherit trusted history.

The display name must remain available for Mihomo selection and the user interface, but it is not a durable identity.

## Chosen approach

Read the active Clash metadata in bounded, read-only mode and build a canonical connection identity from:

- active subscription/source identifier when available;
- proxy protocol;
- server host and port;
- connection parameters that materially identify the endpoint, including authentication identifier, transport, TLS/SNI, cipher, and plugin/options when present.

The canonical value is never persisted or logged. It is immediately passed through a versioned HMAC using the existing installation-local identity key. Persistent state stores only a token such as `node-v1-<digest>`.

The node name, provider marketing text, multiplier, measured latency, actual exit country, and current exit IP are excluded from the canonical identity. Renaming the same connection therefore preserves history, while changing the connection parameters creates a new identity.

## Configuration reader

Introduce a focused read-only component that reads only the active profile metadata and the `proxies` section of the generated Clash configuration. The reader has explicit file-size, entry-count, scalar-length, and nesting limits. It extracts only identity fields and discards all raw mappings after identity creation.

The parser must support the block-style mappings and inline scalar/list forms emitted by Clash Verge Rev. YAML aliases, custom tags, malformed structures, duplicate unsafe keys, or values outside the limits cause that entry to be treated as unresolved rather than guessed.

No raw server, credential, UUID, password, SNI, subscription identifier, or complete canonical string may enter logs, exception messages, state files, status files, or decision traces.

## Candidate model

Extend each runtime candidate with:

- `Name`: live Mihomo display name used only for selection and presentation;
- `NodeId`: versioned HMAC identity when resolution succeeds;
- `IdentityStrength`: `Strong` or `SessionOnly`.

Multiple live aliases that resolve to the same connection may share one persistent identity. They remain separate operational choices for Mihomo, while long-lived evidence is coalesced under the physical identity. The current live alias is used when a decision must select that identity.

If metadata cannot be resolved, the candidate receives a session-only identifier. It can still be probed, ranked from current-cycle evidence, used for hard-failure recovery, and displayed normally. It cannot read, create, or authorize persistent node history, proactive recommendations, standbys, region-cache reuse, or delayed automatic transactions.

## State and decision integration

All durable per-node evidence moves from display name to `NodeId`:

- health and preferred-node records;
- experience, response windows, failure rate, and stability history;
- quality history and historical candidate recommendations;
- region eligibility records;
- standby records;
- switch observation, rollback origin, and pending optimization targets;
- account/browser verification records that are currently node-bound.

Records may retain a display-name snapshot solely for user-facing history. Comparisons, authorization, deduplication, and cache lookup must use `NodeId`.

At the beginning of every cycle, build a live `NodeId -> Name` map. A persisted transaction is valid only if its strong `NodeId` resolves to a current candidate. A missing or ambiguous target cancels that transaction safely; it must never fall back to matching by name.

Subscription and experience scope continuity use the sorted strong identities rather than sorted display names. Renaming nodes without changing their connections preserves the scope. Replacing a connection behind the same name produces a new node identity and starts that node's evidence from zero without unnecessarily discarding unaffected nodes.

## Migration

Existing preview.10 records have no trusted `NodeId`. They remain parseable so upgrades do not corrupt state, but they are classified as legacy name-bound evidence and cannot authorize a switch, rollback, standby, region-cache hit, or historical recommendation.

On the first successful identity-resolved cycle:

- live nodes begin new identity-bound histories;
- legacy entries may remain visible for diagnostics but are excluded from decisions;
- the next bounded state save may prune obsolete legacy entries;
- no old record is automatically attached to a new identity merely because the display name matches.

This intentionally sacrifices some accumulated history to prevent unsafe inheritance.

## Failure handling

- Missing or unreadable configuration: continue normal current-cycle monitoring with session-only identities.
- Malformed proxy entry: isolate that entry; do not disable other candidates.
- Identity-key failure: disable persistent identity use for the cycle and surface a bounded diagnostic without secrets.
- Metadata changes during a scan: finish the current probe data as transient evidence, then rebuild the candidate map next cycle; do not persist it under a mismatched identity.
- Duplicate display names with conflicting connection metadata: mark them unresolved and exclude them from persistent evidence.

Hard-failure recovery remains available whenever the live candidate can be selected and freshly verified. Identity resolution failure must not turn an outage into an unrecoverable state.

## Privacy and security boundaries

- Use keyed HMAC, not a plain SHA-256 hash of credentials or server names.
- Persist only versioned node IDs and existing bounded display snapshots.
- Never expose identity source material in logs, UI, crash text, or tests.
- Never write to Clash configuration or subscription paths.
- Do not add a third-party YAML dependency for this feature.

## Testing

Unit and orchestration coverage must prove:

- identical connection metadata produces the same identity;
- renaming a node preserves its identity;
- changing protocol, server, port, subscription source, or a material connection parameter produces a new identity;
- identical names with different connection metadata never share history;
- legacy name-only history cannot authorize a quality switch or rollback;
- unresolved identities remain eligible for fresh hard-failure recovery but not persistent recommendations;
- region cache, experience history, standbys, and pending decisions use identities rather than names;
- a persisted target whose identity is absent after restart is cancelled;
- generated state, status, and logs contain none of the raw identity inputs;
- all configuration and subscription hashes are unchanged after build, install, and runtime verification.

## Release and acceptance

Publish the feature as immutable version `0.7.0-preview.11`. Build and run the complete C# suite, Clash enhancement tests, pure-Clash tests, release checks, and CI/CodeQL. Package the release with a SHA-256 sidecar, replace the installed preview.10 instance, confirm one watchdog and the startup shortcut, observe at least two scheduled cycles, and copy the folder, ZIP, and checksum to the established D-drive product directory.

Acceptance requires that a same-name endpoint replacement starts with no trusted history, a pure rename preserves identity-bound history, unresolved metadata fails closed for persistent decisions, and the installed monitor continues automatic checks without modifying Clash files.
