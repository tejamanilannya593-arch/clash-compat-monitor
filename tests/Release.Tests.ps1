$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$required = @(
    'README.md','README.en.md','QUICKSTART.md','LICENSE','Install.cmd','Diagnose.cmd','package-release.ps1',
    'SECURITY.md','CONTRIBUTING.md','CODE_OF_CONDUCT.md',
    'scripts\install.ps1','scripts\upgrade.ps1','scripts\uninstall.ps1','scripts\diagnose.ps1',
    'clash\enhancement.js','assets\ClashCompatibilityMonitor.manifest','assets\ClashCompatibilityMonitor.ico',
    'assets\social-preview.png','docs\images\service-incident-flow.png','docs\release-notes\v0.7.0-preview.27.md'
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
$coordinator = Get-Content -LiteralPath (Join-Path $root 'src\MonitorCoordinator.cs') -Raw
$candidateForm = Get-Content -LiteralPath (Join-Path $root 'src\CandidateLatencyForm.cs') -Raw
$statusReport = Get-Content -LiteralPath (Join-Path $root 'src\StatusReport.cs') -Raw
$uninstall = Get-Content -LiteralPath (Join-Path $root 'scripts\uninstall.ps1') -Raw
$readme = Get-Content -LiteralPath (Join-Path $root 'README.md') -Raw -Encoding UTF8
$currentDocs = $readme + (Get-Content -LiteralPath (Join-Path $root 'README.en.md') -Raw -Encoding UTF8) +
    (Get-Content -LiteralPath (Join-Path $root 'QUICKSTART.md') -Raw -Encoding UTF8)

if ($build.Contains('BrowserHost') -or $build.Contains('browser-extension') -or
    $package.Contains('BrowserHost') -or $package.Contains('browser-extension')) { throw 'Release still packages the removed browser companion.' }
if (!$package.Contains('ClashCompatibilityMonitor-v0.7.0-preview.27') -or
    !$program.Contains('Version = "0.7.0-preview.27"')) { throw 'Release version is inconsistent.' }
if ($worker.Contains('EnsureIpv4Compatibility') -or $mihomo.Contains('PUT", "/configs')) { throw 'Monitor may rewrite the complete Mihomo configuration.' }
if ($install -match '(?i)Set-Content[^\r\n]*(profiles\.yaml|clash-verge\.yaml)' -or
    $install -match '(?i)Copy-Item[^\r\n]+-Destination[^\r\n]+\$clashRoot') { throw 'Installer may write a protected Clash file.' }
if (!$install.Contains("Version='0.7.0-preview.27'") -or !$install.Contains('LegacyBrowserCompanionRemoved=$true') -or
    !$install.Contains("diagnosis.Status -ne 'CONFIG_READY'")) { throw 'Installer upgrade safeguards are incomplete.' }
if (!$package.Contains('$checksumFile = $zip + ''.sha256''') -or !$package.Contains('Encoding ASCII')) { throw 'SHA-256 sidecar is missing.' }
if (!$program.Contains('Application.ThreadException') -or
    !$program.Contains('AppDomain.CurrentDomain.UnhandledException') -or
    !$program.Contains('process started version=') -or
    !$program.Contains('process stopped exit_code=')) { throw 'Process lifetime diagnostics are incomplete.' }
if (!$coordinator.Contains('LoopSafely') -or !$coordinator.Contains('NotifySafely')) {
    throw 'Coordinator exception isolation is incomplete.'
}
if (!$coordinator.Contains('RequestOptimization') -or
    !$coordinator.Contains('MonitorCycleTrigger.ManualOptimization')) {
    throw 'Explicit manual optimization routing is incomplete.'
}
if (!$candidateForm.Contains('AddColumn("decision"')) { throw 'Candidate rejection diagnostics are missing.' }
if (!$statusReport.Contains('TryWriteLatest') -or !$worker.Contains('status update skipped error=')) {
    throw 'Non-critical status publication is not isolated from monitoring.'
}
if (!$worker.Contains('!budget.Allowed && !improvement.MandatorySwitch') -or
    !$worker.Contains('!budget.Allowed && !mandatoryImprovement')) {
    throw 'Mandatory 200 ms performance switching is incomplete.'
}
if (!$program.Contains('StartupSupervision.Run') -or
    !$program.Contains('ShouldRestart(exitCode, abnormalExitCount)')) { throw 'Bounded abnormal-exit restart is incomplete.' }
if (!$install.Contains('$shortcut.TargetPath = $target') -or
    !$install.Contains("`$shortcut.Arguments = '--startup-supervisor'") -or
    !$install.Contains("Start-Process -FilePath `$target") -or
    $install.Contains('$shortcut.TargetPath = $wscript')) {
    throw 'Startup shortcut must launch the installed executable supervisor directly.'
}
if (!$install.Contains('Test-IsInstalledMonitorProcess $_') -or
    !$install.Contains('[IO.Path]::GetFullPath($target), [StringComparison]::OrdinalIgnoreCase')) {
    throw 'Installer must stop and verify package-redirected monitor processes.'
}
if (!$install.Contains('function Test-IsInstalledMonitorProcess') -or
    !$install.Contains('function Get-InstalledMonitorProcesses') -or
    !$install.Contains('[String]::IsNullOrWhiteSpace($_.ExecutablePath)') -or
    !$install.Contains('Close any elevated ClashCompatibilityMonitor window')) {
    throw 'Installer must reject an unidentifiable elevated monitor without killing by name alone.'
}
if ($install.Contains("return [String]::Equals(`$Process.Name, 'ClashCompatibilityMonitor.exe'")) {
    throw 'Installer must not claim ownership of a path-hidden process by name alone.'
}
$hiddenProcessPreflight = $install.IndexOf('$null = @(Get-InstalledMonitorProcesses)', [StringComparison]::Ordinal)
$firstLauncherStop = $install.IndexOf("`nStop-InstalledLauncher", [StringComparison]::Ordinal)
if ($hiddenProcessPreflight -lt 0 -or $firstLauncherStop -lt 0 -or $hiddenProcessPreflight -gt $firstLauncherStop) {
    throw 'Installer must reject path-hidden processes before stopping the legacy launcher.'
}
if ($package.Contains("Join-Path `$root 'launcher.vbs'")) { throw 'Release package still depends on the removable script launcher.' }
if ($currentDocs -match '(?i)JMComic|18comic') { throw 'Current documentation advertises a retired probe.' }
if (!$currentDocs.Contains('ChatGPT official supported-region requirement')) { throw 'ChatGPT region requirement is undocumented.' }
if (!$currentDocs.Contains('three lowest-latency eligible candidates')) { throw 'Fast candidate limit is undocumented.' }
if (!$currentDocs.Contains('every 60 seconds')) { throw 'Automatic healthy-cycle interval is undocumented.' }
if (!$currentDocs.Contains('three-minute opportunity scan') -or
    !$currentDocs.Contains('ranked automatic selection') -or
    !$currentDocs.Contains('full selected-service recheck')) {
    throw 'Automatic ranked selection is undocumented.'
}
if (!$currentDocs.Contains('bypasses the automatic stability hold, scan interval, and switch budget')) {
    throw 'Manual immediate optimization behavior is undocumented.'
}
if (!$currentDocs.Contains('top 10 candidate nodes') -or !$currentDocs.Contains('per-website latency')) {
    throw 'Candidate website latency ranking is undocumented.'
}
if (!$currentDocs.Contains('three distinct real exits') -or !$currentDocs.Contains('at least two ASNs')) {
    throw 'Independent-exit incident consensus is undocumented.'
}
if (!$currentDocs.Contains('raw probe evidence') -or !$currentDocs.Contains('raw exit IP')) {
    throw 'Raw evidence separation or exit-IP privacy is undocumented.'
}
if (!$currentDocs.Contains('stable node identity') -or !$currentDocs.Contains('same-name connection replacement') -or
    !$currentDocs.Contains('Raw servers, ports, UUIDs, passwords, and SNI')) {
    throw 'Stable node identity and migration privacy are undocumented.'
}

$exe = Join-Path $root 'bin\ClashCompatibilityMonitor.exe'
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Main executable was not built.' }
$version = (Get-Item -LiteralPath $exe).VersionInfo
if ($version.FileVersion -ne '0.7.0.0' -or $version.ProductVersion -ne '0.7.0-preview.27') { throw 'Windows version metadata is incorrect.' }

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
