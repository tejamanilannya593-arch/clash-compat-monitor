# Severe-Latency Confirmation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Require one focused current-node retry before a passed service response above 2000 ms can trigger fast failover.

**Architecture:** Extend the pure startup-recovery policy so severe latency requests confirmation and has an explicit confirmation predicate. Reuse the existing `ScanSelected` retry and success-merge branch in `MonitorWorker`; definite service and region failures continue directly into bounded candidate discovery.

**Tech Stack:** C#/.NET Framework 4, repository console tests, PowerShell build and release tests, Node.js Clash tests.

---

### Task 1: Specify severe-latency confirmation policy

**Files:**
- Modify: `tests/Tests.cs`
- Modify: `src/StartupRecovery.cs`

- [ ] **Step 1: Write failing policy tests**

Extend the existing `StartupRecovery` assertions in `tests/Tests.cs`:

```csharp
Equal(true, StartupRecovery.RequiresRepeatConfirmation(severeLatency),
    "severe latency is confirmed once before candidate discovery");
Equal(false, StartupRecovery.ConfirmsSevereLatency(
    new CandidateScanResult("x", CandidateHealth.Compatible, null, "recovered", 500, 1,
        new Dictionary<ServiceKind, ProbeResult> {
            { ServiceKind.GitHub, ProbeResult.Success(500) }
        }), ServiceKind.GitHub),
    "recovered retry does not confirm severe latency");
Equal(true, StartupRecovery.ConfirmsSevereLatency(
    new CandidateScanResult("x", CandidateHealth.Compatible, null, "still slow", 2100, 1,
        new Dictionary<ServiceKind, ProbeResult> {
            { ServiceKind.GitHub, ProbeResult.Success(2100) }
        }), ServiceKind.GitHub),
    "repeated response above two seconds confirms severe latency");
Equal(false, StartupRecovery.ConfirmsSevereLatency(
    new CandidateScanResult("x", CandidateHealth.Unknown, null, "unknown"),
    ServiceKind.GitHub),
    "unknown retry cannot manufacture confirmed degradation");
```

Keep the existing assertions that `RegionBlocked` and `ServiceFailed` do not require repeat confirmation.

- [ ] **Step 2: Run the build and verify RED**

Run:

```powershell
& .\build.ps1
```

Expected: compilation fails because `StartupRecovery.ConfirmsSevereLatency` does not exist, and the severe-latency repeat assertion would fail with the current implementation.

- [ ] **Step 3: Implement the minimal pure policy**

Change `RequiresRepeatConfirmation` and add the confirmation predicate:

```csharp
public static bool RequiresRepeatConfirmation(CandidateScanResult scan)
{
    return scan != null && (scan.Health == CandidateHealth.Transient ||
        QualityPolicy.SeverelySlowService(scan).HasValue);
}

public static bool ConfirmsSevereLatency(CandidateScanResult confirmation, ServiceKind service)
{
    if (confirmation == null || confirmation.Health == CandidateHealth.Unknown ||
        confirmation.ServiceResults == null) return false;
    ProbeResult result;
    if (!confirmation.ServiceResults.TryGetValue(service, out result) || result == null ||
        result.FailureKind == ProbeFailureKind.Unverified) return false;
    return !result.Passed ||
        result.ElapsedMilliseconds > QualityPolicy.SevereServiceResponseMilliseconds;
}
```

- [ ] **Step 4: Run tests and commit**

Run `& .\build.ps1`. Expected: all repository console tests pass.

```powershell
git add src/StartupRecovery.cs tests/Tests.cs
git commit -m "fix: confirm severe latency before failover"
```

### Task 2: Apply confirmation in worker orchestration

**Files:**
- Modify: `tests/Tests.cs`
- Modify: `src/MonitorWorker.cs`

- [ ] **Step 1: Write failing orchestration tests**

Add this probe beside `OrchestratedServiceProbe`:

