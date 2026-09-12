# Z-Library Web Detection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add opt-in reachability and latency checks for `https://zh.z-library.sk/` without claiming login, search, or download functionality.

**Architecture:** Extend the existing `ServiceKind` and `HttpServiceProbe` path; no new browser automation or URL discovery. A selected service joins the normal scanner, while a status-specific presentation label keeps HTTP success distinct from functional proof.

**Tech Stack:** C# / .NET Framework 4.x WinForms, existing PowerShell build and release tests.

---

### Task 1: Service identity and selected-only scanning

**Files:** Modify `src/Models.cs`, `src/CompatibilityScanner.cs`, `tests/Tests.cs`.

- [ ] **Step 1: Write failing test.** In `Tests.Main`, call `ZLibraryWebBehavior()`. Add a method that uses `Enum.TryParse("ZLibraryWeb", out ServiceKind parsed)` and asserts true, asserts `HttpServiceProbe.Endpoint(parsed).AbsoluteUri == "https://zh.z-library.sk/"`, calls `CompatibilityScanner.ScanSelected` with only `parsed` and checks `FakeProbe.Calls` contains exactly one entry, and checks `UserPreferences.Defaults().RequiredServices` omits it.
- [ ] **Step 2: Run red test.** Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`; expect `Enum.TryParse` assertion false or incorrect endpoint, not an infrastructure error.
- [ ] **Step 3: Implement minimal service path.** Append `ZLibraryWeb` to `ServiceKind` (preserve existing enum values). Add `case ServiceKind.ZLibraryWeb: return new Uri("https://zh.z-library.sk/");` to `HttpServiceProbe.Endpoint`. `ScanSelected` already accepts arbitrary selected services; do not change defaults.
- [ ] **Step 4: Run green test.** Run the same build command; expect the new service identity and selected-only assertions to pass.
- [ ] **Step 5: Commit.** Stage only `src/Models.cs`, `src/CompatibilityScanner.cs`, and `tests/Tests.cs`; commit `feat: add optional Z-Library probe identity`.

### Task 2: Conservative HTTP classification and truthful status

**Files:** Modify `src/CompatibilityScanner.cs`, `src/MonitorSnapshot.cs`, `tests/Tests.cs`.

- [ ] **Step 1: Write failing tests.** Assert Z-Library `EvaluateResponse` returns `None` for HTTP 200 normal body, `Partial` for HTTP 200 containing `cf-chl`, `Partial` for HTTP 403 with a challenge header, `Partial` for HTTP 302 with any `Location`, `Region` for an explicit region-block body, and `Service` for ordinary HTTP 403/500. Assert `MonitorPresentation.ServiceLabel` returns `Z-Library 网页`, and `ServiceText` for a successful Z-Library measurement starts with `入口可达`, not `探测通过` or `登录可用`.
- [ ] **Step 2: Run red test.** Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`; expect failures specifically for challenge/redirect classification and status wording.
- [ ] **Step 3: Implement minimal classification.** Add a Z-Library case before the generic success branch: `if ((status == 200 || status == 403) && (challengeHeader || IsChallengeResponse(body))) return ProbeResult.Partial("验证页可达，未验证网站功能", elapsed); if (status == 200) return ProbeResult.Success(elapsed); if (status >= 300 && status < 400) return ProbeResult.Partial("入口发生跳转，未验证网站功能", elapsed); return ProbeResult.ServiceFailure("unexpected HTTP " + status, elapsed);`. Keep the existing `IsRegionBlocked` guard first and keep redirects disabled in `HttpClientHandler`. Add explicit presentation case and a successful-service status label `入口可达 · N ms`.
- [ ] **Step 4: Run green test.** Run the same build command; expect all C# and browser-extension tests to pass.
- [ ] **Step 5: Commit.** Stage only the three named files; commit `feat: classify Z-Library entrance evidence`.

### Task 3: User choice, documentation, and final verification

**Files:** Modify `src/DetailsForm.cs`, `tests/Tests.cs`, `README.md`, `README.en.md`, `QUICKSTART.md`.

- [ ] **Step 1: Write failing UI/persistence tests.** Instantiate `DetailsForm` with default preferences and find `CheckBox` text `Z-Library 网页` in settings controls; assert it exists and is unchecked. With preferences containing `ServiceKind.ZLibraryWeb`, assert checked. Save and load `UserPreferenceStore` in a temp directory and assert the chosen service survives. A recursive control traversal may be used inside the test; do not add production test hooks.
- [ ] **Step 2: Run red test.** Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`; expect only the missing checkbox assertion to fail.
- [ ] **Step 3: Implement minimal UI and docs.** Add `AddChoice(layout, "Z-Library 网页", new[] { ServiceKind.ZLibraryWeb }, preferences);` alongside other optional services. Add a concise Chinese/English note: fixed user-specified URL, optional by default, HTTP entrance and response time only, not login/search/download proof; no automatic mirror discovery.
- [ ] **Step 4: Run full verification.** Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`, `node .\clash\enhancement.test.js`, `powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Release.Tests.ps1`, and `git diff --check`. Expect exit code 0 for each. Do not run installation or replace v0.6.2 archive as part of this task.
- [ ] **Step 5: Commit.** Stage only the five named files; commit `feat: expose optional Z-Library web detection`.

### Task 4: Integration review

**Files:** Review the committed diff and the approved spec at `docs/superpowers/specs/2026-09-12-zlibrary-web-detection-design.md`.

- [ ] Verify every selected-only, failure-classification, status-label, and no-Clash-config-change requirement against tests and diff.
- [ ] Report that the external website fetch could not be validated in the design environment; do not claim live availability or login functionality.
- [ ] Keep this work on `feature/zlibrary-web-detection`; do not install, merge, or overwrite the current v0.6.2 release without a separately verified release decision.
