$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$required = @(
    'README.md', 'README.en.md', 'QUICKSTART.md', 'LICENSE', 'Install.cmd', 'Diagnose.cmd', 'package-release.ps1',
    'SECURITY.md', 'CODE_OF_CONDUCT.md',
    'scripts\install.ps1', 'scripts\upgrade.ps1', 'scripts\uninstall.ps1', 'scripts\diagnose.ps1',
    'browser-extension\manifest.json', 'browser-extension\service-worker.js',
    'browser-extension\content-chatgpt.js', 'browser-extension\content-gemini.js',
    'browser-extension\adapter-core.js', 'browser-extension\chatgpt-adapter.js',
    'browser-extension\gemini-adapter.js', 'browser-extension\native-host-template.json',
    'browser-extension\README.md',
    '.github\ISSUE_TEMPLATE\config.yml', '.github\ISSUE_TEMPLATE\compatibility.yml',
    '.github\ISSUE_TEMPLATE\feature.yml', '.github\dependabot.yml', '.github\workflows\codeql.yml',
    'assets\social-preview.png', 'docs\images\service-incident-flow.png',
    'docs\release-notes\v0.6.1.md', 'docs\release-notes\v0.6.2.md',
    'docs\release-notes\v0.6.3-preview.3.md'
)
foreach ($relative in $required) {
    $path = Join-Path $root $relative
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing release file: $relative" }
}
$packageScript = Get-Content -LiteralPath (Join-Path $root 'package-release.ps1') -Raw
$installScript = Get-Content -LiteralPath (Join-Path $root 'scripts\install.ps1') -Raw
$uninstallScript = Get-Content -LiteralPath (Join-Path $root 'scripts\uninstall.ps1') -Raw
$readme = Get-Content -LiteralPath (Join-Path $root 'README.md') -Raw -Encoding UTF8
$readmeEn = Get-Content -LiteralPath (Join-Path $root 'README.en.md') -Raw -Encoding UTF8
$quickStart = Get-Content -LiteralPath (Join-Path $root 'QUICKSTART.md') -Raw -Encoding UTF8
$contributing = Get-Content -LiteralPath (Join-Path $root 'CONTRIBUTING.md') -Raw -Encoding UTF8
$releaseNotes = Get-Content -LiteralPath (Join-Path $root 'docs\release-notes\v0.6.1.md') -Raw -Encoding UTF8
$program = Get-Content -LiteralPath (Join-Path $root 'src\Program.cs') -Raw
$monitorCoordinator = Get-Content -LiteralPath (Join-Path $root 'src\MonitorCoordinator.cs') -Raw
$trayHost = Get-Content -LiteralPath (Join-Path $root 'src\TrayHost.cs') -Raw
$detailsForm = Get-Content -LiteralPath (Join-Path $root 'src\DetailsForm.cs') -Raw
$buildScript = Get-Content -LiteralPath (Join-Path $root 'build.ps1') -Raw
$security = if (Test-Path -LiteralPath (Join-Path $root 'SECURITY.md')) { Get-Content -LiteralPath (Join-Path $root 'SECURITY.md') -Raw -Encoding UTF8 } else { '' }
$conduct = if (Test-Path -LiteralPath (Join-Path $root 'CODE_OF_CONDUCT.md')) { Get-Content -LiteralPath (Join-Path $root 'CODE_OF_CONDUCT.md') -Raw -Encoding UTF8 } else { '' }
$manifestPath = Join-Path $root 'assets\ClashCompatibilityMonitor.manifest'
$iconPath = Join-Path $root 'assets\ClashCompatibilityMonitor.ico'
$socialPreviewPath = Join-Path $root 'assets\social-preview.png'
$flowImagePath = Join-Path $root 'docs\images\service-incident-flow.png'
Add-Type -AssemblyName System.Drawing
if ((Get-Item -LiteralPath $socialPreviewPath).Length -ge 1MB) {
    throw 'Social preview must be smaller than 1 MiB.'
}
$socialPreview = [System.Drawing.Image]::FromFile($socialPreviewPath)
try {
    if ($socialPreview.Width -ne 1280 -or $socialPreview.Height -ne 640) {
        throw 'Social preview dimensions must be exactly 1280x640.'
    }
} finally { $socialPreview.Dispose() }
if (!$readme.Contains('docs/images/service-incident-flow.png')) {
    throw 'README does not link the service incident flow image.'
}
if (!$packageScript.Contains('ClashCompatibilityMonitor-v0.6.3-preview.3')) { throw 'Release package version is not v0.6.3-preview.3.' }
if (!$packageScript.Contains('ClashCompatibilityMonitor.BrowserHost.exe') -or
    !$packageScript.Contains('browser-extension') -or
    !$packageScript.Contains('native-host-template.json')) {
    throw 'Release package is missing the browser companion.'
}
$checksumAssignment = '$checksumFile = $zip + ''.sha256'''
$checksumFormat = '$archiveHash + ''  '' + (Split-Path -Leaf $zip)'
if (!$packageScript.Contains($checksumAssignment) -or
    !$packageScript.Contains('ChecksumFile=$checksumFile') -or
    !$packageScript.Contains($checksumFormat) -or
    !$packageScript.Contains('Encoding ASCII')) {
    throw 'Release package does not generate the required SHA-256 sidecar format.'
}
if (!$packageScript.Contains('docs\images') -or !$packageScript.Contains('service-incident-flow.png')) {
    throw 'Release package does not include the README flow image.'
}
if (!$installScript.Contains("Version='0.6.3-preview.3'")) { throw 'Installer status version is not v0.6.3-preview.3.' }
if (!$installScript.Contains('EndsWith($monitorSuffix')) { throw 'Installer does not stop virtualized monitor paths.' }
if (!$installScript.Contains("diagnosis.Status -ne 'CONFIG_READY'")) { throw 'Installer does not run preflight diagnostics.' }
$browserManifest = Get-Content -LiteralPath (Join-Path $root 'browser-extension\manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($browserManifest.manifest_version -ne 3 -or $browserManifest.version -ne '0.6.2' -or
    (Compare-Object @($browserManifest.permissions) @('nativeMessaging')) -or
    (Compare-Object @($browserManifest.host_permissions | Sort-Object) @('https://chatgpt.com/*','https://gemini.google.com/*'))) {
    throw 'Browser extension permissions are broader than approved.'
}
if (!$installScript.Contains('HKCU:\Software\Google\Chrome\NativeMessagingHosts') -or
    !$installScript.Contains('HKCU:\Software\Microsoft\Edge\NativeMessagingHosts') -or
    $installScript.Contains('HKLM:')) {
    throw 'Native host must be registered per current user for Chrome and Edge only.'
}
if (!$uninstallScript.Contains('NativeMessagingHosts') -or
    !$uninstallScript.Contains('Remove-Item -LiteralPath $registration')) {
    throw 'Uninstaller does not clean up native host registration.'
}
if (!$uninstallScript.Contains('EndsWith($monitorSuffix') -or
    !$uninstallScript.Contains('EndsWith($browserHostSuffix')) {
    throw 'Uninstaller may leave virtualized app processes running.'
}
if (!$installScript.Contains('Remove-Item -LiteralPath $browserExtensionTarget -Recurse -Force') -or
    !$installScript.Contains('Remove-Item -LiteralPath $browserHostTarget -Force') -or
    !$installScript.Contains('Remove-Item -LiteralPath $nativeManifestTarget -Force')) {
    throw 'Installer rollback may leave a partially installed browser companion.'
}
if (!$installScript.Contains('Copy-Item -LiteralPath $oldFolder -Destination $installRoot -Recurse -Force') -or
    !$installScript.Contains('Copy-Item -LiteralPath $oldShortcut -Destination $shortcutPath -Force')) {
    throw 'Installer rollback does not restore prior state and startup shortcut.'
}
$nativeTemplate = Get-Content -LiteralPath (Join-Path $root 'browser-extension\native-host-template.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($nativeTemplate.name -ne 'com.clashcompatibilitymonitor.browser' -or
    (Compare-Object @($nativeTemplate.allowed_origins) @('chrome-extension://micoadiomajggfdfbnhjbpkbccjoldlg/'))) {
    throw 'Native host template does not pin the approved extension origin.'
}
foreach ($exeName in @('ClashCompatibilityMonitor.exe','ClashCompatibilityMonitor.BrowserHost.exe')) {
    $exePath = Join-Path $root ('bin\' + $exeName)
    if (!(Test-Path -LiteralPath $exePath -PathType Leaf)) { throw "Missing built executable: $exeName" }
    $version = (Get-Item -LiteralPath $exePath).VersionInfo
    if ($version.FileVersion -ne '0.6.3.0' -or $version.ProductVersion -ne '0.6.3-preview.3') {
        throw "Incorrect Windows version metadata: $exeName"
    }
}
if ($installScript -match '(?i)Set-Content[^\r\n]*(profiles\.yaml|clash-verge\.yaml)' -or
    $installScript -match '(?i)Copy-Item[^\r\n]+-Destination[^\r\n]+\$clashRoot') {
    throw 'Installer may write a protected Clash file.'
}
$renderFixture = Join-Path ([IO.Path]::GetTempPath()) ('ccm-native-manifest-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $renderFixture | Out-Null
try {
    $renderedManifest = Join-Path $renderFixture 'native-host.json'
    & (Join-Path $root 'scripts\install.ps1') -SourceBrowserHost (Join-Path $root 'bin\ClashCompatibilityMonitor.BrowserHost.exe') `
        -RenderNativeHostManifest $renderedManifest | Out-Null
    $rendered = Get-Content -LiteralPath $renderedManifest -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($rendered.path -ne [IO.Path]::GetFullPath((Join-Path $root 'bin\ClashCompatibilityMonitor.BrowserHost.exe')) -or
        $rendered.type -ne 'stdio' -or (Compare-Object @($rendered.allowed_origins) @($nativeTemplate.allowed_origins))) {
        throw 'Native host render-only helper produced an invalid manifest.'
    }
    $defaultManifest = Join-Path $renderFixture 'native-host-default.json'
    & (Join-Path $root 'scripts\install.ps1') -RenderNativeHostManifest $defaultManifest | Out-Null
    $defaultRendered = Get-Content -LiteralPath $defaultManifest -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($defaultRendered.path -ne [IO.Path]::GetFullPath((Join-Path $root 'bin\ClashCompatibilityMonitor.BrowserHost.exe'))) {
        throw 'Installer cannot find the browser host without an explicit source path.'
    }
} finally {
    Remove-Item -LiteralPath $renderFixture -Recurse -Force
}
if (!$readme.Contains('v0.6.2') -or !$readme.Contains('HTTP') -or !$readme.Contains('preferences.state') -or
    !$program.Contains('InstanceActivation.TryOwn') -or !$trayHost.Contains('NotifyIcon')) {
    throw 'README does not describe v0.6.2 tray and intent behavior.'
}
if (!$trayHost.Contains('Path.Combine(Application.StartupPath, "browser-extension", "README.md")')) {
    throw 'Installed browser setup action does not open the packaged companion guide.'
}
if ($monitorCoordinator.Contains('Publish(Latest.WithBrowserStatus')) {
    throw 'Browser status can overwrite a newer network snapshot.'
}
$proofDocs = $readme + $quickStart
$proofTerms = @(
    ([char[]](0x81ea,0x52a8,0x53d1,0x9001) -join ''),
    ([char[]](0x81ea,0x52a8,0x5224,0x65ad) -join ''),
    ([char[]](0x81ea,0x52a8,0x5173,0x95ed) -join ''),
    ([char[]](0x6d4b,0x8bd5,0x5bf9,0x8bdd,0x4f1a,0x4fdd,0x7559) -join ''),
    ([char[]](0x9ed8,0x8ba4,0x5173,0x95ed) -join ''),
    ([char[]](0x4e0d,0x8bfb,0x53d6,0x20,0x43,0x6f,0x6f,0x6b,0x69,0x65) -join ''),
    ([char[]](0x7f51,0x9875) -join ''), '6 ', 'API'
)
if (!$program.Contains('Version = "0.6.3-preview.3"') -or
    !$buildScript.Contains('browser-extension\tests\run.js') -or
    @($proofTerms | Where-Object { !$proofDocs.Contains($_) }).Count -ne 0) {
    throw 'v0.6.2 browser verification documentation is incomplete.'
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
$accountProof = ([char[]](0x8D26,0x53F7,0x5B9E,0x6D4B)) -join ''
$loginChain = ([char[]](0x767B,0x5F55,0x94FE,0x8DEF)) -join ''
$noCookieRead = (([char[]](0x4E0D,0x8BFB,0x53D6)) -join '') + ' Cookie'
if (!$readme.Contains($accountProof) -or !$readme.Contains($loginChain) -or !$readme.Contains($noCookieRead)) {
    throw 'README does not explain account verification, login-chain evidence, and browser privacy.'
}
$currentDocs = $readme + $readmeEn + $quickStart + $contributing
if ($currentDocs -match '(?i)JMComic|18comic') {
    throw 'Current user-facing documentation still advertises the retired JMComic probe.'
}
$removed = ([char[]](0x79FB,0x9664)) -join ''
if (!$releaseNotes.Contains('JMComic') -or !$releaseNotes.Contains($removed)) {
    throw 'Release notes do not explain the JMComic retirement.'
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
$median = ([char[]](0x4E2D,0x4F4D,0x6570)) -join ''
if (!$readme.Contains($median) -or !$readme.Contains('800 ms') -or !$readme.Contains('150 ms') -or !$readme.Contains('1500 ms')) {
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
