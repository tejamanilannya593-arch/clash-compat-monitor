# Opportunity Candidate Exploration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add deterministic `5 + 2 + 1` candidate exploration to scheduled performance optimization without changing failure-recovery ordering or allowing historical data to override fresh service measurements.

**Architecture:** Add a side-effect-free `OpportunityCandidatePlanner` that composes and interleaves live-delay, historical, and under-tested candidates. `MonitorWorker` uses the plan only in the scheduled opportunity branch, logs safe source diagnostics, and retains the existing quick-scan, fresh P75 ranking, confirmation, hysteresis, budget, observation, and rollback flow.

**Tech Stack:** C# on .NET Framework 4.x, WinForms service process, existing hand-built test executable in `tests/Tests.cs`, PowerShell build/release/install scripts, Node.js Clash configuration tests, Serena LSP diagnostics.

---

## File structure

- Create `src/OpportunityCandidatePlanner.cs`: pure candidate composition, source attribution, deterministic interleaving, deduplication, and live-delay fill.
- Modify `src/MonitorWorker.cs`: call the planner only in scheduled performance optimization and emit safe decision-trace records.
- Modify `tests/Tests.cs`: pure policy tests, MonitorWorker orchestration coverage, safe-log assertions, unchanged recovery-order regression, and preview.9 version assertion.
- Modify `src/Program.cs`, `src/AssemblyInfo.cs`, `scripts/install.ps1`, `package-release.ps1`, `tests/Release.Tests.ps1`: immutable preview.9 identity and packaging boundaries.
- Modify `README.md`, `README.en.md`, `QUICKSTART.md`: document exploration behavior and the performance-only boundary.
- Create `docs/release-notes/v0.7.0-preview.9.md`: release behavior, safety boundaries, verification, and upgrade notes.

### Task 1: Add the pure opportunity candidate planner

**Files:**
- Create: `src/OpportunityCandidatePlanner.cs`
- Modify: `tests/Tests.cs:61-109`
- Test: `tests/Tests.cs`

- [ ] **Step 1: Register a focused policy test and write the failing assertions**

Add `OpportunityCandidatePlanningBehavior();` immediately after `OpportunityOptimizationBehavior();` in `Tests.Main`, then add this method near the existing opportunity policy tests:

