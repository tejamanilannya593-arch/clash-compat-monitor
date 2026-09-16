# Fast and Accurate Node Quality Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Return the current-node verdict within one probe timeout and report conservative P75-based quality instead of a misleading average.

**Architecture:** `CompatibilityScanner` owns concurrent orchestration and deterministic aggregation. `QualityMeasurement` owns the shared P75 calculation used by display and switching, while `MonitorPresentation` owns labels and the slowest-service warning. Existing failover gates remain unchanged.

**Tech Stack:** C#/.NET Framework WinForms, `Task`, existing custom test runner, PowerShell packaging.

---

### Task 1: Concurrent service scan

**Files:**
- Modify: `tests/Tests.cs`
- Modify: `src/CompatibilityScanner.cs`

- [ ] Add a synchronization-based probe test that requires multiple service calls to enter before any may return, plus a test proving failure selection follows requested service order.
- [ ] Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1` and confirm the concurrency test fails by timeout/serialization.
- [ ] In `ScanSelected`, select the node once, start exit identity and one bounded `Task<ProbeResult>` per service, await them, then aggregate in original service order. Protect test call tracking with a lock.
- [ ] Re-run the build and confirm scan, count, failure, cancellation, and concurrency tests pass.

### Task 2: Conservative P75 quality

**Files:**
- Modify: `tests/Tests.cs`
- Modify: `src/QualityScoring.cs`
- Modify: `src/FailoverController.cs`
- Modify: `src/MonitorSnapshot.cs`

- [ ] Add failing tests asserting values `214,295,322,377,670,824,2569` produce P75 `824`, label “较慢”, and a `Steam 社区 2569 ms` warning.
- [ ] Add failing boundary tests for 300/500/800 ms and candidate-history preference at 500/501 ms.
- [ ] Implement nearest-rank P75 in `QualityMeasurement.ResponseMilliseconds`, add separate excellent/good constants, and retain the 800 ms current-node optimization trigger.
- [ ] Make `ResponseLabel` use the same P75 and append the slowest passed service when it exceeds 2000 ms.
- [ ] Re-run the build and confirm all quality and failover tests pass.

### Task 3: Accurate completed-state presentation

**Files:**
- Modify: `tests/Tests.cs`
- Modify: `src/MonitorSnapshot.cs`

- [ ] Add a failing test that a `BasicCompatible` snapshot and its background-progress derivative both display “基础连接可用”.
- [ ] Update `StateLabel` to prioritize `CandidateHealth.BasicCompatible`; leave unknown evidence as pending/checking.
- [ ] Re-run the build and confirm presentation tests pass.

### Task 4: Release and live verification

**Files:**
- Modify: version/release files matched by `rg "0.7.0-preview.2"` to `0.7.0-preview.3`
- Create: `docs/release-notes/v0.7.0-preview.3.md`

- [ ] Update version metadata, installer/package checks, README summaries, and release notes with the exact new quality semantics.
- [ ] Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\package-release.ps1`; expect exit 0 from build, browser tests, enhancement tests, and release checks.
- [ ] Back up the installed executable, deploy preview.3 without changing preferences, and restart the monitor from the existing enabled startup shortcut.
- [ ] Confirm fresh log/current-status timestamps, inspect elapsed time and P75 label, verify Google 204 and GitHub 200 through port 7897, then copy the release ZIP and checksum to the D: product directory.

