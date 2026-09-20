# Five-Minute Automatic Optimization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Automatically discover, confirm, and select a materially faster supported node within five minutes when automatic optimization is enabled and the current node is persistently slow.

**Architecture:** Add a pure opportunity policy and separately persisted pending-target state, then reuse the existing all-node delay and bounded service-ranking mechanics from fast failover. A first scheduled opportunity scan prepares a target; a 30-second confirmation cycle rechecks only current and target before entering the existing switch-observation and rollback flow.

**Tech Stack:** C#/.NET Framework 4, WinForms, `JavaScriptSerializer`, Mihomo named-pipe REST API, PowerShell packaging, repository console tests.

---

### Task 1: Add opportunity policy and pending state

**Files:**
- Create: `src/OpportunityOptimization.cs`
- Modify: `src/ConnectionAssurance.cs`
- Modify: `tests/Tests.cs`

- [ ] **Step 1: Write failing pure-policy tests**

Add `OpportunityOptimizationBehavior()` to the main test sequence and assert these exact boundaries:

```csharp
DateTime now = new DateTime(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);
Equal(true, OpportunityOptimizationPolicy.ShouldScan(true, false,
    new[] { 900d, 850d, 801d }, DateTime.MinValue, now),
    "slow current schedules automatic opportunity discovery");
Equal(false, OpportunityOptimizationPolicy.ShouldScan(true, false,
    new[] { 900d, 800d, 700d }, DateTime.MinValue, now),
    "good current does not schedule opportunity discovery");
Equal(false, OpportunityOptimizationPolicy.ShouldScan(true, false,
    new[] { 900d, 850d, 801d }, now.AddMinutes(-2), now),
    "opportunity discovery is limited to one round per three minutes");
Equal(true, OpportunityOptimizationPolicy.MateriallyBetter(1000, 800),
    "twenty percent faster target is material");
Equal(false, OpportunityOptimizationPolicy.MateriallyBetter(1000, 801),
    "less than twenty percent is not material");
```

Build representative scans and assert that `IsPerformanceComparable` accepts a `BasicCompatible` scan only when the actual exit is in the AI-region intersection, all required services are present, no result is unknown or definitely failed, and every service is at or below 1500 ms.

- [ ] **Step 2: Run tests and verify RED**

Run:

```powershell
& .\build.ps1
```

Expected: compilation fails because `OpportunityOptimizationPolicy` and pending optimization members do not exist.

- [ ] **Step 3: Implement the pure policy**

Create `src/OpportunityOptimization.cs` with these public shapes:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

