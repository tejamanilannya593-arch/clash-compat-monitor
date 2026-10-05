# Clash Compatibility Monitor

> Continuous ChatGPT/Gemini node optimization for Clash/Mihomo on Windows

[![CI](https://github.com/tejamanilannya593-arch/clash-compat-monitor/actions/workflows/ci.yml/badge.svg)](https://github.com/tejamanilannya593-arch/clash-compat-monitor/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/tejamanilannya593-arch/clash-compat-monitor)](https://github.com/tejamanilannya593-arch/clash-compat-monitor/releases/latest)
[![License](https://img.shields.io/github/license/tejamanilannya593-arch/clash-compat-monitor)](LICENSE)

[Download the latest release](https://github.com/tejamanilannya593-arch/clash-compat-monitor/releases/latest) · [中文](README.md)

Clash Compatibility Monitor rechecks the current node every 60 seconds by default. When basic, ChatGPT, and Gemini website delays are measurable, the actual exit region is eligible, and both core-site delays are at most 800 ms, it keeps that node. Website timing does not verify login or conversation functions.

## Continuous optimization behavior

- Rechecks the current node every 60 seconds by default. A qualified node at or below 800 ms stays selected without a full sweep.
- When the current node is slow or website latency is unavailable, measures basic, ChatGPT, and Gemini website delays for every node, sorts by the slower core-site delay, then checks actual-exit region eligibility.
- Requires all three delays to be measurable. The actual exit must meet the shared ChatGPT and Gemini region policy. Login-chain evidence is not an automatic-switch gate.
- Selects the first eligible candidate with `max(ChatGPT latency, Gemini latency) ≤ 800 ms`, then verifies it. A failed verification rolls back and resumes the search.
- When no candidate meets 800 ms, it ranks all eligible nodes by the slower core response, then by total core latency, Clash delay, and node name. Explicit force optimization always scans the full set.
- External manual selection during a sweep cancels automatic writing. Detection and switching remain serialized.
- A disconnected local network pauses candidate discovery. Pausing requests cancellation after in-flight requests finish; unfinished switching still receives safe rollback handling.

Settings expose the recheck interval; automatic optimization when the current node exceeds 800 ms remains enabled. Old preferences, history, and statistics remain readable.

## Diagnostics and operation

“Recheck current node” runs detection. Reporting “ChatGPT unavailable” requests detection and does not directly switch or roll back. “Restore previous node” is an explicit manual action with precheck, immediate post-switch verification, and protection for external selection changes.

The candidate table can manually refresh the top 10 candidate nodes, showing Clash delay, exit country, and per-website latency. It is read-only diagnostics: refreshing it does not switch the active group. Optional-service rankings and historical recommendations do not control recovery eligibility. The raw probe evidence remains available for diagnosis; an optional-service failure is not a core recovery gate.

Logs describe failure counts, confirmation, rejected candidates, switching, verification, rollback, and cooldown transitions. Runtime statistics show resource use and limited probe-body measurements, not complete traffic bills or website availability. Closing the details window leaves the tray monitor running; use Exit to stop it. A second launch opens the existing instance.

Historical records from former standby, public-incident, failure-count, and observation policies may remain on disk. They do not affect the current all-node ranking; the continuous optimization rules above describe current behavior.

The built-in logon supervisor restarts abnormal exits up to three times and detects workers with no progress for three minutes. Waiting and paused states report liveness, and intentional exits are not restarted. The coordinator logs unexpected exceptions and retries after five seconds. Installation resolves the physical user directory and supports migration of older startup instances redirected into `LocalCache`.

Supports `proxies`, `proxy-providers`, and mixed subscriptions without requiring region-name conventions. Does not modify Clash configuration or subscription files, inspect browser history, or upload telemetry.

## Requirements

- Windows 10 or Windows 11
- Clash Verge Rev with a Mihomo core; the primary development environment uses Clash Verge Rev 2.4.5
- An existing working subscription; this project does not provide proxy nodes

## Quick start

1. Download and extract the complete package from [GitHub Releases](https://github.com/tejamanilannya593-arch/clash-compat-monitor/releases/latest).
2. Keep your existing main selector working and run `Diagnose.cmd`. Optionally enable `clash/enhancement.js` for isolated HTTP probing.
3. Run `Install.cmd`, choose the services you use, and leave the monitor running in the Windows tray.

The monitor uses an existing ordinary selector, such as `🚀 节点选择`; a dedicated “unified stable node” group is not required. Leave the selector setting empty for automatic discovery or enter an existing group name. Without isolated probing, each sweep temporarily selects nodes in order to obtain fresh measurements, then writes the first-ranked verified node. Manually refreshing the candidate diagnostics does not switch the active selection. The installer does not edit subscriptions or restart Clash. See the [Chinese quick-start guide](QUICKSTART.md) for setup and troubleshooting.

The optional enhancement preserves ordinary group types, candidates, providers, and filters. It prefers an existing `🚀 节点选择`, then `PROXY`/`Proxy`/`代理`, then the selector named by `MATCH`/`FINAL`, or the first non-internal selector. It adds a hidden `🧪 兼容性探测` group and a localhost-only HTTP listener on port 7896 for isolated per-website measurements. No ordinary selector is created if none exists. Old dedicated-group references are migrated without dangling references or cycles; existing explicit routing takes precedence. You do not need to select the hidden probe group.

## Evidence boundaries

Automatic optimization uses website latency and actual exit region only. Manual candidate diagnostics can still show application-entry and authentication evidence, but a challenge page alone is not full compatibility. The monitor does not operate your browser account and cannot prove actual model generation or an ongoing conversation.

Anonymous probes and displayed HTTP latency cannot measure model-generation latency or guarantee future account behavior. Browser proof concerns websites, not API-key calls. Sign-in prompts and challenges alone are incomplete evidence. Existing account-verification and quality history is retained for diagnostics and does not influence the fresh all-node ranking.

A stable node identity uses an installation-local key, the subscription source, and material connection parameters. Renames can retain trusted history, while a same-name connection replacement starts cold. Raw servers, ports, UUIDs, passwords, and SNI are not written to monitor logs or state; a raw exit IP is never persisted, logged, or displayed. Region eligibility uses the actual exit country rather than a node-name label, checking the ChatGPT official supported-region requirement and Gemini eligibility. Unresolved identity must not inherit an unrelated node's persistent trust; recovery requires fresh measurements.

Steam game traffic and downloads continue to follow the user's existing Clash direct-routing rules.

## Privacy and security

The application runs locally and uploads no telemetry. Do not post subscription URLs, controller secrets, full Clash configuration files, full logs, or `experience.json` in public issues. Use the repository's private Security Advisory flow for vulnerabilities; see [SECURITY.md](SECURITY.md).

## Build and test

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
node .\clash\enhancement.test.js
node .\tests\Enhancement.Tests.js
node .\browser-extension\tests\run.js
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Release.Tests.ps1
```

Contributions and privacy-safe compatibility reports are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) and the structured issue forms.