```csharp
private static void OpportunityCandidatePlanningBehavior()
{
    DateTime now = new DateTime(2026, 9, 21, 11, 0, 0, DateTimeKind.Utc);
    string scope = "scope";
    CandidateNode[] candidates = new[] { "current" }
        .Concat(Enumerable.Range(1, 9).Select(x => "node-" + x.ToString("D2")))
        .Select(x => new CandidateNode(x, null)).ToArray();
    Dictionary<string, int> delays = Enumerable.Range(1, 9)
        .ToDictionary(x => "node-" + x.ToString("D2"), x => x * 10, StringComparer.Ordinal);
    var experience = new ExperienceData {
        Nodes = new List<NodeExperience> {
            new NodeExperience { Scope = scope, Node = "node-06", FirstUtc = now.AddHours(-1),
                LastUtc = now.AddMinutes(-1), Samples = 6, Success = 1, LastPassed = true,
                ResponseMs = 300, RecentResponseMilliseconds = new List<double> { 300, 310, 290 } },
            new NodeExperience { Scope = scope, Node = "node-07", FirstUtc = now.AddHours(-1),
                LastUtc = now.AddMinutes(-2), Samples = 5, Success = 1, LastPassed = true,
                ResponseMs = 350, RecentResponseMilliseconds = new List<double> { 350, 340, 360 } }
        }
    };

    OpportunityCandidatePlan plan = OpportunityCandidatePlanner.Create(
        candidates, delays, "current", experience, scope, now);

    Equal("node-01:LiveDelay,node-06:Historical,node-08:Exploration,node-02:LiveDelay," +
        "node-07:Historical,node-03:LiveDelay,node-04:LiveDelay,node-05:LiveDelay",
        String.Join(",", plan.Candidates.Select(x => x.Name + ":" + x.Source)),
        "opportunity plan interleaves five live two historical and one exploration candidate");
    Equal(8, plan.Candidates.Count, "opportunity plan remains bounded to eight candidates");
    Equal(5, plan.LiveDelayCount, "opportunity plan carries five live-delay candidates");
    Equal(2, plan.HistoricalCount, "opportunity plan carries two historical candidates");
    Equal(1, plan.ExplorationCount, "opportunity plan carries one under-tested candidate");
    Equal(0, plan.FillCount, "complete source quotas need no fill");
    Equal(false, plan.Candidates.Any(x => x.Name == "current"),
        "opportunity plan excludes the current node");

    experience.Nodes.Insert(0, new NodeExperience { Scope = scope, Node = "node-02",
        FirstUtc = now.AddHours(-1), LastUtc = now.AddMinutes(-1), Samples = 8,
        Success = 1, LastPassed = true, ResponseMs = 100,
        RecentResponseMilliseconds = new List<double> { 100, 100, 100 } });
    OpportunityCandidatePlan overlap = OpportunityCandidatePlanner.Create(
        candidates, delays, "current", experience, scope, now);
    Equal(true, overlap.DuplicateRemovalCount > 0,
        "historical overlap is recorded and never duplicates a live candidate");
    Equal(overlap.Candidates.Count, overlap.Candidates.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count(),
        "opportunity plan contains distinct nodes");

    OpportunityCandidatePlan fallback = OpportunityCandidatePlanner.Create(
        candidates.Take(8), delays, "current", new ExperienceData(), scope, now);
    Equal("node-01:LiveDelay,node-06:Exploration,node-02:LiveDelay,node-03:LiveDelay," +
        "node-04:LiveDelay,node-05:LiveDelay,node-07:Fill",
        String.Join(",", fallback.Candidates.Select(x => x.Name + ":" + x.Source)),
        "missing history safely falls back to exploration and live-delay fill");

    string[] recovery = StartupRecovery.RankFastCandidates(candidates, delays, "current", 8);
    Equal("node-01,node-02,node-03,node-04,node-05,node-06,node-07,node-08",
        String.Join(",", recovery),
        "failure recovery remains strict live-delay order");
}
```

- [ ] **Step 2: Run the build and verify the new policy test fails before implementation**

Run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

Expected: compilation fails because `OpportunityCandidatePlan` and `OpportunityCandidatePlanner` do not exist.

- [ ] **Step 3: Implement the pure planner with explicit source attribution**

Create `src/OpportunityCandidatePlanner.cs` with these public types and behavior:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

public enum OpportunityCandidateSource
{
    LiveDelay,
    Historical,
    Exploration,
    Fill
}

public sealed class OpportunityCandidateChoice
{
    public OpportunityCandidateChoice(string name, OpportunityCandidateSource source)
    {
        Name = name;
        Source = source;
    }

    public string Name { get; private set; }
    public OpportunityCandidateSource Source { get; private set; }
}

public sealed class OpportunityCandidatePlan
{
    public OpportunityCandidatePlan(IList<OpportunityCandidateChoice> candidates,
        int liveDelayCount, int historicalCount, int explorationCount,
        int fillCount, int duplicateRemovalCount)
    {
        Candidates = candidates ?? new List<OpportunityCandidateChoice>();
        LiveDelayCount = liveDelayCount;
        HistoricalCount = historicalCount;
        ExplorationCount = explorationCount;
        FillCount = fillCount;
        DuplicateRemovalCount = duplicateRemovalCount;
    }

    public IList<OpportunityCandidateChoice> Candidates { get; private set; }
    public int LiveDelayCount { get; private set; }
    public int HistoricalCount { get; private set; }
    public int ExplorationCount { get; private set; }
    public int FillCount { get; private set; }
    public int DuplicateRemovalCount { get; private set; }
}

public static class OpportunityCandidatePlanner
{
    public const int MaximumCandidates = 8;
    public const int LiveDelayQuota = 5;
    public const int HistoricalQuota = 2;