public sealed class PendingOptimization
{
    public string Scope { get; set; }
    public string Current { get; set; }
    public string Target { get; set; }
    public double BaselineResponse { get; set; }
    public double TargetResponse { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public static class OpportunityOptimizationPolicy
{
    public static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan ConfirmationInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(2);

    public static bool ShouldScan(bool enabled, bool observing,
        IEnumerable<double> currentResponses, DateTime lastScanUtc, DateTime nowUtc)
    {
        return enabled && !observing && QualityPolicy.CurrentNeedsOptimization(currentResponses) &&
            (lastScanUtc == DateTime.MinValue || nowUtc - lastScanUtc >= ScanInterval);
    }

    public static bool IsPerformanceComparable(CandidateScanResult scan,
        IEnumerable<ServiceKind> requiredServices)
    {
        if (scan == null || !AiRegionPolicy.SupportsBoth(scan.ExitCountryCode) ||
            !ServiceEvidencePolicy.CanHold(scan) || !QualityPolicy.ServicesWithinLimit(scan)) return false;
        var required = (requiredServices ?? Enumerable.Empty<ServiceKind>()).Distinct().ToList();
        if (required.Any(service => !scan.ServiceResults.ContainsKey(service))) return false;
        return required.All(service => {
            ProbeResult result = scan.ServiceResults[service];
            return result != null && result.FailureKind != ProbeFailureKind.Unverified &&
                (result.Passed || result.FailureKind == ProbeFailureKind.Partial);
        });
    }

    public static bool MateriallyBetter(double baseline, double target)
    {
        return baseline > QualityPolicy.OptimizationResponseMilliseconds &&
            target <= QualityPolicy.OptimizationResponseMilliseconds && target <= baseline * 0.80;
    }

    public static bool IsFresh(PendingOptimization pending, string scope,
        string current, DateTime nowUtc)
    {
        return pending != null && pending.Scope == scope && pending.Current == current &&
            pending.CreatedUtc <= nowUtc && pending.CreatedUtc >= nowUtc - PendingLifetime;
    }
}
```

- [ ] **Step 4: Persist pending state separately from switch observation**

Add to `ConnectionAssurance`:

```csharp
public PendingOptimization PendingOptimization { get; set; }
public DateTime LastOpportunityScanUtc { get; set; }

public void ClearPendingOptimization() { PendingOptimization = null; }
```

Clear pending state in `SetScope` when the scope changes and in `Begin` when an actual switch transaction starts. Add a serializer round-trip assertion proving the pending target survives restart without populating `Target` or `Previous`.

- [ ] **Step 5: Run tests and commit**

Run `& .\build.ps1`. Expected: all tests pass.

```powershell
git add src/OpportunityOptimization.cs src/ConnectionAssurance.cs tests/Tests.cs
git commit -m "feat: model bounded automatic opportunity scans"
```

### Task 2: Orchestrate discovery and confirmation

**Files:**
- Modify: `src/MonitorWorker.cs`
- Modify: `src/StartupRecovery.cs`
- Modify: `tests/Tests.cs`

- [ ] **Step 1: Add failing worker orchestration tests**

Extend `FastFailoverWorkerOrchestration()` with a two-cycle scenario whose current node has three stored responses above 800 ms. The first scheduled cycle must:

```csharp
Equal(true, mihomo.DelayNodes(2500).Distinct(StringComparer.Ordinal).Count() == alternatives.Length,
    "scheduled opportunity scan measures every alternative once");
Equal(3, mihomo.QuickEligibleScans,
    "scheduled opportunity scan stops after three comparable candidates");
Equal("current", mihomo.GetSelected("shared"),
    "first opportunity scan prepares a target without switching");
Equal("node-01", persisted.Assurance.PendingOptimization.Target,
    "best real-service target is persisted for confirmation");
```

Advance the fake clock by 30 seconds and run a second cycle. Assert that it probes only current and `node-01`, performs no second all-node delay round, switches to `node-01`, enters normal `assurance.Target` observation, and clears `PendingOptimization`.

Add cancellation cases for a recovered current node, external selector change, expired pending state, active service incident, target failure, and insufficient 20 percent improvement. Add an acceptance test whose scheduled cycles complete the switch within five fake-clock minutes without calling `RequestCheck`.

- [ ] **Step 2: Run tests and verify RED**

Run `& .\build.ps1`.

Expected: the scheduled healthy path never performs the all-node delay round and no pending target is created.

- [ ] **Step 3: Extract reusable bounded comparison helpers**

Add to `StartupRecovery`:

```csharp
public static string[] RankPerformanceComparableTargets(
    IEnumerable<CandidateScanResult> scans, IDictionary<string, int> delays)
{
    return (scans ?? Enumerable.Empty<CandidateScanResult>())
        .OrderBy(scan => QualityMeasurement.ResponseMilliseconds(scan, 5000))
        .ThenBy(scan => delays.ContainsKey(scan.Name) ? delays[scan.Name] : Int32.MaxValue)
        .ThenBy(scan => scan.Name, StringComparer.Ordinal)
        .Select(scan => scan.Name).ToArray();
}
```

In `MonitorWorker`, extract the existing parallel `GetDelay(..., 2500)` block into a private `MeasureLiveDelays(IEnumerable<CandidateNode>)` method and use it from both emergency failover and opportunity discovery. Preserve one delay call per candidate and the existing exception-to-`Int32.MaxValue` behavior.

- [ ] **Step 4: Implement first-cycle opportunity discovery**

After current-node evidence is recorded and before ordinary three-node maintenance, evaluate:

```csharp
bool opportunityDue = OpportunityOptimizationPolicy.ShouldScan(
    preferences.AutomaticOptimization, observing,
    experience.RecentResponses(memoryScope, current, 3),
    assurance.LastOpportunityScanUtc, clock.UtcNow);
```

When due and traffic/path/incident checks allow it:

1. Run one all-node concurrent delay round excluding the current node.
2. Iterate `StartupRecovery.RankFastCandidates` in live-delay order.
3. Reuse `StartupRecovery.FastProbeServices(requiredServices, ServiceKind.ChatGPT)` with a two-second timeout.
4. Skip a fresh cached unsupported exit.
5. Keep only scans satisfying `OpportunityOptimizationPolicy.IsPerformanceComparable` for the probed services.
6. Stop after three qualifying or eight checked candidates.
7. Rank the qualifying scans by real-service P75.
8. Fully scan the winner with `requiredServices`.
9. If the full scan remains performance-comparable and materially better than the current rolling median, persist `PendingOptimization` and set the completed snapshot's next interval to 30 seconds.
10. Otherwise clear pending state and retain the current node.

Set `LastOpportunityScanUtc` when the all-node round begins so failed scans cannot loop every minute.

- [ ] **Step 5: Implement confirmation-cycle switching**

Before starting a new opportunity scan, check `OpportunityOptimizationPolicy.IsFresh`. Re-scan the pending target with every required service, compare it with the just-completed current scan, and switch only when:

```csharp
OpportunityOptimizationPolicy.IsPerformanceComparable(targetScan, requiredServices) &&
OpportunityOptimizationPolicy.MateriallyBetter(
    QualityMeasurement.ResponseMilliseconds(currentScan, pending.BaselineResponse),
    QualityMeasurement.ResponseMilliseconds(targetScan, pending.TargetResponse)) &&
String.Equals(mihomo.GetSelected(config.SharedGroup), pending.Current, StringComparison.Ordinal)
```

Use `SelectRecorded`, `assurance.Begin`, `controller.RecordSwitch`, and the existing observation interval. Clear pending state on every success, rejection, expiry, external selection, active incident, path mismatch, or traffic postponement.

- [ ] **Step 6: Run tests and commit**

Run:

```powershell
& .\build.ps1
node .\clash\enhancement.test.js
node .\clash\pure-clash.test.js
```

Expected: all tests pass and no test observes a repeated all-node delay round during confirmation.

```powershell
git add src/MonitorWorker.cs src/StartupRecovery.cs tests/Tests.cs
git commit -m "feat: complete automatic optimization within five minutes"
```

### Task 3: Distinguish scheduled and requested diagnostics

**Files:**
- Modify: `src/MonitorCoordinator.cs`
- Modify: `src/MonitorWorker.cs`
- Modify: `tests/Tests.cs`

- [ ] **Step 1: Write failing trigger tests**

Add a fake runner implementing a new `ITriggeredCycleRunner` and verify the coordinator sends `Startup` on its first cycle, `Requested` after `RequestCheck()`, and `Scheduled` when `NextCheckUtc` expires. Assert that all three triggers return the same opportunity-policy result for identical inputs.

- [ ] **Step 2: Implement trigger propagation**

Add:

```csharp
public enum MonitorCycleTrigger { Startup, Scheduled, Requested }

public interface ITriggeredCycleRunner : ICycleRunner
{
    MonitorSnapshot Run(UserPreferences preferences, MonitorCycleTrigger trigger);
}
```

In `MonitorCoordinator.Loop`, capture whether the first run or `checkRequested` caused the cycle and call the triggered interface when available. `MonitorWorker.Run(UserPreferences)` delegates to `Run(preferences, MonitorCycleTrigger.Scheduled)`. The triggered overload logs `cycle trigger=startup|scheduled|requested` and must not branch on the trigger.

- [ ] **Step 3: Verify and commit**

Run `& .\build.ps1`. Expected: all trigger and behavior tests pass.

```powershell
git add src/MonitorCoordinator.cs src/MonitorWorker.cs tests/Tests.cs
git commit -m "chore: expose automatic and requested cycle triggers"
```

### Task 4: Publish and verify immutable preview.6

**Files:**
- Modify: `src/Program.cs`
- Modify: `src/AssemblyInfo.cs`
- Modify: `scripts/install.ps1`
- Modify: `package-release.ps1`
- Modify: `tests/Release.Tests.ps1`
- Modify: `README.md`
- Modify: `README.en.md`
- Modify: `QUICKSTART.md`
- Create: `docs/release-notes/v0.7.0-preview.6.md`

- [ ] **Step 1: Update failing release assertions**

Require `0.7.0-preview.6`, its release note, and documentation phrases describing the three-minute opportunity scan, 30-second confirmation, five-minute objective, and unchanged manual/automatic safety gates. Run `& .\tests\Release.Tests.ps1` and expect failure against preview.5 metadata.

- [ ] **Step 2: Update release metadata and documentation**

Keep Windows file version `0.7.0.0`, set informational/product version to `0.7.0-preview.6`, and document that the five-minute objective excludes pauses, proxy conflicts, Mihomo unavailability, and active foreground-traffic postponement.

The release note must state that proactive `BasicCompatible` comparison does not claim account-level compatibility and that every target still passes actual-exit gating plus a complete selected-service scan.

- [ ] **Step 3: Run complete verification and package**

Run:

```powershell
& .\build.ps1
node .\clash\enhancement.test.js
node .\clash\pure-clash.test.js
& .\tests\Release.Tests.ps1
git diff --check
& .\package-release.ps1
```

Expected: every command exits 0 and preview.6 folder, ZIP, and SHA-256 sidecar are created.

- [ ] **Step 4: Commit, install, and observe**

Commit release metadata as `release: publish five-minute optimization preview.6`. Record pre-install SHA-256 for `profiles.yaml` and `clash-verge.yaml`, install preview.6, verify one process and the Startup shortcut, then observe an automatic slow-node scenario for up to five minutes without clicking **立即复检**.

The log must show a scheduled opportunity scan, pending target, 30-second confirmation, and either a safe switch or an explicit hold reason. Recheck both Clash hashes after installation.

- [ ] **Step 5: Copy immutable artifacts**

Copy the preview.6 folder, ZIP, and sidecar to:

```text
D:\CodexStudyDocs\成品（最终文件）\长期通用（跨学期）\工具\ClashCompatibilityMonitor
```

Refuse to overwrite an existing preview.6 artifact. Recompute the destination ZIP SHA-256 and require it to match the sidecar before reporting completion.
