# Stable Node Identity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace display-name-based persistent trust with a keyed identity derived from the active proxy connection while retaining display names for Mihomo operations and UI.

**Architecture:** A bounded read-only configuration source extracts identity fields and immediately HMACs a canonical representation with the existing installation key. `CandidateNode` carries both live name and identity strength; persistent evidence uses a strong `NodeId`, while unresolved candidates remain available only for freshly verified hard-failure recovery. Each cycle rebuilds an identity-to-live-name map and cancels stale transactions rather than falling back to names.

**Tech Stack:** C#/.NET Framework 4.x, HMAC-SHA256, existing `tests/Tests.cs` harness, PowerShell release scripts, Node.js Clash tests.

---

## File structure

- Create `src/NodeIdentity.cs`: canonical material, HMAC identity, bounded read-only YAML extraction.
- Modify `src/Models.cs` and `src/CandidateCatalog.cs`: attach identity without changing live selector naming.
- Modify `src/Program.cs` and `src/MonitorWorker.cs`: configure, resolve, and map identities once per cycle.
- Modify `src/ExperienceMemory.cs`, `src/QualityScoring.cs`, `src/RegionEligibilityCache.cs`, `src/StateStore.cs`, `src/ConnectionAssurance.cs`, and node-bound verification/decision records: compare persistent evidence by identity.
- Modify `tests/Tests.cs`: identity, parser, migration, privacy, and orchestration coverage.
- Modify release files and create `docs/release-notes/v0.7.0-preview.11.md`.

### Task 1: Keyed identity primitive

**Files:**
- Create: `src/NodeIdentity.cs`
- Modify: `src/ExitIdentity.cs`
- Test: `tests/Tests.cs`

- [ ] **Step 1: Write failing identity tests**

Add and invoke `NodeIdentityBehavior()`:

```csharp
byte[] key = Enumerable.Range(1, 32).Select(x => (byte)x).ToArray();
var parameters = new Dictionary<string, string> {
    { "uuid", "secret-uuid" }, { "network", "ws" }, { "tls", "true" }
};
var material = new NodeIdentityMaterial("provider-a", "vmess", "edge.example", 443, parameters);
string id = NodeIdentity.Create(material, key);
Equal(true, id.StartsWith("node-v1-", StringComparison.Ordinal), "node identity is versioned");
Equal(id, NodeIdentity.Create(new NodeIdentityMaterial("provider-a", "vmess", "edge.example", 443,
    parameters.Reverse().ToDictionary(x => x.Key, x => x.Value)), key), "parameter order is irrelevant");
Equal(false, id.Contains("edge.example") || id.Contains("secret-uuid"), "identity reveals no source material");
Equal(false, id == NodeIdentity.Create(new NodeIdentityMaterial("provider-a", "vmess", "other.example", 443, parameters), key), "server change creates new identity");
Equal(false, id == NodeIdentity.Create(new NodeIdentityMaterial("provider-a", "vmess", "edge.example", 8443, parameters), key), "port change creates new identity");
Equal(false, id == NodeIdentity.Create(new NodeIdentityMaterial("provider-b", "vmess", "edge.example", 443, parameters), key), "source change creates new identity");
```

- [ ] **Step 2: Run `build.ps1` and verify compile failure**

Expected: undefined `NodeIdentityMaterial` and `NodeIdentity`.

- [ ] **Step 3: Implement the primitive**

```csharp
public enum NodeIdentityStrength { SessionOnly, Strong }

public sealed class NodeIdentityMaterial
{
    public NodeIdentityMaterial(string source, string protocol, string server, int port,
        IDictionary<string, string> parameters)
    {
        Source = (source ?? "").Trim();
        Protocol = (protocol ?? "").Trim().ToLowerInvariant();
        Server = (server ?? "").Trim().ToLowerInvariant();
        Port = port;
        Parameters = new SortedDictionary<string, string>(
            (parameters ?? new Dictionary<string, string>()).ToDictionary(
                x => (x.Key ?? "").Trim().ToLowerInvariant(), x => (x.Value ?? "").Trim()),
            StringComparer.Ordinal);
    }
    public string Source { get; private set; }
    public string Protocol { get; private set; }
    public string Server { get; private set; }
    public int Port { get; private set; }
    public IDictionary<string, string> Parameters { get; private set; }
}

public static class NodeIdentity
{
    public static string Create(NodeIdentityMaterial material, byte[] key)
    {
        if (material == null || key == null || key.Length < 16) return "";
        string canonical = Canonicalize(material);
        using (var hmac = new HMACSHA256(key))
            return "node-v1-" + Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
```