    public static OpportunityCandidatePlan Create(IEnumerable<CandidateNode> candidates,
        IDictionary<string, int> delays, string current, ExperienceData experience,
        string scope, DateTime now)
    {
        string[] names = (candidates ?? Enumerable.Empty<CandidateNode>())
            .Where(x => x != null && !String.IsNullOrWhiteSpace(x.Name) && x.Name != current)
            .Select(x => x.Name).Distinct(StringComparer.Ordinal).ToArray();
        string[] liveRanked = names.OrderBy(x => ValidDelay(delays, x))
            .ThenBy(x => x, StringComparer.Ordinal).ToArray();
        string[] live = liveRanked.Take(LiveDelayQuota).ToArray();
        var reserved = new HashSet<string>(live, StringComparer.Ordinal);

        NodeExperience[] recommendations = experience == null || experience.Nodes == null
            ? new NodeExperience[0]
            : experience.Recommend(scope, names, now).ToArray();
        int duplicateRemovalCount = recommendations.Count(x => reserved.Contains(x.Node));
        string[] historical = recommendations.Select(x => x.Node)
            .Where(x => !reserved.Contains(x)).Distinct(StringComparer.Ordinal)
            .Take(HistoricalQuota).ToArray();
        foreach (string name in historical) reserved.Add(name);

        string exploration = names.Where(x => !reserved.Contains(x))
            .Select(x => new { Name = x, Experience = FindExperience(experience, scope, x) })
            .OrderBy(x => x.Experience != null)
            .ThenBy(x => x.Experience == null ? 0 : x.Experience.Samples)
            .ThenBy(x => x.Experience == null ? DateTime.MinValue : x.Experience.LastUtc)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => x.Name).FirstOrDefault();
        if (exploration != null) reserved.Add(exploration);

        var ordered = new List<OpportunityCandidateChoice>();
        AddAt(ordered, live, 0, OpportunityCandidateSource.LiveDelay);
        AddAt(ordered, historical, 0, OpportunityCandidateSource.Historical);
        if (exploration != null)
            ordered.Add(new OpportunityCandidateChoice(exploration, OpportunityCandidateSource.Exploration));
        AddAt(ordered, live, 1, OpportunityCandidateSource.LiveDelay);
        AddAt(ordered, historical, 1, OpportunityCandidateSource.Historical);
        for (int index = 2; index < live.Length; index++)
            AddAt(ordered, live, index, OpportunityCandidateSource.LiveDelay);

        int fillCount = 0;
        foreach (string name in liveRanked.Where(x => !reserved.Contains(x)))
        {
            if (ordered.Count >= MaximumCandidates) break;
            ordered.Add(new OpportunityCandidateChoice(name, OpportunityCandidateSource.Fill));
            reserved.Add(name);
            fillCount++;
        }
        if (ordered.Count > MaximumCandidates)
            ordered.RemoveRange(MaximumCandidates, ordered.Count - MaximumCandidates);
        return new OpportunityCandidatePlan(ordered, live.Length, historical.Length,
            exploration == null ? 0 : 1, fillCount, duplicateRemovalCount);
    }

    private static int ValidDelay(IDictionary<string, int> delays, string name)
    {
        int value;
        return delays != null && delays.TryGetValue(name, out value) && value > 0
            ? value : Int32.MaxValue;
    }

    private static NodeExperience FindExperience(ExperienceData experience, string scope, string name)
    {
        return experience == null || experience.Nodes == null ? null :
            experience.Nodes.FirstOrDefault(x => x != null && x.Scope == scope && x.Node == name);
    }

    private static void AddAt(List<OpportunityCandidateChoice> output, string[] values,
        int index, OpportunityCandidateSource source)
    {
        if (index >= 0 && index < values.Length)
            output.Add(new OpportunityCandidateChoice(values[index], source));
    }
}
```

- [ ] **Step 4: Run the complete C# build and confirm the planner tests pass**

Run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

Expected: exit code 0 and the new opportunity-plan assertions print `PASS`.

- [ ] **Step 5: Run Serena diagnostics on the new planner and tests**

Use `get_symbols_overview` on `src/OpportunityCandidatePlanner.cs`, then `get_diagnostics_for_file` with minimum severity 2 on `src/OpportunityCandidatePlanner.cs` and `tests/Tests.cs`.

Expected: symbol query succeeds and neither file reports an error or warning introduced by this task.

- [ ] **Step 6: Commit the pure planner**

```powershell
git add src/OpportunityCandidatePlanner.cs tests/Tests.cs
git commit -m "feat: plan diverse optimization candidates"
```

### Task 2: Integrate the plan into scheduled optimization

**Files:**
- Modify: `src/MonitorWorker.cs:843-905`
- Modify: `tests/Tests.cs:2134-2207`
- Test: `tests/Tests.cs`

- [ ] **Step 1: Change the opportunity orchestration test to require history and exploration**

In `RunAutomaticOpportunityOrchestration`, create seven alternatives and add one proven historical node outside the five lowest live-delay nodes:

```csharp
string[] alternatives = Enumerable.Range(1, 7).Select(x => "node-" + x.ToString("D2")).ToArray();
```

Add this record beside the current-node experience record:

```csharp
new NodeExperience { Scope = scope, Node = "node-06", FirstUtc = now.AddHours(-1),
    LastUtc = now.AddMinutes(-1), Samples = 6, Success = 1, LastPassed = true,
    ResponseMs = 400, RecentResponseMilliseconds = new List<double> { 400, 410, 390 } }
