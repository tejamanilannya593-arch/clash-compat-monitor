# Clash Compatibility Monitor

> A stability-first Clash/Mihomo node guardian for Windows

[![CI](https://github.com/tejamanilannya593-arch/clash-compat-monitor/actions/workflows/ci.yml/badge.svg)](https://github.com/tejamanilannya593-arch/clash-compat-monitor/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/tejamanilannya593-arch/clash-compat-monitor)](https://github.com/tejamanilannya593-arch/clash-compat-monitor/releases/latest)
[![License](https://img.shields.io/github/license/tejamanilannya593-arch/clash-compat-monitor)](LICENSE)

[Download the latest release](https://github.com/tejamanilannya593-arch/clash-compat-monitor/releases/latest) · [中文](README.md)

Clash Compatibility Monitor keeps a working node in place, performs conservative failover after confirmed failures, and avoids blaming a node when the same service fails across the current node and two recently verified standbys.

## Why use it

- Discovers actual leaf nodes from `proxies`, `proxy-providers`, and mixed subscription layouts without relying on region names.
- Checks selected services including ChatGPT, Gemini, Google, GitHub, Steam, Discord, Spotify, Epic, and optional JMComic web reachability.
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

HTTP reachability cannot prove that a signed-in ChatGPT conversation, Gemini generation, account state, or a complete web application flow works. Challenge and login pages are shown as basic reachability rather than full compatibility. JMComic probing is opt-in and currently targets `https://18comic.vip/`, whose domain may change independently of this project.

Steam game traffic and downloads continue to follow the user's existing Clash direct-routing rules.

## Privacy and security

The application runs locally and uploads no telemetry. Do not post subscription URLs, controller secrets, full Clash configuration files, full logs, or `experience.json` in public issues. Use the repository's private Security Advisory flow for vulnerabilities; see [SECURITY.md](SECURITY.md).

## Build and test

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
node .\clash\enhancement.test.js
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Release.Tests.ps1
```

Contributions and privacy-safe compatibility reports are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) and the structured issue forms.
