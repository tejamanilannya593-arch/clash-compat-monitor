$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$required = @(
    'README.md','README.en.md','QUICKSTART.md','LICENSE','Install.cmd','Diagnose.cmd','package-release.ps1',
    'SECURITY.md','CONTRIBUTING.md','CODE_OF_CONDUCT.md',
    'scripts\install.ps1','scripts\upgrade.ps1','scripts\uninstall.ps1','scripts\diagnose.ps1',
    'clash\enhancement.js','assets\ClashCompatibilityMonitor.manifest','assets\ClashCompatibilityMonitor.ico',
    'assets\social-preview.png','docs\images\service-incident-flow.png','docs\release-notes\v0.7.0-preview.35.md'
)
foreach ($relative in $required) {
    if (!(Test-Path -LiteralPath (Join-Path $root $relative) -PathType Leaf)) { throw "Missing release file: $relative" }
}

$build = Get-Content -LiteralPath (Join-Path $root 'build.ps1') -Raw
$package = Get-Content -LiteralPath (Join-Path $root 'package-release.ps1') -Raw
$install = Get-Content -LiteralPath (Join-Path $root 'scripts\install.ps1') -Raw
$worker = Get-Content -LiteralPath (Join-Path $root 'src\MonitorWorker.cs') -Raw
$recovery = Get-Content -LiteralPath (Join-Path $root 'src\RecoveryMonitoring.cs') -Raw
$mihomo = Get-Content -LiteralPath (Join-Path $root 'src\MihomoPipeClient.cs') -Raw
$program = Get-Content -LiteralPath (Join-Path $root 'src\Program.cs') -Raw
$coordinator = Get-Content -LiteralPath (Join-Path $root 'src\MonitorCoordinator.cs') -Raw
$candidateForm = Get-Content -LiteralPath (Join-Path $root 'src\CandidateLatencyForm.cs') -Raw
$manualOptimization = Get-Content -LiteralPath (Join-Path $root 'src\ManualOptimization.cs') -Raw
$continuousOptimization = Get-Content -LiteralPath (Join-Path $root 'src\ContinuousOptimization.cs') -Raw
$statusReport = Get-Content -LiteralPath (Join-Path $root 'src\StatusReport.cs') -Raw
$uninstall = Get-Content -LiteralPath (Join-Path $root 'scripts\uninstall.ps1') -Raw
$readme = Get-Content -LiteralPath (Join-Path $root 'README.md') -Raw -Encoding UTF8
$currentDocs = $readme + (Get-Content -LiteralPath (Join-Path $root 'README.en.md') -Raw -Encoding UTF8) +
    (Get-Content -LiteralPath (Join-Path $root 'QUICKSTART.md') -Raw -Encoding UTF8)

if ($build.Contains('BrowserHost') -or $build.Contains('browser-extension') -or
    $package.Contains('BrowserHost') -or $package.Contains('browser-extension')) { throw 'Release still packages the removed browser companion.' }
if (!$package.Contains('ClashCompatibilityMonitor-v0.7.0-preview.35') -or
    !$program.Contains('Version = "0.7.0-preview.35"')) { throw 'Release version is inconsistent.' }
if ($worker.Contains('EnsureIpv4Compatibility') -or $mihomo.Contains('PUT", "/configs')) { throw 'Monitor may rewrite the complete Mihomo configuration.' }
if ($install -match '(?i)Set-Content[^\r\n]*(profiles\.yaml|clash-verge\.yaml)' -or
    $install -match '(?i)Copy-Item[^\r\n]+-Destination[^\r\n]+\$clashRoot') { throw 'Installer may write a protected Clash file.' }
if (!$install.Contains("Version='0.7.0-preview.35'") -or !$install.Contains('LegacyBrowserCompanionRemoved=$true') -or
    !$install.Contains("diagnosis.Status -ne 'CONFIG_READY'")) { throw 'Installer upgrade safeguards are incomplete.' }
if (!$package.Contains('$checksumFile = $zip + ''.sha256''') -or !$package.Contains('Encoding ASCII')) { throw 'SHA-256 sidecar is missing.' }
if (!$program.Contains('Application.ThreadException') -or
    !$program.Contains('AppDomain.CurrentDomain.UnhandledException') -or
    !$program.Contains('process started version=') -or
    !$program.Contains('process stopped exit_code=')) { throw 'Process lifetime diagnostics are incomplete.' }