```

Retain the assertion that all alternatives receive one concurrent 2500 ms Mihomo delay. Replace the expected quick-scan order and add fresh-ranking and safe-trace assertions:

```csharp
Equal("node-01,node-06,node-07", String.Join(",", mihomo.ScanNodes(TimeSpan.FromSeconds(2))),
    "scheduled opportunity scan includes live historical and exploration candidates before stopping");
Equal("node-01", afterDiscovery.Assurance.PendingOptimization.Target,
    "fresh service response outranks historical candidate origin");
string trace = File.ReadAllText(Path.Combine(root, "logs", "monitor.log"));
Equal(true, trace.Contains("opportunity candidate plan live=5 historical=1 exploration=1 fill=0"),
    "opportunity trace records source counts");
Equal(true, trace.Contains("source=historical") && trace.Contains("source=exploration"),
    "opportunity trace records scanned source categories");
Equal(false, trace.Contains("node-06") || trace.Contains("node-07"),
    "opportunity trace never exposes raw node names");
```

- [ ] **Step 2: Run the build and verify orchestration fails with the old live-delay-only order**

Run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

Expected: the new orchestration assertion fails because the quick scans are still `node-01,node-02,node-03` and the source trace does not exist.

- [ ] **Step 3: Replace only the scheduled opportunity ranking with the planner**

In the `opportunityDue` branch of `MonitorWorker.RunOnce`, keep `MeasureLiveDelays(opportunityPool)` unchanged and replace the unbounded `RankFastCandidates` result with:

```csharp
OpportunityCandidatePlan candidatePlan = OpportunityCandidatePlanner.Create(
    opportunityPool, opportunityDelays, current, experience, memoryScope, clock.UtcNow);
logger.Write("opportunity candidate plan live=" + candidatePlan.LiveDelayCount +
    " historical=" + candidatePlan.HistoricalCount +
    " exploration=" + candidatePlan.ExplorationCount +
    " fill=" + candidatePlan.FillCount +
    " duplicate_removal=" + candidatePlan.DuplicateRemovalCount);