Canonicalization lowercases protocol/host, preserves case-sensitive credentials, sorts keys ordinally, length-prefixes every field, and rejects empty source/protocol/server, invalid ports, oversized scalars, and duplicate normalized keys.

- [ ] **Step 4: Run full build and commit**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
git add src\NodeIdentity.cs src\ExitIdentity.cs tests\Tests.cs
git commit -m "feat: add keyed stable node identity"
```

Expected: all tests pass.

### Task 2: Bounded read-only metadata resolver

**Files:**
- Modify: `src/NodeIdentity.cs`
- Modify: `src/Program.cs`
- Test: `tests/Tests.cs`

- [ ] **Step 1: Add failing resolver/privacy tests**

Build temporary `profiles.yaml` and `clash-verge.yaml` fixtures containing block proxy mappings, quoted names, reordered keys, inline options, a duplicate-name conflict, and malformed/oversized entries:

```csharp
var source = new ClashNodeIdentitySource(configPath, profilesPath, key);
IDictionary<string, ResolvedNodeIdentity> identities = source.Resolve(new[] { "renamed", "duplicate", "broken" });
Equal(NodeIdentityStrength.Strong, identities["renamed"].Strength, "valid metadata resolves strongly");
Equal(NodeIdentityStrength.SessionOnly, identities["duplicate"].Strength, "duplicate conflict fails closed");
Equal(NodeIdentityStrength.SessionOnly, identities["broken"].Strength, "malformed metadata fails closed");
Equal(beforeConfigHash, FileHash(configPath), "resolver never writes generated config");
Equal(beforeProfilesHash, FileHash(profilesPath), "resolver never writes profile metadata");
```

- [ ] **Step 2: Run the build and verify undefined resolver types**

- [ ] **Step 3: Implement bounded parsing**

Add `ResolvedNodeIdentity` and `ClashNodeIdentitySource` with limits:

```csharp
internal const long MaximumConfigBytes = 8 * 1024 * 1024;
internal const int MaximumProxyEntries = 4096;
internal const int MaximumScalarCharacters = 8192;
internal const int MaximumMaterialParameters = 64;
```

Open with `FileShare.ReadWrite | FileShare.Delete`; reject a file whose length or last-write timestamp changes during parsing. Read only active source metadata and the top-level `proxies` sequence. Allow `name`, `type`, `server`, `port`, `uuid`, `password`, `cipher`, `network`, `tls`, `servername`, `sni`, `plugin`, and bounded option maps. Never include source lines in exceptions. Return only bounded reason codes: `config-unavailable`, `metadata-missing`, `duplicate-conflict`, `config-changed`.

- [ ] **Step 4: Configure resolver paths and key injection**

Extend `MonitorConfiguration`:

```csharp
public string ClashProfilesPath;
public string IdentityKeyPath;
```

Set both in `CreateDefault`; load the installation key once and inject it into both exit and node identity components.

- [ ] **Step 5: Run full build, compare real config hashes, and commit**

Expected: all tests pass, the selected node resolves strongly, and all protected hashes remain identical.

```powershell
git add src\NodeIdentity.cs src\Program.cs tests\Tests.cs
git commit -m "feat: resolve node identity from Clash metadata"
```

### Task 3: Candidate identity and identity-based scope

**Files:**
- Modify: `src/Models.cs`
- Modify: `src/CandidateCatalog.cs`
- Modify: `src/MonitorWorker.cs`
- Modify: `src/ExperienceMemory.cs`
- Test: `tests/Tests.cs`

- [ ] **Step 1: Add failing candidate/scope tests**

```csharp
var candidate = new CandidateNode("display", 1.0, "node-v1-abc", NodeIdentityStrength.Strong);
Equal("display", candidate.Name, "candidate retains selector name");
Equal("node-v1-abc", candidate.NodeId, "candidate carries stable identity");
Equal(originalScope, renamedMemory.ResolveScope("set-a", new[] { "node-v1-a", "node-v1-b" }, services), "rename preserves scope");
```

Add an orchestration fixture where the name is unchanged but `NodeId` changes; assert previous history is unavailable.

- [ ] **Step 2: Run build and verify constructor/scope failures**

- [ ] **Step 3: Extend model/catalog**

```csharp
public CandidateNode(string name, double? multiplier)
    : this(name, multiplier, "", NodeIdentityStrength.SessionOnly) { }

