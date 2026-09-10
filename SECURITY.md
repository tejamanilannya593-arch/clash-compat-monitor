# Security Policy

## Supported versions

Security fixes are provided for the latest `0.6.x` release. Older development builds may be referenced when diagnosing a regression, but they do not receive separate security updates.

## Reporting a vulnerability

Please use the repository's **Security** tab and choose **Report a vulnerability** to open a private GitHub Security Advisory. Do not open a public issue for a suspected vulnerability.

Include the affected version, impact, reproduction conditions, and the smallest sanitized evidence needed to confirm the problem. Please do not include subscription URLs, proxy credentials, Mihomo controller secrets, full Clash configuration files, full logs, or `experience.json`.

The maintainer will acknowledge a complete report through the Security Advisory, investigate it privately, and coordinate disclosure after a fix is available. Reports about third-party proxy providers, airport services, websites, or Clash/Mihomo itself should be sent to their respective maintainers.

## Security boundaries

Clash Compatibility Monitor selects an existing leaf node in configured proxy groups. It does not provide proxy nodes, modify subscription contents, upload telemetry, or claim to validate account-level ChatGPT, Gemini, or other signed-in functionality.
