# Clash Compatibility Monitor

> A stability-first Clash/Mihomo node guardian for Windows

[![CI](https://github.com/tejamanilannya593-arch/clash-compat-monitor/actions/workflows/ci.yml/badge.svg)](https://github.com/tejamanilannya593-arch/clash-compat-monitor/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/tejamanilannya593-arch/clash-compat-monitor)](https://github.com/tejamanilannya593-arch/clash-compat-monitor/releases/latest)
[![License](https://img.shields.io/github/license/tejamanilannya593-arch/clash-compat-monitor)](LICENSE)

[Download the latest release](https://github.com/tejamanilannya593-arch/clash-compat-monitor/releases/latest) · [中文](README.md)

Clash Compatibility Monitor keeps a working node in place, performs conservative failover after confirmed failures, and avoids blaming a node when the same service fails across the current node and two recently verified standbys.

## Why use it

- Discovers actual leaf nodes from `proxies`, `proxy-providers`, and mixed subscription layouts without relying on region names.
- Checks selected services including ChatGPT, Gemini, Google, GitHub, Steam, Discord, Spotify, and Epic.
- The development branch adds an optional Z-Library web entrance check at the user-specified `https://zh.z-library.sk/`. It is off by default and measures only HTTP reachability and latency, not sign-in, search, or downloads. It does not discover alternate mirrors.
- Requires five observations spanning at least 30 minutes with a success rate of 95% or higher before a performance-only switch.
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

The monitor separates entry reachability, login-chain evidence, and account verification. For ChatGPT and Gemini it checks both the application entry and official authentication infrastructure; a challenge page alone is not full compatibility. In v0.6.2, an optional Chrome/Edge browser companion can send a short random-challenge message on the already signed-in official website, check the newly generated reply, and close its test tab. The test conversation remains in your account and is not deleted automatically. Loading the unpacked extension is a one-time manual step; see [browser companion setup](browser-extension/README.md). Background conversation checks are off by default and require explicit consent. You can also run one check without persistent consent. The result is bound to the node and encrypted exit fingerprint for up to 30 days. The extension does not request Cookie or browsing-history permissions, read account identifiers or existing conversations, or store the test prompt/reply body. It does inspect the new reply to verify the challenge.

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