public CandidateNode(string name, double? multiplier, string nodeId, NodeIdentityStrength strength)
{
    Name = name; Multiplier = multiplier; NodeId = nodeId ?? ""; IdentityStrength = strength;
}
```

Add a catalog overload that accepts resolved identities after current filtering.

- [ ] **Step 4: Resolve once at cycle start**

Build `byName` and `byStrongId` maps in `MonitorWorker`. Compute subscription fingerprint and `ResolveScope` candidate continuity from sorted strong IDs. Session-only candidates are excluded from persisted scope continuity but remain in live scans. Use deterministic live aliases for identities shared by equivalent aliases.

- [ ] **Step 5: Run `build.ps1`, require exit 0, and commit**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
git add src\Models.cs src\CandidateCatalog.cs src\MonitorWorker.cs src\ExperienceMemory.cs tests\Tests.cs
git commit -m "feat: carry stable identities through candidate discovery"
```

### Task 4: Identity-keyed quality, experience, and region evidence

**Files:**
- Modify: `src/ExperienceMemory.cs`
- Modify: `src/QualityScoring.cs`
- Modify: `src/RegionEligibilityCache.cs`
- Modify: `src/MonitorWorker.cs`
- Test: `tests/Tests.cs`

- [ ] **Step 1: Add failing isolation/migration tests**

```csharp
memory.Observe(scope, "node-v1-a", firstScan, null, now);
memory.Observe(scope, "node-v1-b", replacementScan, null, now.AddMinutes(1));
Equal(1, memory.RecentResponses(scope, "node-v1-a", 10).Count, "old physical node isolated");
Equal(1, memory.RecentResponses(scope, "node-v1-b", 10).Count, "replacement starts cold");
```

Also assert rename reuse, region cache identity matching, legacy exclusion from recommendations, and absence of fixture server/credentials in serialized state.

- [ ] **Step 2: Run `build.ps1` and observe the same-name history collision or missing overloads**

- [ ] **Step 3: Add backward-compatible identity fields**

Add `NodeId` to `NodeExperience`, `QualitySample`, and `RegionEligibilityRecord`; retain name only as display snapshot. Load old records but classify missing/non-`node-v1-` IDs as legacy. Decision queries ignore legacy records, and bounded saves prune them once strong live identities exist.

- [ ] **Step 4: Wire worker queries/writes**

Pass strong IDs to experience, quality, and region stores. Session-only candidates may use in-memory current-cycle scores but skip persistent writes/cache reads. Logs contain only `SafeName(displayName)`, identity strength, and bounded reason.

- [ ] **Step 5: Run `build.ps1`, require exit 0, and commit**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
git add src\ExperienceMemory.cs src\QualityScoring.cs src\RegionEligibilityCache.cs src\MonitorWorker.cs tests\Tests.cs
git commit -m "feat: bind persistent evidence to stable identities"
```

### Task 5: Identity-safe health and automatic transactions

**Files:**
- Modify: `src/StateStore.cs`
- Modify: `src/ConnectionAssurance.cs`
- Modify: `src/AutomaticDecisionStateMachine.cs`
- Modify: `src/AccountVerificationMemory.cs`
- Modify: `src/MonitorWorker.cs`
- Test: `tests/Tests.cs`

- [ ] **Step 1: Add failing transaction tests**

Cover renamed target continuation, same-name/different-ID cancellation, missing target after restart, session-only standby rejection, and fresh session-only hard-failure recovery:

```csharp
assurance.PendingOptimization = new PendingOptimization { Target = "old-name", TargetNodeId = "node-v1-old" };
assurance.ReconcileIdentities(new Dictionary<string, string> { { "node-v1-new", "old-name" } }, now);
Equal<PendingOptimization>(null, assurance.PendingOptimization, "same name changed identity cancels pending target");
```

- [ ] **Step 2: Run `build.ps1` and verify the old name fallback fails the new assertions**

- [ ] **Step 3: Add identity fields and one reconciliation boundary**

Add `NodeId`, `TargetNodeId`, `PreviousNodeId`, and `CurrentNodeId` only to node-bound records. Keep display snapshots for UI. Add `ReconcileIdentities(IReadOnlyDictionary<string,string> liveNamesById, DateTime now)` that clears stale preferred nodes, standbys, pending optimization, observations, rollback origins, and account verification references without name fallback.

- [ ] **Step 4: Preserve emergency recovery**

Hard-failure scanning/full verification stays unchanged. A session-only candidate may be selected only for direct `HardFailure` recovery and is never saved as preferred, standby, proven-stable, rollback-safe, or optimization target. `SevereDegradation` and proactive optimization require strong identity.

- [ ] **Step 5: Run all state-machine tests and commit**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
git add src\StateStore.cs src\ConnectionAssurance.cs src\AutomaticDecisionStateMachine.cs src\AccountVerificationMemory.cs src\MonitorWorker.cs tests\Tests.cs
git commit -m "fix: reject stale identities in automatic decisions"
```

