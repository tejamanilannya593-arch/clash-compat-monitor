$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$required = @(
    'README.md', 'README.en.md', 'QUICKSTART.md', 'LICENSE', 'Install.cmd', 'Diagnose.cmd', 'package-release.ps1',
    'SECURITY.md', 'CODE_OF_CONDUCT.md',
    'scripts\install.ps1', 'scripts\upgrade.ps1', 'scripts\uninstall.ps1', 'scripts\diagnose.ps1',
    '.github\ISSUE_TEMPLATE\config.yml', '.github\ISSUE_TEMPLATE\compatibility.yml',
    '.github\ISSUE_TEMPLATE\feature.yml', '.github\dependabot.yml', '.github\workflows\codeql.yml'
)
foreach ($relative in $required) {
    $path = Join-Path $root $relative
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing release file: $relative" }
}
$packageScript = Get-Content -LiteralPath (Join-Path $root 'package-release.ps1') -Raw
$installScript = Get-Content -LiteralPath (Join-Path $root 'scripts\install.ps1') -Raw
$readme = Get-Content -LiteralPath (Join-Path $root 'README.md') -Raw -Encoding UTF8
$program = Get-Content -LiteralPath (Join-Path $root 'src\Program.cs') -Raw
$trayHost = Get-Content -LiteralPath (Join-Path $root 'src\TrayHost.cs') -Raw
$detailsForm = Get-Content -LiteralPath (Join-Path $root 'src\DetailsForm.cs') -Raw
$buildScript = Get-Content -LiteralPath (Join-Path $root 'build.ps1') -Raw
$security = if (Test-Path -LiteralPath (Join-Path $root 'SECURITY.md')) { Get-Content -LiteralPath (Join-Path $root 'SECURITY.md') -Raw -Encoding UTF8 } else { '' }
$conduct = if (Test-Path -LiteralPath (Join-Path $root 'CODE_OF_CONDUCT.md')) { Get-Content -LiteralPath (Join-Path $root 'CODE_OF_CONDUCT.md') -Raw -Encoding UTF8 } else { '' }
$manifestPath = Join-Path $root 'assets\ClashCompatibilityMonitor.manifest'
$iconPath = Join-Path $root 'assets\ClashCompatibilityMonitor.ico'
if (!$packageScript.Contains('ClashCompatibilityMonitor-v0.6.0')) { throw 'Release package version is not v0.6.0.' }
if (!$installScript.Contains("Version='0.6.0'")) { throw 'Installer status version is not v0.6.0.' }
if (!$installScript.Contains('EndsWith($monitorSuffix')) { throw 'Installer does not stop virtualized monitor paths.' }
if (!$installScript.Contains("diagnosis.Status -ne 'CONFIG_READY'")) { throw 'Installer does not run preflight diagnostics.' }
if (!$readme.Contains('v0.6.0') -or !$readme.Contains('HTTP') -or !$readme.Contains('preferences.state') -or
    !$program.Contains('InstanceActivation.TryOwn') -or !$trayHost.Contains('NotifyIcon')) {
    throw 'README does not describe v0.6.0 tray and intent behavior.'
}
$stabilityFirst = ([char[]](0x7A33,0x5B9A,0x4F18,0x5148,0x7684,0x20,0x43,0x6C,0x61,0x73,0x68,0x2F,0x4D,0x69,0x68,0x6F,0x6D,0x6F,0x20,0x57,0x69,0x6E,0x64,0x6F,0x77,0x73,0x20,0x8282,0x70B9,0x5B88,0x62A4,0x7A0B,0x5E8F)) -join ''
$doesNotModify = ([char[]](0x4E0D,0x4FEE,0x6539,0x20,0x43,0x6C,0x61,0x73,0x68,0x20,0x914D,0x7F6E,0x6587,0x4EF6)) -join ''
$noTelemetry = ([char[]](0x4E0D,0x4E0A,0x4F20,0x9065,0x6D4B)) -join ''
if (!$readme.Contains($stabilityFirst) -or !$readme.Contains('releases/latest') -or
    !$readme.Contains($doesNotModify) -or !$readme.Contains($noTelemetry) -or !$readme.Contains('README.en.md')) {
    throw 'README does not provide the approved landing-page hero and trust links.'
}
$twoRecentStandbys = ([char[]](0x4E24,0x4E2A,0x8FD1,0x671F,0x5907,0x7528,0x8282,0x70B9)) -join ''
$sameFailure = ([char[]](0x540C,0x7C7B,0x5F02,0x5E38)) -join ''
$tenMinutes = ([char[]](0x31,0x30,0x20,0x5206,0x949F)) -join ''
$notAttributed = ([char[]](0x4E0D,0x5F52,0x56E0,0x4E8E,0x8282,0x70B9)) -join ''
if (!$readme.Contains($twoRecentStandbys) -or !$readme.Contains($sameFailure) -or !$readme.Contains($tenMinutes) -or
    !$readme.Contains($notAttributed)) {
    throw 'README does not describe the v0.6.0 service incident circuit.'
}
if (!$readme.Contains('JMComic') -or !$readme.Contains('18comic.vip')) {
    throw 'README does not describe the opt-in JMComic web probe.'
}
if (!$security.Contains('Security Advisory') -or !$conduct.Contains('Contributor Covenant')) {
    throw 'Repository security or conduct policy is incomplete.'
}
$singleNodeSubscription = ([char[]](0x5355,0x8282,0x70B9,0x8BA2,0x9605)) -join ''
$subscriptionOrder = ([char[]](0x8BA2,0x9605,0x987A,0x5E8F)) -join ''
$liveRecheck = ([char[]](0x73B0,0x573A,0x590D,0x68C0)) -join ''
if (!$readme.Contains('proxy-providers') -or !$readme.Contains($singleNodeSubscription) -or
    !$readme.Contains($subscriptionOrder) -or !$readme.Contains($liveRecheck)) {
    throw 'README does not describe subscription compatibility and safe reload behavior.'
}
$fiveChecks = ([char[]](0x81F3,0x5C11,0x20,0x35,0x20,0x6B21)) -join ''
$thirtyMinutes = ([char[]](0x8DE8,0x5EA6,0x81F3,0x5C11,0x20,0x33,0x30,0x20,0x5206,0x949F)) -join ''
$successRate = ([char[]](0x6210,0x529F,0x7387,0x4E0D,0x4F4E,0x4E8E,0x20,0x39,0x35,0x25)) -join ''
$firstFailure = ([char[]](0x7B2C,0x4E00,0x6B21,0x660E,0x786E,0x670D,0x52A1,0x5931,0x8D25)) -join ''
if (!$readme.Contains($fiveChecks) -or !$readme.Contains($thirtyMinutes) -or !$readme.Contains($successRate) -or
    !$readme.Contains($firstFailure)) {
    throw 'README does not describe the conservative quality-switch policy.'
}
if (!$readme.Contains('500 ms') -or !$readme.Contains('1500 ms')) {
    throw 'README does not describe the HTTP response quality bands.'
}
if (!$readme.Contains('P95') -or !$readme.Contains('800 ms') -or !$readme.Contains('150 ms') -or !$readme.Contains('1000 ms')) {
    throw 'README does not describe robust latency and per-service gates.'
}
if (!(Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
    !(Select-String -LiteralPath $manifestPath -SimpleMatch 'PerMonitorV2' -Quiet) -or
    !$buildScript.Contains('/win32manifest:') -or
    !$detailsForm.Contains('AutoScaleDimensions = new SizeF(96F, 96F)') -or
    !$detailsForm.Contains('ScaleForCurrentDpi(720, 590)')) {
    throw 'Windows build is not Per-Monitor DPI aware.'
}
if (!(Test-Path -LiteralPath $iconPath -PathType Leaf) -or
    !$buildScript.Contains('/win32icon:') -or
    $trayHost.Contains('SystemIcons.Application') -or
    $detailsForm.Contains('SystemIcons.Application')) {
    throw 'Generated application icon is not embedded and used by the UI.'
}
$scriptFiles = Get-ChildItem (Join-Path $root 'scripts') -Filter '*.ps1' -File
foreach ($scriptFile in $scriptFiles) {
    $tokens = $null
    $parseErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($scriptFile.FullName, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) { throw "PowerShell parse failure: $($scriptFile.Name)" }
}
$published = @($required | ForEach-Object { Join-Path $root $_ }) +
    @(Get-ChildItem (Join-Path $root 'src') -File | ForEach-Object FullName) +
    @(Get-ChildItem (Join-Path $root 'clash') -File | ForEach-Object FullName)
$forbidden = @([Environment]::GetFolderPath('UserProfile'), ('set-your-' + 'secret'))
foreach ($value in $forbidden) {
    if (Select-String -LiteralPath $published -SimpleMatch $value -Quiet) {
        throw 'Release sources contain machine-specific values.'
    }
}
$forbiddenPatterns = @(
    '(?i)^\s*secret:\s+["'']?[^"''\s]+',
    '(?i)https?://[^\s]+(?:token|key|subscription)='
)
foreach ($pattern in $forbiddenPatterns) {
    if (Select-String -LiteralPath $published -Pattern $pattern -Quiet) {
        throw 'Release sources contain a credential-like value.'
    }
}
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('monitor-diagnose-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
try {
    $missing = & (Join-Path $root 'scripts\diagnose.ps1') -ClashDirectory $fixture -AsObject
    if ($missing.Status -ne 'CLASH_CONFIG_MISSING') { throw 'Diagnosis does not identify missing configuration.' }
    Set-Content -LiteralPath (Join-Path $fixture 'profiles.yaml') -Value 'profiles: []' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $fixture 'clash-verge.yaml') -Value "listeners:`n- name: compatibility-probe`n  port: 7896" -Encoding UTF8
    $ready = & (Join-Path $root 'scripts\diagnose.ps1') -ClashDirectory $fixture -AsObject
    if ($ready.Status -ne 'CONFIG_READY') { throw 'Diagnosis does not identify a prepared configuration.' }
    $diagnosticJson = $ready | ConvertTo-Json
    if ($diagnosticJson.Contains($fixture)) { throw 'Diagnosis leaks the local configuration path.' }
} finally { Remove-Item -LiteralPath $fixture -Recurse -Force }
Write-Output 'PASS release files exist and contain no machine-specific values'
