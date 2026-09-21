param([string]$SourceExe)
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($SourceExe)) {
    $packaged = Join-Path $projectRoot 'ClashCompatibilityMonitor.exe'
    $built = Join-Path $projectRoot 'bin\ClashCompatibilityMonitor.exe'
    $SourceExe = if (Test-Path -LiteralPath $packaged -PathType Leaf) { $packaged } else { $built }
}
$SourceExe = [IO.Path]::GetFullPath($SourceExe)
if (!(Test-Path -LiteralPath $SourceExe -PathType Leaf)) { throw 'ClashCompatibilityMonitor.exe was not found.' }

$installRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ClashCompatibilityMonitor'
$target = Join-Path $installRoot 'ClashCompatibilityMonitor.exe'
$legacyHost = Join-Path $installRoot 'ClashCompatibilityMonitor.BrowserHost.exe'
$legacyManifest = Join-Path $installRoot 'browser-native-host.json'
$legacyExtension = Join-Path $installRoot 'browser-extension'
$startup = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
$shortcutPath = Join-Path $startup 'Clash Compatibility Monitor.lnk'
$monitorSuffix = '\ClashCompatibilityMonitor\ClashCompatibilityMonitor.exe'
$legacyHostSuffix = '\ClashCompatibilityMonitor\ClashCompatibilityMonitor.BrowserHost.exe'
$registrations = @(
    'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.clashcompatibilitymonitor.browser',
    'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.clashcompatibilitymonitor.browser'
)

$clashRoot = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'io.github.clash-verge-rev.clash-verge-rev'
$diagnosis = & (Join-Path $PSScriptRoot 'diagnose.ps1') -ClashDirectory $clashRoot -AsObject
if ($diagnosis.Status -ne 'CONFIG_READY') { throw ($diagnosis.Status + ': ' + $diagnosis.NextStep + ' See QUICKSTART.md.') }
$protected = @('profiles.yaml', 'clash-verge.yaml') | ForEach-Object { Join-Path $clashRoot $_ }
foreach ($path in $protected) { if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required Clash file is missing: $path" } }
$before = @(Get-FileHash -LiteralPath $protected)

function Stop-InstalledMonitor {
    Get-CimInstance Win32_Process -Filter "Name='ClashCompatibilityMonitor.exe'" -ErrorAction SilentlyContinue |
        ForEach-Object {
            $process = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
            if ($process) { $process.Kill(); if (!$process.WaitForExit(5000)) { throw 'Monitor did not stop.' } }
        }
}
function Stop-LegacyHost {
    Get-CimInstance Win32_Process -Filter "Name='ClashCompatibilityMonitor.BrowserHost.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath).EndsWith($legacyHostSuffix, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
}

New-Item -ItemType Directory -Force -Path $installRoot | Out-Null
$backup = Join-Path $installRoot ('upgrade-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $backup | Out-Null
foreach ($item in @('ClashCompatibilityMonitor.exe','ClashCompatibilityMonitor.BrowserHost.exe','browser-native-host.json')) {
    $existing = Join-Path $installRoot $item
    if (Test-Path -LiteralPath $existing -PathType Leaf) { Copy-Item -LiteralPath $existing -Destination (Join-Path $backup $item) }
}
foreach ($folder in @('state','logs','browser-extension')) {
    $existing = Join-Path $installRoot $folder
    if (Test-Path -LiteralPath $existing -PathType Container) { Copy-Item -LiteralPath $existing -Destination $backup -Recurse }
}
if (Test-Path -LiteralPath $shortcutPath -PathType Leaf) { Copy-Item -LiteralPath $shortcutPath -Destination (Join-Path $backup 'startup.lnk') }
$previousRegistrations = @{}
foreach ($registration in $registrations) {
    if (Test-Path -LiteralPath $registration) { $previousRegistrations[$registration] = (Get-Item -LiteralPath $registration).GetValue('') }
}

Stop-InstalledMonitor
Stop-LegacyHost
try {
    Copy-Item -LiteralPath $SourceExe -Destination $target -Force
    foreach ($legacy in @($legacyHost,$legacyManifest)) { if (Test-Path -LiteralPath $legacy -PathType Leaf) { Remove-Item -LiteralPath $legacy -Force } }
    if (Test-Path -LiteralPath $legacyExtension -PathType Container) { Remove-Item -LiteralPath $legacyExtension -Recurse -Force }
    foreach ($registration in $registrations) { if (Test-Path -LiteralPath $registration) { Remove-Item -LiteralPath $registration -Recurse -Force } }
    $check = Start-Process -FilePath $target -WorkingDirectory $installRoot -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru
    if ($check.ExitCode -ne 0) { throw 'Cannot connect to the Mihomo core. Start Clash Verge Rev and verify the enhancement groups are present.' }
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $target
    $shortcut.WorkingDirectory = $installRoot
    $shortcut.WindowStyle = 7
    $shortcut.Save()
    Start-Process -FilePath $target -WorkingDirectory $installRoot -WindowStyle Hidden
    Start-Sleep -Seconds 3
    $running = @(Get-CimInstance Win32_Process -Filter "Name='ClashCompatibilityMonitor.exe'" |
        Where-Object { $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath) -eq [IO.Path]::GetFullPath($target) })
    if ($running.Count -ne 1) { throw 'Expected exactly one monitor process.' }
    foreach ($record in $before) { if ((Get-FileHash -LiteralPath $record.Path).Hash -ne $record.Hash) { throw 'A protected Clash file changed.' } }
    [pscustomobject]@{Version='0.7.0-preview.10';ProcessId=$running[0].ProcessId;Backup=$backup;ClashFilesUnchanged=$true;LegacyBrowserCompanionRemoved=$true} | ConvertTo-Json -Compress
} catch {
    Stop-InstalledMonitor
    Stop-LegacyHost
    foreach ($registration in $registrations) {
        if ($previousRegistrations.ContainsKey($registration)) { New-Item -Path $registration -Force | Out-Null; Set-Item -LiteralPath $registration -Value $previousRegistrations[$registration] }
        elseif (Test-Path -LiteralPath $registration) { Remove-Item -LiteralPath $registration -Recurse -Force }
    }
    foreach ($item in @('ClashCompatibilityMonitor.exe','ClashCompatibilityMonitor.BrowserHost.exe','browser-native-host.json')) {
        $installed = Join-Path $installRoot $item
        if (Test-Path -LiteralPath $installed -PathType Leaf) { Remove-Item -LiteralPath $installed -Force }
        $old = Join-Path $backup $item
        if (Test-Path -LiteralPath $old -PathType Leaf) { Copy-Item -LiteralPath $old -Destination $installed -Force }
    }
    foreach ($folder in @('state','logs','browser-extension')) {
        $installed = Join-Path $installRoot $folder
        if (Test-Path -LiteralPath $installed -PathType Container) { Remove-Item -LiteralPath $installed -Recurse -Force }
        $old = Join-Path $backup $folder
        if (Test-Path -LiteralPath $old -PathType Container) { Copy-Item -LiteralPath $old -Destination $installRoot -Recurse -Force }
    }
    if (Test-Path -LiteralPath $shortcutPath -PathType Leaf) { Remove-Item -LiteralPath $shortcutPath -Force }
    $oldShortcut = Join-Path $backup 'startup.lnk'
    if (Test-Path -LiteralPath $oldShortcut -PathType Leaf) { Copy-Item -LiteralPath $oldShortcut -Destination $shortcutPath -Force }
    if (Test-Path -LiteralPath $target -PathType Leaf) { Start-Process -FilePath $target -WorkingDirectory $installRoot -WindowStyle Hidden }
    throw
}
