# Clash Compatibility Monitor

> A stability-first Clash/Mihomo node guardian for Windows

[![CI](https://github.com/tejamanilannya593-arch/clash-compat-monitor/actions/workflows/ci.yml/badge.svg)](https://github.com/tejamanilannya593-arch/clash-compat-monitor/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/tejamanilannya593-arch/clash-compat-monitor)](https://github.com/tejamanilannya593-arch/clash-compat-monitor/releases/latest)
[![License](https://img.shields.io/github/license/tejamanilannya593-arch/clash-compat-monitor)](LICENSE)

[Download the latest release](https://github.com/tejamanilannya593-arch/clash-compat-monitor/releases/latest) · [中文](README.md)

Clash Compatibility Monitor recovers from confirmed failures, can select a faster node from measured per-website latency rankings, and avoids blaming a node when the same service fails across the current node and two recently verified standbys.

## Why use it

- Discovers actual leaf nodes from `proxies`, `proxy-providers`, and mixed subscription layouts without relying on region names.
- Always checks ChatGPT, Steam API, and Google together, with optional GitHub, Steam Store/Community, Discord, Spotify, Epic, and Z-Library web checks.
- v0.7.0-preview.27 closes a discovery gap in mandatory performance switching. Even during the post-switch hold or budget stabilization state, scheduled optimization still measures candidates every three minutes. A candidate that passes region and all selected-service checks and is at least 200 ms faster switches immediately. A fully checked candidate improving by 199 ms or less is retained only as a diagnostic while the search continues, preventing small fluctuations from causing churn. Observation and rollback safeguards remain unchanged.
- v0.7.0-preview.23 separates “Recheck current node” from “Optimize and switch now.” An explicit manual optimization bypasses the automatic stability hold, scan interval, and switch budget, then switches only when a candidate is faster and passes the actual-exit region gate plus a fresh full recheck of ChatGPT, Steam API, Google, and every selected service. Scheduled optimization keeps its stability and budget protection and now shows the hold deadline. The candidate table pins each rejection reason beside the node name; refreshing the top 10 remains measurement-only.
- v0.7.0-preview.22 fixes a sign-in startup failure that could show “Cannot find script file.” The Startup shortcut now launches the installed executable's built-in supervisor directly instead of depending on `launcher.vbs`. Abnormal exits are still restarted up to three times, while an intentional exit remains final.
- v0.7.0-preview.21 treats a ChatGPT outage as the highest-priority recovery event. It skips a second wait on the failed current node, measures Clash delay for all alternatives concurrently, then concurrently measures the ChatGPT URL on every Clash-reachable candidate. Candidates receive a full recheck with a two-second per-probe budget in ChatGPT-latency order, and the first one passing actual-exit region, ChatGPT, Steam API, Google, and every selected service is switched immediately. Full rechecks run in batches of at most 12; remaining node identities are persisted and the next batch starts about 100 ms later. Only a complete pass with no target waits three seconds before restarting. Recovery is never blocked by the optimization switch budget. Delay checks run in continuous batches of at most 64; every candidate is attempted, while shorter per-request pipe limits and malformed-response handling keep one bad request from aborting recovery.
- v0.7.0-preview.20 records full exception stacks plus process start/stop markers, isolates UI and activation callbacks, and restarts the coordinator loop after an unexpected failure. Its logon launcher does not restart a normal tray exit, but retries an abnormal process exit at most three times with a five-second delay. The candidate table now explains fixed-site failures, region rejection, full-recheck rejection, insufficient improvement, switch-budget blocking, and successful selection. Confirmed hard failures remain able to recover when the optimization budget is exhausted; latency-only and proactive switches remain budgeted.
- The local v0.7.0-preview.19 build coordinates monitoring, failure recovery, ranked selection, post-switch observation, and rollback through one persisted decision state machine. If any of the three mandatory websites fails while an Ethernet or Wi-Fi adapter remains connected, automatic recovery keeps searching every 30 seconds. Each round checks at most eight candidates and persists its position so later rounds reach the rest; the three lowest-latency eligible candidates are ranked by measured website response. A shared website incident pauses node-fault attribution, not the search for a working three-site candidate. A replacement must connect to ChatGPT, Steam API, and Google, pass the ChatGPT official supported-region requirement, and pass the full selected-service recheck. With automatic optimization enabled, a three-minute opportunity scan measures all leaf-node Clash delays and uses them only to choose the top 10 candidate nodes to probe. Final ranking uses per-website latency: ChatGPT first, then the slower of Steam API and Google when ChatGPT measurements tie. ChatGPT-unavailable nodes rank after reachable nodes. Clash delay does not break final ranking ties. Saving preferences cancels an in-flight cycle and starts a new one with the updated services; stale results cannot switch or overwrite the new status. The rolling budget permits at most two automatic switches in ten minutes and four in thirty minutes. Every switch retains observation and rollback. Healthy connections are checked every 60 seconds; failures and observation use 30-second intervals. Manual and scheduled checks use the same safety gates. The program does not modify Clash configuration or subscription files.
- v0.7.0-preview.19 derives an irreversible stable node identity from the active subscription source, protocol, server, port, and material connection parameters using an installation-local key. Renames retain trusted history, while a same-name connection replacement starts cold. An unresolved node may still take over after fresh verification of a confirmed hard failure, but it cannot create or reuse persistent quality, region, standby, rollback, or account-verification trust. Raw servers, ports, UUIDs, passwords, and SNI values are never written to monitor logs or state.
- The details window displays the top 10 candidate nodes initially chosen by current Clash delay, with per-website latency, exit country, Clash delay, and the reason each row was not selected. The reason column is pinned beside the node name. The displayed and automatic selection order is based on ChatGPT response first and Steam API/Google response second; Clash delay is not the final ranking criterion. Scheduled optimization refreshes the same ranking and tries faster qualified nodes in that order. Refreshing the table only measures; “Optimize and switch now” performs the guarded full recheck and may switch.
- A public-service incident now requires the same definite failure across three distinct real exits: the current node plus two alternatives. When ASN evidence is complete, at least two ASNs are required; otherwise at least two known countries are required. ASN evidence is cached for 60 minutes by a keyed exit fingerprint. The monitor never persists, logs, or displays a raw exit IP.
- The raw probe evidence is kept separate from node-health attribution. An incident circuit leaves the real failure visible, but marks only that service observation as excluded from node health, quality, standby, and stability history.
- Requires a fresh full selected-service recheck before a performance-only switch.
- The ranked automatic selection advances to the next candidate when a full recheck fails; it keeps the current node when no measured candidate is better.
- Keeps two recent standbys, observes every automatic switch, and can safely roll back.
- Does not modify Clash configuration files, take ownership of subscriptions, inspect browser history, or upload telemetry.

## Requirements

- Windows 10 or Windows 11
- Clash Verge Rev with a Mihomo core; the primary development environment uses Clash Verge Rev 2.4.5
- An existing working subscription; this project does not provide proxy nodes

## Quick start

1. Download and extract the complete package from [GitHub Releases](https://github.com/tejamanilannya593-arch/clash-compat-monitor/releases/latest).
2. Enable `clash/enhancement.js` as a Clash Verge Rev JavaScript enhancement, apply the configuration, and run `Diagnose.cmd`.
3. Run `Install.cmd`, choose the services you use, and leave the monitor running in the Windows tray.

The installer only selects nodes in the generated proxy group. It does not edit subscription files or restart Clash. See the [Chinese quick-start guide](QUICKSTART.md) for detailed setup and troubleshooting.

## Evidence boundaries

The monitor separates entry reachability from login-chain network evidence. For ChatGPT it checks the application entry and official authentication infrastructure; a challenge page alone is not full compatibility. It does not operate your browser account and cannot prove actual model generation or an ongoing conversation.

Anonymous probes and displayed HTTP latency still cannot measure actual model-generation latency or guarantee future account behavior. Browser proof checks the website, not API-key calls. Sign-in prompts, CAPTCHAs, security challenges, and unsupported page layouts are reported separately and are not attributed to the node. Automatic retries after a failed conversation have a six-hour cooldown; a user-triggered retry does not. Performance optimization of an otherwise healthy node is off by default. If explicitly enabled, a performance-only AI switch requires strict probe evidence, fresh conversation proof for the selected AI services, five history observations spanning 30 minutes at 95% success or better, a five-sample median at or below 800 ms, no sample or selected-service response above 1500 ms, and jitter at or below 150 ms.

Steam game traffic and downloads continue to follow the user's existing Clash direct-routing rules.

## Privacy and security

The application runs locally and uploads no telemetry. Do not post subscription URLs, controller secrets, full Clash configuration files, full logs, or `experience.json` in public issues. Use the repository's private Security Advisory flow for vulnerabilities; see [SECURITY.md](SECURITY.md).

## Build and test

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
node .\clash\enhancement.test.js
node .\browser-extension\tests\run.js
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Release.Tests.ps1
```

Contributions and privacy-safe compatibility reports are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) and the structured issue forms.