### Task 6: Privacy and full regression verification

**Files:**
- Modify: `tests/Tests.cs`
- Modify: `tests/Release.Tests.ps1`
- Modify: `README.md`
- Modify: `README.en.md`
- Modify: `QUICKSTART.md`

- [ ] **Step 1: Add release/privacy assertions**

Require stable-identity documentation and scan generated fixture state/log/status for known server, UUID, password, and SNI strings. Retain installer/worker checks forbidding Clash writes.

- [ ] **Step 2: Run all local verification**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
node clash\enhancement.test.js
node clash\pure-clash.test.js
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Release.Tests.ps1
git diff --check
```

Expected: every command exits 0.

- [ ] **Step 3: Run real-config read-only verification**

Hash `profiles.yaml`, `clash-verge.yaml`, and all files under `profiles\`; run one built `--once --dry-run` cycle; compare hashes. Search output/state for raw connection values inside the verification process without printing them.

Expected: zero changed files and zero secret matches.

- [ ] **Step 4: Commit safeguards/docs**

```powershell
git add tests\Tests.cs tests\Release.Tests.ps1 README.md README.en.md QUICKSTART.md
git commit -m "test: verify stable identity privacy boundaries"
```

### Task 7: Publish and install preview.11

**Files:**
- Modify: `src/Program.cs`, `src/AssemblyInfo.cs`, `scripts/install.ps1`, `package-release.ps1`
- Modify: `tests/Release.Tests.ps1`, `tests/Tests.cs`, `README.md`, `README.en.md`, `QUICKSTART.md`
- Create: `docs/release-notes/v0.7.0-preview.11.md`
- Generate: `dist/ClashCompatibilityMonitor-v0.7.0-preview.11/`, ZIP, and SHA-256 sidecar

- [ ] **Step 1: Change version tests first and confirm red**

Update expected identity/status/release versions to `0.7.0-preview.11`, run the build, and expect version-only failures.

- [ ] **Step 2: Update all immutable release references**

Set preview.11 in program, assembly, installer, package, release tests, READMEs, quick start, and new release notes. Explain that preview.10 name-only history starts cold.

- [ ] **Step 3: Package and verify SHA-256**

Run `package-release.ps1`; compare the ZIP hash with its sidecar.

- [ ] **Step 4: Install with protected-file hashing**

Hash generated config and every subscription profile before/after installation. Confirm exactly one installed process and the startup shortcut target.

- [ ] **Step 5: Observe two scheduled cycles**

Do not click “立即复检”. Confirm two new `cycle trigger=scheduled` lines, current node, enabled services, bounded identity diagnostic, and no raw metadata. Do not manufacture a destructive outage; use orchestration tests unless a failure naturally occurs.

- [ ] **Step 6: Copy immutable artifacts**

Copy folder, ZIP, and sidecar to `D:\CodexStudyDocs\成品（最终文件）\长期通用（跨学期）\工具\ClashCompatibilityMonitor` and verify destination hash.

- [ ] **Step 7: Commit, push, and wait for CI**

```powershell
git add src\Program.cs src\AssemblyInfo.cs scripts\install.ps1 package-release.ps1 tests\Release.Tests.ps1 tests\Tests.cs README.md README.en.md QUICKSTART.md docs\release-notes\v0.7.0-preview.11.md
git commit -m "release: prepare v0.7.0-preview.11"
git push origin feature/decision-state-machine
gh pr checks 8 --watch --interval 10
```

Expected: clean worktree, local HEAD equals remote, all CI/CodeQL checks pass.