```

Replace the quick-scan loop with source-aware choices while preserving the existing stop conditions and scan timeout:

```csharp
foreach (OpportunityCandidateChoice planned in candidatePlan.Candidates)
{
    if (StartupRecovery.ShouldStopAfterCheckedCandidates(checkedCandidates) ||
        StartupRecovery.ShouldStopAfterEligibleCandidates(comparableScans.Count)) break;
    string candidateName = planned.Name;
    CandidateNode candidate = candidates.First(x => x.Name == candidateName);
    Report("正在自动比较候选：" + candidateName, currentEvidence);
    logger.Write("opportunity candidate scan node=" + SafeName(candidateName) +
        " source=" + planned.Source.ToString().ToLowerInvariant());
    CandidateScanResult quickScan = scanner.ScanSelected(candidate, quickServices,
        TimeSpan.FromSeconds(2));
    RememberRegionEligibility(memoryScope, quickScan);
    scans[candidateName] = quickScan;
    scanTimes[candidateName] = clock.UtcNow;
    checkedCandidates++;
    if (OpportunityOptimizationPolicy.IsPerformanceComparable(quickScan, quickServices))
        comparableScans.Add(quickScan);
}
```

Do not modify the confirmed-failure branch around `rescueDelays`, `RankFastCandidates`, or `RankVerifiedFastTargets`. Keep `RankPerformanceComparableTargets(comparableScans, opportunityDelays)` unchanged so fresh service evidence remains authoritative.

- [ ] **Step 4: Run the complete build and verify the integration tests pass**

Run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

Expected: exit code 0; the scheduled scan order is `node-01,node-06,node-07`, target remains `node-01`, and existing fast-failover ordering tests still pass.

- [ ] **Step 5: Check the exact production diff and Serena diagnostics**

Use Serena `find_symbol` on `MonitorWorker/RunOnce[1]` and inspect only the opportunity branch. Run `get_diagnostics_for_file` with minimum severity 2 on `src/MonitorWorker.cs` and `src/OpportunityCandidatePlanner.cs`.

Run:

```powershell
git diff --check
git diff -- src/MonitorWorker.cs src/OpportunityCandidatePlanner.cs tests/Tests.cs
```

Expected: no whitespace errors; recovery code is unchanged; diagnostics contain no errors or warnings introduced by the integration.

- [ ] **Step 6: Commit the orchestration integration**

```powershell
git add src/MonitorWorker.cs tests/Tests.cs
git commit -m "feat: explore diverse optimization candidates"
```

### Task 3: Strengthen edge cases and regression coverage

**Files:**
- Modify: `tests/Tests.cs`
- Modify if a test exposes a defect: `src/OpportunityCandidatePlanner.cs`
- Test: `tests/Tests.cs`

- [ ] **Step 1: Add deterministic edge-case assertions**

Extend `OpportunityCandidatePlanningBehavior` with these cases:

```csharp
OpportunityCandidatePlan empty = OpportunityCandidatePlanner.Create(
    null, null, "current", null, scope, now);
Equal(0, empty.Candidates.Count, "empty opportunity input produces an empty safe plan");

var stale = new ExperienceData { Nodes = new List<NodeExperience> {
    new NodeExperience { Scope = scope, Node = "node-06", FirstUtc = now.AddDays(-9),
        LastUtc = now.AddDays(-8), Samples = 20, Success = 1, LastPassed = true,
        ResponseMs = 1, RecentResponseMilliseconds = new List<double> { 1, 1, 1 } },
    new NodeExperience { Scope = "other-scope", Node = "node-07", FirstUtc = now.AddHours(-1),
        LastUtc = now.AddMinutes(-1), Samples = 20, Success = 1, LastPassed = true,
        ResponseMs = 1, RecentResponseMilliseconds = new List<double> { 1, 1, 1 } }
} };
OpportunityCandidatePlan stalePlan = OpportunityCandidatePlanner.Create(
    candidates, delays, "current", stale, scope, now);
Equal(0, stalePlan.HistoricalCount,
    "stale and foreign-scope history cannot enter opportunity history slots");
Equal("node-08", stalePlan.Candidates.First(x => x.Source == OpportunityCandidateSource.Exploration).Name,
    "an unseen in-scope candidate wins deterministic exploration over stale history");

Dictionary<string, int> tiedDelays = candidates.Where(x => x.Name != "current")
    .ToDictionary(x => x.Name, x => 100, StringComparer.Ordinal);
OpportunityCandidatePlan tied = OpportunityCandidatePlanner.Create(
    candidates, tiedDelays, "current", new ExperienceData(), scope, now);
Equal("node-01", tied.Candidates.First().Name,
    "ordinal node name resolves equal live-delay ordering deterministically");
Equal(true, tied.Candidates.Count <= OpportunityCandidatePlanner.MaximumCandidates,
    "every opportunity plan respects the hard maximum");
