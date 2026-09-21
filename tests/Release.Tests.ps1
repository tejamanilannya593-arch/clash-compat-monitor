$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$required = @(
    'README.md','README.en.md','QUICKSTART.md','LICENSE','Install.cmd','Diagnose.cmd','package-release.ps1',
    'SECURITY.md','CONTRIBUTING.md','CODE_OF_CONDUCT.md',
    'scripts\install.ps1','scripts\upgrade.ps1','scripts\uninstall.ps1','scripts\diagnose.ps1',
    'clash\enhancement.js','assets\ClashCompatibilityMonitor.manifest','assets\ClashCompatibilityMonitor.ico',
    'assets\social-preview.png','docs\images\service-incident-flow.png','docs\release-notes\v0.7.0-preview.10.md'
)
foreach ($relative in $required) {
    if (!(Test-Path -LiteralPath (Join-Path $root $relative) -PathType Leaf)) { throw "Missing release file: $relative" }
}

$build = Get-Content -LiteralPath (Join-Path $root 'build.ps1') -Raw
$package = Get-Content -LiteralPath (Join-Path $root 'package-release.ps1') -Raw
$install = Get-Content -LiteralPath (Join-Path $root 'scripts\install.ps1') -Raw
$worker = Get-Content -LiteralPath (Join-Path $root 'src\MonitorWorker.cs') -Raw
$mihomo = Get-Content -LiteralPath (Join-Path $root 'src\MihomoPipeClient.cs') -Raw
$program = Get-Content -LiteralPath (Join-Path $root 'src\Program.cs') -Raw
$readme = Get-Content -LiteralPath (Join-Path $root 'README.md') -Raw -Encoding UTF8
$currentDocs = $readme + (Get-Content -LiteralPath (Join-Path $root 'README.en.md') -Raw -Encoding UTF8) +
    (Get-Content -LiteralPath (Join-Path $root 'QUICKSTART.md') -Raw -Encoding UTF8)

if ($build.Contains('BrowserHost') -or $build.Contains('browser-extension') -or
    $package.Contains('BrowserHost') -or $package.Contains('browser-extension')) { throw 'Release still packages the removed browser companion.' }
if (!$package.Contains('ClashCompatibilityMonitor-v0.7.0-preview.10') -or
    !$program.Contains('Version = "0.7.0-preview.10"')) { throw 'Release version is inconsistent.' }
if ($worker.Contains('EnsureIpv4Compatibility') -or $mihomo.Contains('PUT", "/configs')) { throw 'Monitor may rewrite the complete Mihomo configuration.' }
if ($install -match '(?i)Set-Content[^\r\n]*(profiles\.yaml|clash-verge\.yaml)' -or
    $install -match '(?i)Copy-Item[^\r\n]+-Destination[^\r\n]+\$clashRoot') { throw 'Installer may write a protected Clash file.' }
if (!$install.Contains("Version='0.7.0-preview.10'") -or !$install.Contains('LegacyBrowserCompanionRemoved=$true') -or
    !$install.Contains("diagnosis.Status -ne 'CONFIG_READY'")) { throw 'Installer upgrade safeguards are incomplete.' }
if (!$package.Contains('$checksumFile = $zip + ''.sha256''') -or !$package.Contains('Encoding ASCII')) { throw 'SHA-256 sidecar is missing.' }
if ($currentDocs -match '(?i)JMComic|18comic') { throw 'Current documentation advertises a retired probe.' }
if (!$currentDocs.Contains('ChatGPT and Gemini official supported-region intersection')) { throw 'Official region intersection is undocumented.' }
if (!$currentDocs.Contains('three lowest-latency eligible candidates')) { throw 'Fast candidate limit is undocumented.' }
if (!$currentDocs.Contains('every 60 seconds')) { throw 'Automatic healthy-cycle interval is undocumented.' }
if (!$currentDocs.Contains('three-minute opportunity scan') -or
    !$currentDocs.Contains('30-second confirmation') -or
    !$currentDocs.Contains('five-minute objective')) { throw 'Automatic opportunity timing is undocumented.' }
if (!$currentDocs.Contains('same safety gates')) { throw 'Manual and automatic safety parity is undocumented.' }
if (!$currentDocs.Contains('three distinct real exits') -or !$currentDocs.Contains('at least two ASNs')) {
    throw 'Independent-exit incident consensus is undocumented.'
}
if (!$currentDocs.Contains('raw probe evidence') -or !$currentDocs.Contains('raw exit IP')) {
    throw 'Raw evidence separation or exit-IP privacy is undocumented.'
}

$exe = Join-Path $root 'bin\ClashCompatibilityMonitor.exe'
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Main executable was not built.' }
$version = (Get-Item -LiteralPath $exe).VersionInfo
if ($version.FileVersion -ne '0.7.0.0' -or $version.ProductVersion -ne '0.7.0-preview.10') { throw 'Windows version metadata is incorrect.' }

$manifest = Join-Path $root 'assets\ClashCompatibilityMonitor.manifest'
if (!(Select-String -LiteralPath $manifest -SimpleMatch 'PerMonitorV2' -Quiet) -or
    !$build.Contains('/win32manifest:') -or !$build.Contains('/win32icon:')) { throw 'Windows build metadata is incomplete.' }

foreach ($script in Get-ChildItem (Join-Path $root 'scripts') -Filter '*.ps1' -File) {
    $tokens = $null; $errors = $null
    [void][Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$errors)
    if ($errors.Count -ne 0) { throw "PowerShell parse failure: $($script.Name)" }
}

$published = @($required | ForEach-Object { Join-Path $root $_ }) + @(Get-ChildItem (Join-Path $root 'src') -File | ForEach-Object FullName)
if (Select-String -LiteralPath $published -SimpleMatch ([Environment]::GetFolderPath('UserProfile')) -Quiet) { throw 'Release sources contain a machine-specific path.' }

$fixture = Join-Path ([IO.Path]::GetTempPath()) ('monitor-diagnose-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
try {
    $missing = & (Join-Path $root 'scripts\diagnose.ps1') -ClashDirectory $fixture -AsObject
    if ($missing.Status -ne 'CLASH_CONFIG_MISSING') { throw 'Diagnosis does not identify missing configuration.' }
    Set-Content -LiteralPath (Join-Path $fixture 'profiles.yaml') -Value 'profiles: []' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $fixture 'clash-verge.yaml') -Value "listeners:`n- name: compatibility-probe`n  port: 7896" -Encoding UTF8
    $ready = & (Join-Path $root 'scripts\diagnose.ps1') -ClashDirectory $fixture -AsObject
    if ($ready.Status -ne 'CONFIG_READY') { throw 'Diagnosis does not identify prepared configuration.' }
} finally { Remove-Item -LiteralPath $fixture -Recurse -Force }

Write-Output 'PASS release files, version, installer boundaries and diagnostics'
