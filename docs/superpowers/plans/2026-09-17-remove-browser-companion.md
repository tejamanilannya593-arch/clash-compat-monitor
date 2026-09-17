# Remove Browser Companion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship a monitor with no browser companion runtime, UI, build artifact, installation registration, or release content while preserving conservative network-only node protection.

**Architecture:** Remove the browser subsystem at its boundaries first, then collapse quality eligibility to current network evidence plus existing historical stability gates. Treat old browser files and registry values only as upgrade debris that the installer backs up and removes.

**Tech Stack:** C#/.NET Framework WinForms, PowerShell installer and release checks, existing custom C# test runner.

---

### Task 1: Define removal and migration tests

**Files:**
- Modify: `tests/Release.Tests.ps1`
- Modify: `tests/Tests.cs`

- [ ] Assert build/package scripts contain no BrowserHost or browser-extension production step and the release tree has neither artifact.
- [ ] Assert version 2 preferences containing `browserConversation=True` load normally, while a new save writes version 3 without that field.
- [ ] Assert quality eligibility accepts a fully compatible network scan without browser proof and still rejects BasicCompatible evidence.
- [ ] Run the focused tests and observe failures caused by the existing browser implementation.

### Task 2: Remove browser runtime and UI

**Files:**
- Delete: `src/AccountVerificationMemory.cs`
- Delete: `src/BrowserBridgeServer.cs`
- Delete: `src/BrowserConversationCoordinator.cs`
- Delete: `src/BrowserConversationProof.cs`
- Delete: `src/BrowserHostProgram.cs`
- Delete: `src/BrowserNativeProtocol.cs`
- Delete: `src/BrowserVerificationForm.cs`
- Modify: `src/Program.cs`, `src/MonitorCoordinator.cs`, `src/MonitorWorker.cs`, `src/MonitorSnapshot.cs`, `src/ConnectionAssurance.cs`, `src/ExperienceMemory.cs`, `src/ServiceEvidencePolicy.cs`, `src/TrayHost.cs`, `src/DetailsForm.cs`, `src/UserPreferences.cs`, `src/StatusReport.cs`

- [ ] Remove browser interfaces, callbacks, state, forms, bridge startup, proof storage and rollback paths.
- [ ] Change `CanQualitySwitch` to accept only `Compatible` network evidence; leave historical eligibility checks in `ExperienceData.IsProvenStable` unchanged.
- [ ] Migrate preferences to version 3 while reading version 1/2 and ignoring the old browser field.
- [ ] Compile and fix only browser-removal errors until C# tests pass.

### Task 3: Remove browser build and source assets

**Files:**
- Delete: every tracked file under `browser-extension/`
- Modify: `build.ps1`, `package-release.ps1`, `tests/Release.Tests.ps1`

- [ ] Remove browser JavaScript tests, BrowserHost compilation, browser-extension release directory and copy list.
- [ ] Keep the main WinForms executable build unchanged.
- [ ] Run build and release tests; confirm no browser artifact is generated or required.

### Task 4: Add safe upgrade cleanup

**Files:**
- Modify: `scripts/install.ps1`, `scripts/uninstall.ps1`, `scripts/upgrade.ps1`, `scripts/diagnose.ps1`

- [ ] Back up any legacy host, manifest, extension directory and registration values before installation changes.
- [ ] On successful upgrade, stop a legacy host and remove its files plus Chrome/Edge registrations.
- [ ] On installation failure, restore the previous monitor and all backed-up legacy browser items.
- [ ] Keep uninstall cleanup for old-version debris and verify Clash protected configuration hashes remain unchanged.

### Task 5: Release preview.4 and install

**Files:**
- Modify: versioned source, scripts, READMEs and release tests
- Create: `docs/release-notes/v0.7.0-preview.4.md`

- [ ] Update version strings and documentation to describe network-only evidence accurately.
- [ ] Run the full package command and require exit 0 for C#, release and enhancement tests.
- [ ] Back up the live installation, install preview.4, remove legacy browser artifacts/registrations, preserve preferences and startup, and restart the monitor.
- [ ] Verify a fresh current-node cycle, Google 204, GitHub 200, one running process and a ZIP/checksum copied to the D: product directory.