if (!$coordinator.Contains('LoopSafely') -or !$coordinator.Contains('NotifySafely')) {
    throw 'Coordinator exception isolation is incomplete.'
}
if ($worker.Contains('ApplyAutomaticSwitch(')) {
    throw 'A legacy proactive switching entry remains.'
}
if (!$candidateForm.Contains('this.requestOptimization()') -or
    !$manualOptimization.Contains('CoreWebsitePolicy.Required') -or
    !$manualOptimization.Contains('RollbackRecovery(recovery)')) {
    throw 'The user-authorized verified optimization action is incomplete.'
}
if (!$program.Contains('ContinuousOptimization = true') -or
    !$continuousOptimization.Contains('MeasureLiveDelays(forced ? candidates') -or
    !$continuousOptimization.Contains('AiRegionPolicy.SupportsBoth') -or
    !$continuousOptimization.Contains('CoreMaximumMilliseconds') -or
    !$continuousOptimization.Contains('PreferredCoreLatencyMilliseconds = 800') -or
    !$continuousOptimization.Contains('TryFastContinuousSwitch') -or
    !$continuousOptimization.Contains('RollbackRecovery(recovery)')) {
    throw 'Conditional 800 ms optimization, region gating, ranking, or rollback is incomplete.'
}
if (!$candidateForm.Contains('AddColumn("decision"')) { throw 'Candidate rejection diagnostics are missing.' }
if (!$statusReport.Contains('TryWriteLatest') -or !$recovery.Contains('status update skipped')) {
    throw 'Non-critical status publication is not isolated from monitoring.'
}
if (!$recovery.Contains('RecoverConfirmed') -or !$recovery.Contains('RollbackRecovery') -or
    !$recovery.Contains('CooldownUntilUtc')) {
    throw 'Verified fault recovery and persistent cooldown are incomplete.'
}
if (!$program.Contains('StartupSupervision.Run') -or
    !$program.Contains('ShouldRestart(exitCode, abnormalExitCount)')) { throw 'Bounded abnormal-exit restart is incomplete.' }
if (!$program.Contains('--supervision-heartbeat=') -or
    !$program.Contains('HeartbeatExpired') -or
    !$program.Contains('supervisor heartbeat timeout') -or
    !$program.Contains('TerminateStalledWorker(child, logger)')) {
    throw 'Startup supervision must restart a live but stalled worker.'
}
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
if ($install.Contains("GetFolderPath('LocalApplicationData')") -or
    !$install.Contains("GetFolderPath('UserProfile')") -or
    !$install.Contains("Join-Path `$userProfile 'AppData\Local'")) {
    throw 'Installer must resolve the physical per-user LocalAppData path without package redirection.'
}
if (!$install.Contains('Get-InstalledMonitorProcesses | Where-Object') -or
    !$install.Contains('[IO.Path]::GetFullPath($_.ExecutablePath).Equals(')) {
    throw 'Installer must verify that the launched processes use the physical target path.'
}
if ($uninstall.Contains("GetFolderPath('LocalApplicationData')") -or
    !$uninstall.Contains("GetFolderPath('UserProfile')") -or
    !$uninstall.Contains("Join-Path `$userProfile 'AppData\Local'")) {
    throw 'Installer and uninstaller must use the same physical per-user directory.'
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
if (!$currentDocs.Contains('every 60 seconds')) { throw 'Automatic healthy-cycle interval is undocumented.' }
if (!$currentDocs.Contains('top 10 candidate nodes') -or !$currentDocs.Contains('per-website latency')) {
    throw 'Candidate website latency ranking is undocumented.'
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
if ($version.FileVersion -ne '0.7.0.0' -or $version.ProductVersion -ne '0.7.0-preview.35') { throw 'Windows version metadata is incorrect.' }

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
} finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixture)
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (!$resolvedFixture.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path.' }
    Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
}

Write-Output 'PASS release files, version, installer boundaries and diagnostics'