```

- [ ] **Step 2: Run the build and inspect any genuine edge-case failure**

Run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

Expected: the tests either pass with the Task 1 implementation or expose one narrowly defined ordering/filtering defect. If a defect appears, change only the planner branch responsible for that failed assertion and rerun until exit code 0.

- [ ] **Step 3: Run all non-C# regression suites**

```powershell
node .\clash\enhancement.test.js
node .\clash\pure-clash.test.js
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Release.Tests.ps1
git diff --check
```

Expected: both Node suites, release tests, and whitespace validation pass.

- [ ] **Step 4: Commit edge-case coverage if it changed tracked files**

```powershell
git add tests/Tests.cs src/OpportunityCandidatePlanner.cs
git commit -m "test: cover optimization candidate exploration bounds"
```

If `src/OpportunityCandidatePlanner.cs` did not change, stage only `tests/Tests.cs`.

### Task 4: Prepare immutable v0.7.0-preview.9

**Files:**
- Modify: `tests/Tests.cs:63`
- Modify: `src/Program.cs`
- Modify: `src/AssemblyInfo.cs`
- Modify: `scripts/install.ps1`
- Modify: `package-release.ps1`
- Modify: `tests/Release.Tests.ps1`
- Modify: `README.md`
- Modify: `README.en.md`
- Modify: `QUICKSTART.md`
- Create: `docs/release-notes/v0.7.0-preview.9.md`

- [ ] **Step 1: Change only the test expectation to preview.9 and verify the red release test**

Change:

```csharp
Equal("0.7.0-preview.9", MonitorIdentity.Version, "release version");
```

Run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

Expected: the release-version assertion fails because production identity still reports `0.7.0-preview.8`.

- [ ] **Step 2: Update every release identity boundary**

Apply these exact version values consistently:

```text
Product/display/package version: 0.7.0-preview.9
AssemblyVersion: 0.7.0.0
AssemblyFileVersion: 0.7.0.0
AssemblyInformationalVersion: 0.7.0-preview.9
Release directory: ClashCompatibilityMonitor-v0.7.0-preview.9
ZIP: ClashCompatibilityMonitor-v0.7.0-preview.9.zip
Checksum sidecar: ClashCompatibilityMonitor-v0.7.0-preview.9.zip.sha256
```

Update `src/Program.cs`, `src/AssemblyInfo.cs`, `scripts/install.ps1`, `package-release.ps1`, and `tests/Release.Tests.ps1`. Do not reuse or overwrite preview.8 paths.

- [ ] **Step 3: Update user documentation and create release notes**

Document these exact boundaries in both READMEs and Quick Start:

```text
Scheduled performance optimization tests a bounded mix of five live-delay leaders,
two recent high-quality nodes, and one under-tested node. Candidate origin affects
only which nodes are freshly tested; fresh service P75 remains the primary ranking
signal. Hard-failure and severe-degradation recovery remain strict live-delay flows.
```

Create `docs/release-notes/v0.7.0-preview.9.md` with:

- the performance-only `5 + 2 + 1` composition;
- deterministic interleaving and deduplication;
- eight checked / three eligible / two-second quick-probe bounds;
- fresh P75 ranking and unchanged full validation;
- safe source diagnostics;
- unchanged failure-recovery path;
- upgrade, automatic-cycle, and Clash-file integrity verification instructions.

- [ ] **Step 4: Run the complete release verification**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
node .\clash\enhancement.test.js
node .\clash\pure-clash.test.js
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Release.Tests.ps1
git diff --check
```

Expected: all commands exit 0 and release tests identify preview.9 exclusively.

- [ ] **Step 5: Commit the immutable release metadata**

```powershell
git add src/Program.cs src/AssemblyInfo.cs scripts/install.ps1 package-release.ps1 `
  tests/Release.Tests.ps1 tests/Tests.cs README.md README.en.md QUICKSTART.md `
  docs/release-notes/v0.7.0-preview.9.md
git commit -m "release: prepare v0.7.0-preview.9"
```

### Task 5: Package, install, and verify preview.9