```csharp
private sealed class SevereLatencyProbe : IServiceProbe
{
    private readonly OrchestratedMihomo mihomo;
    private readonly long retryMilliseconds;
    private int currentChatGptCalls;

    public SevereLatencyProbe(OrchestratedMihomo mihomo, long retryMilliseconds)
    {
        this.mihomo = mihomo;
        this.retryMilliseconds = retryMilliseconds;
    }

    public int CurrentChatGptCalls { get { return currentChatGptCalls; } }

    public ProbeResult Probe(ServiceKind service, TimeSpan timeout)
    {
        OrchestratedScanEvent scan = mihomo.ActiveScan();
        scan.ObserveTimeout(timeout);
        scan.ObserveService(service);
        if (scan.Node == "current" && service == ServiceKind.ChatGPT)
        {
            int call = System.Threading.Interlocked.Increment(ref currentChatGptCalls);
            return ProbeResult.Success(call == 1 ? 2100 : retryMilliseconds);
        }
        return ProbeResult.Success(300);
    }
}
```

Add `RunSevereLatencyConfirmationOrchestration()` to the main test sequence. Its first worker uses three alternatives, `SevereLatencyProbe(mihomo, 500)`, and a supported `OrchestratedExitIdentityProbe`. After `RunOnce`, assert:

```csharp
Equal(2, probe.CurrentChatGptCalls,
    "severe latency receives one focused current-node retry");
Equal(0, mihomo.DelayNodes(2500).Count,
    "recovered retry avoids the all-node delay round");
Equal("current", mihomo.GetSelected("shared"),
    "recovered retry keeps the current node");
```

Create a second isolated worker with `SevereLatencyProbe(mihomo, 2100)`. After `RunOnce`, assert:

```csharp
Equal(2, probe.CurrentChatGptCalls,
    "repeated severe latency performs one focused confirmation");
Equal(alternatives.Length, mihomo.DelayNodes(2500).Count,
    "confirmed severe latency performs one all-node delay round");
Equal(alternatives.Length,
    mihomo.DelayNodes(2500).Distinct(StringComparer.Ordinal).Count(),
    "confirmed severe latency never repeats the all-node delay round");
Equal("node-01", mihomo.GetSelected("shared"),
    "confirmed severe latency may switch after complete target validation");
```

Use separate temporary roots and `finally { DeleteDirectoryEventually(root); }` cleanup for both workers. Retain the existing region-blocked orchestration assertion showing no focused current-node retry is added.

- [ ] **Step 2: Run the build and verify RED**

Run `& .\build.ps1`.

Expected: the recovery scenario immediately starts the all-node delay round because `MonitorWorker` still sets severe latency as confirmed unconditionally.

- [ ] **Step 3: Use the retry result for the worker decision**

Replace the unconditional severe-latency confirmation in `MonitorWorker` with:

```csharp
currentFailureConfirmed = severeLatency
    ? StartupRecovery.ConfirmsSevereLatency(confirmation, failedService)
    : confirmation.Health != CandidateHealth.Unknown &&
        !ConnectionAssurance.Passed(confirmation);
```

Keep the existing `ApplySuccessfulConfirmation` branch so a recovered service measurement replaces the original spike in the final snapshot and history.

- [ ] **Step 4: Verify focused behavior**

Run `& .\build.ps1`. Expected: policy and orchestration tests pass, recovered latency performs no candidate scan, and repeated latency performs one candidate scan.

- [ ] **Step 5: Run full regression verification**

Run:

```powershell
& .\build.ps1
node .\clash\enhancement.test.js
node .\clash\pure-clash.test.js
& .\tests\Release.Tests.ps1
git diff --check
```

Expected: every command exits 0; no existing emergency failover, opportunity optimization, packaging, or release invariant regresses.

- [ ] **Step 6: Commit the orchestration change**

```powershell
git add src/MonitorWorker.cs tests/Tests.cs
git commit -m "fix: suppress failover after recovered latency spike"
```

### Task 3: Runtime acceptance check

**Files:**
- No source changes.

- [ ] **Step 1: Build an unpublished acceptance binary**

Use the repository build output without changing the installed preview.6 files or release version.

- [ ] **Step 2: Exercise scripted recovery and repeated-latency scenarios**

Confirm logs show `reason=severe-latency action=confirm`; the recovered case logs `current retry recovered` without `fast selection`, while the repeated-slow case logs one `fast selection delay_ms=` entry.

- [ ] **Step 3: Verify protected files remain unchanged**

Recompute SHA-256 for the Clash configuration and subscription files and compare them with their pre-test hashes. Do not publish or install a new preview until a separate versioned release task is approved.