**Files:**
- Generated: `dist/ClashCompatibilityMonitor-v0.7.0-preview.9/`
- Generated: `dist/ClashCompatibilityMonitor-v0.7.0-preview.9.zip`
- Generated: `dist/ClashCompatibilityMonitor-v0.7.0-preview.9.zip.sha256`
- Install target: `C:\Users\lenovo\AppData\Local\ClashCompatibilityMonitor`
- Product target: `D:\CodexStudyDocs\成品（最终文件）\长期通用（跨学期）\工具\ClashCompatibilityMonitor`

- [ ] **Step 1: Capture protected Clash hashes before installation**

Hash these root files when present and every file recursively under `profiles`:

```powershell
$clashRoot='C:\Users\lenovo\AppData\Roaming\io.github.clash-verge-rev.clash-verge-rev'
$protected=@('profiles.yaml','clash-verge.yaml','clash-verge-check.yaml','config.yaml','dns_config.yaml','verge.yaml') |
  ForEach-Object { Join-Path $clashRoot $_ }
$protected += @(Get-ChildItem -LiteralPath (Join-Path $clashRoot 'profiles') -File -Recurse |
  ForEach-Object FullName)
$before=@($protected | Where-Object { Test-Path -LiteralPath $_ } | Sort-Object -Unique |
  ForEach-Object { [pscustomobject]@{ Path=$_; Hash=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash } })
```

Keep `$before` in memory for the final comparison. Do not modify any Clash file.

- [ ] **Step 2: Build the preview.9 release artifacts**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\package-release.ps1
```

Expected: a preview.9 folder, ZIP, and SHA-256 sidecar are created under `dist`; the sidecar hash equals `Get-FileHash` for the ZIP.

- [ ] **Step 3: Install preview.9 from its packaged installer**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\dist\ClashCompatibilityMonitor-v0.7.0-preview.9\scripts\install.ps1 `
  -SourceExe .\dist\ClashCompatibilityMonitor-v0.7.0-preview.9\ClashCompatibilityMonitor.exe
```

Expected: the preview.8 process is replaced, the new executable reports product version `0.7.0-preview.9`, and the installer creates a recoverable upgrade backup.

- [ ] **Step 4: Verify installed process and startup shortcut**

Check that exactly one process runs from:

```text
C:\Users\lenovo\AppData\Local\ClashCompatibilityMonitor\ClashCompatibilityMonitor.exe
```

Resolve `Clash Compatibility Monitor.lnk` in the current user's Startup folder and verify its target and working directory point to the same installation directory.

- [ ] **Step 5: Observe at least two natural scheduled cycles**

Do not click “立即复检”. Read `logs/monitor.log` until two new `cycle trigger=scheduled` entries appear after the preview.9 startup timestamp. Confirm `current-status.txt` reports preview.9 and a real selected node. If automatic optimization is disabled or not due, rely on the orchestration test for exploration ordering rather than changing user preferences.

- [ ] **Step 6: Recompute and compare protected Clash hashes**

Recreate the hash list with the Step 1 command and compare by full path. Expected: identical file count, identical paths, and zero changed hashes.

- [ ] **Step 7: Copy artifacts to the D-drive product directory without overwriting older releases**

Copy:

```text
dist\ClashCompatibilityMonitor-v0.7.0-preview.9
dist\ClashCompatibilityMonitor-v0.7.0-preview.9.zip
dist\ClashCompatibilityMonitor-v0.7.0-preview.9.zip.sha256
```

to:

```text
D:\CodexStudyDocs\成品（最终文件）\长期通用（跨学期）\工具\ClashCompatibilityMonitor
```

Verify the copied ZIP SHA-256 equals both the source ZIP and sidecar. Leave preview.8 and earlier releases untouched.

- [ ] **Step 8: Perform the final clean-state verification and push the PR branch**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
node .\clash\enhancement.test.js
node .\clash\pure-clash.test.js
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Release.Tests.ps1
git diff --check
git status --short
git push
```

Expected: every verification exits 0, the working tree is clean, and PR #8 receives the preview.9 commits.

Report the installed version, current node, two automatic-cycle times, candidate-exploration test order, process count, startup target, protected-file comparison, artifact paths, and final ZIP SHA-256.
