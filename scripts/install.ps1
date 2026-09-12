param(
    [string]$SourceExe,
    [string]$SourceBrowserHost,
    [string]$RenderNativeHostManifest
)
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$nativeHostName = 'com.clashcompatibilitymonitor.browser'
$nativeTemplatePath = Join-Path $projectRoot 'browser-extension\native-host-template.json'

function Resolve-BrowserHostSource {
    if (![string]::IsNullOrWhiteSpace($SourceBrowserHost)) { return [IO.Path]::GetFullPath($SourceBrowserHost) }
    if (![string]::IsNullOrWhiteSpace($SourceExe)) {
        $besideMonitor = Join-Path (Split-Path -Parent ([IO.Path]::GetFullPath($SourceExe))) 'ClashCompatibilityMonitor.BrowserHost.exe'
        if (Test-Path -LiteralPath $besideMonitor -PathType Leaf) { return $besideMonitor }
    }
    $packagedHost = Join-Path $projectRoot 'ClashCompatibilityMonitor.BrowserHost.exe'
    $builtHost = Join-Path $projectRoot 'bin\ClashCompatibilityMonitor.BrowserHost.exe'
    if (Test-Path -LiteralPath $packagedHost -PathType Leaf) { return $packagedHost }
    return $builtHost
}

function Write-NativeHostManifest([string]$OutputPath, [string]$HostPath) {
    if (!(Test-Path -LiteralPath $nativeTemplatePath -PathType Leaf)) { throw 'Native host template was not found.' }
    $manifest = Get-Content -LiteralPath $nativeTemplatePath -Raw -Encoding UTF8 | ConvertFrom-Json
    $manifest.path = [IO.Path]::GetFullPath($HostPath)
    $parent = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
    if (![string]::IsNullOrWhiteSpace($parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    $json = $manifest | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), $json, (New-Object Text.UTF8Encoding($false)))
}

$SourceBrowserHost = Resolve-BrowserHostSource
if (!(Test-Path -LiteralPath $SourceBrowserHost -PathType Leaf)) { throw 'ClashCompatibilityMonitor.BrowserHost.exe was not found.' }
if (![string]::IsNullOrWhiteSpace($RenderNativeHostManifest)) {
    Write-NativeHostManifest $RenderNativeHostManifest $SourceBrowserHost
    Write-Output ([IO.Path]::GetFullPath($RenderNativeHostManifest))
    return
}

if ([string]::IsNullOrWhiteSpace($SourceExe)) {
    $packaged = Join-Path $projectRoot 'ClashCompatibilityMonitor.exe'
    $built = Join-Path $projectRoot 'bin\ClashCompatibilityMonitor.exe'
    $SourceExe = if (Test-Path -LiteralPath $packaged) { $packaged } else { $built }
}
$SourceExe = [IO.Path]::GetFullPath($SourceExe)
if (!(Test-Path -LiteralPath $SourceExe -PathType Leaf)) { throw 'ClashCompatibilityMonitor.exe was not found.' }

$installRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ClashCompatibilityMonitor'
$target = Join-Path $installRoot 'ClashCompatibilityMonitor.exe'
$browserHostTarget = Join-Path $installRoot 'ClashCompatibilityMonitor.BrowserHost.exe'
$browserExtensionSource = Join-Path $projectRoot 'browser-extension'
$browserExtensionTarget = Join-Path $installRoot 'browser-extension'
$nativeManifestTarget = Join-Path $installRoot 'browser-native-host.json'
$startup = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
$shortcutPath = Join-Path $startup 'Clash Compatibility Monitor.lnk'
if ([IO.Path]::GetFullPath($browserExtensionSource) -eq [IO.Path]::GetFullPath($browserExtensionTarget)) {
    throw 'Install source and destination must be different.'
}
if (!(Test-Path -LiteralPath (Join-Path $browserExtensionSource 'manifest.json') -PathType Leaf)) {
    throw 'Browser companion files were not found.'
}
$registrations = @(
    "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$nativeHostName",
    "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\$nativeHostName"
)
$previousRegistrations = @{}
foreach ($registration in $registrations) {
    if (Test-Path -LiteralPath $registration) { $previousRegistrations[$registration] = (Get-Item -LiteralPath $registration).GetValue('') }
}
$clashRoot = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'io.github.clash-verge-rev.clash-verge-rev'
$diagnosis = & (Join-Path $PSScriptRoot 'diagnose.ps1') -ClashDirectory $clashRoot -AsObject
if ($diagnosis.Status -ne 'CONFIG_READY') { throw ($diagnosis.Status + ': ' + $diagnosis.NextStep + ' See QUICKSTART.md.') }
$protected = @('profiles.yaml', 'clash-verge.yaml') | ForEach-Object { Join-Path $clashRoot $_ }
foreach ($path in $protected) { if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required Clash file is missing: $path" } }
$generatedConfig = Join-Path $clashRoot 'clash-verge.yaml'
foreach ($marker in @('compatibility-probe', 'port: 7896')) {
    if (!(Select-String -LiteralPath $generatedConfig -SimpleMatch $marker -Quiet)) {
        throw "Enable clash/enhancement.js first; missing marker: $marker"
    }
}
$before = @(Get-FileHash -LiteralPath $protected)
$backup = $null
$monitorSuffix = '\ClashCompatibilityMonitor\ClashCompatibilityMonitor.exe'
$browserHostSuffix = '\ClashCompatibilityMonitor\ClashCompatibilityMonitor.BrowserHost.exe'
if ((Test-Path -LiteralPath $target -PathType Leaf -ErrorAction SilentlyContinue) -or
    (Test-Path -LiteralPath $browserHostTarget -PathType Leaf -ErrorAction SilentlyContinue) -or
    (Test-Path -LiteralPath $nativeManifestTarget -PathType Leaf -ErrorAction SilentlyContinue) -or
    (Test-Path -LiteralPath $browserExtensionTarget -PathType Container -ErrorAction SilentlyContinue) -or
    (Test-Path -LiteralPath (Join-Path $installRoot 'state') -PathType Container -ErrorAction SilentlyContinue) -or
    (Test-Path -LiteralPath (Join-Path $installRoot 'logs') -PathType Container -ErrorAction SilentlyContinue) -or
    (Test-Path -LiteralPath $shortcutPath -PathType Leaf -ErrorAction SilentlyContinue)) {
    $backup = Join-Path $installRoot ('upgrade-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $backup | Out-Null
    if (Test-Path -LiteralPath $target -PathType Leaf) { Copy-Item -LiteralPath $target -Destination (Join-Path $backup 'ClashCompatibilityMonitor.exe') }
    if (Test-Path -LiteralPath $browserHostTarget -PathType Leaf) { Copy-Item -LiteralPath $browserHostTarget -Destination (Join-Path $backup 'ClashCompatibilityMonitor.BrowserHost.exe') }
    if (Test-Path -LiteralPath $nativeManifestTarget -PathType Leaf) { Copy-Item -LiteralPath $nativeManifestTarget -Destination (Join-Path $backup 'browser-native-host.json') }
    if (Test-Path -LiteralPath $browserExtensionTarget) { Copy-Item -LiteralPath $browserExtensionTarget -Destination $backup -Recurse }
    foreach ($folder in @('state','logs')) {
        $existing = Join-Path $installRoot $folder
        if (Test-Path -LiteralPath $existing) { Copy-Item -LiteralPath $existing -Destination (Join-Path $backup $folder) -Recurse }
    }
    if (Test-Path -LiteralPath $shortcutPath -PathType Leaf) {
        Copy-Item -LiteralPath $shortcutPath -Destination (Join-Path $backup 'startup.lnk')
    }
}

function Test-InstalledMonitorPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    $full = [IO.Path]::GetFullPath($Path)
    return ($full -eq [IO.Path]::GetFullPath($target)) -or
        $full.EndsWith($monitorSuffix, [StringComparison]::OrdinalIgnoreCase)
}

function Stop-InstalledMonitor {
    Get-CimInstance Win32_Process -Filter "Name='ClashCompatibilityMonitor.exe'" -ErrorAction SilentlyContinue |
        Where-Object { Test-InstalledMonitorPath $_.ExecutablePath } |
        ForEach-Object {
            $process = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
            if ($process) { $process.Kill(); if (!$process.WaitForExit(5000)) { throw 'Monitor did not stop.' } }
        }
}

function Stop-InstalledBrowserHost {
    Get-CimInstance Win32_Process -Filter "Name='ClashCompatibilityMonitor.BrowserHost.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath).EndsWith($browserHostSuffix, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
}

New-Item -ItemType Directory -Force -Path $installRoot | Out-Null
Stop-InstalledMonitor
Stop-InstalledBrowserHost
try {
    Copy-Item -LiteralPath $SourceExe -Destination $target -Force
    Copy-Item -LiteralPath $SourceBrowserHost -Destination $browserHostTarget -Force
    if (Test-Path -LiteralPath $browserExtensionTarget -PathType Container) {
        Remove-Item -LiteralPath $browserExtensionTarget -Recurse -Force
    }
    Copy-Item -LiteralPath $browserExtensionSource -Destination $installRoot -Recurse -Force
    Write-NativeHostManifest $nativeManifestTarget $browserHostTarget
    foreach ($registration in $registrations) {
        New-Item -Path $registration -Force | Out-Null
        Set-Item -LiteralPath $registration -Value $nativeManifestTarget
        if ((Get-Item -LiteralPath $registration).GetValue('') -ne $nativeManifestTarget) {
            throw 'Native host registration could not be verified.'
        }
    }
    $hostCheck = Start-Process -FilePath $browserHostTarget -WorkingDirectory $installRoot -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru
    if ($hostCheck.ExitCode -ne 0) { throw 'Browser native host self-test failed.' }
    $check = Start-Process -FilePath $target -WorkingDirectory $installRoot -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru
    if ($check.ExitCode -ne 0) { throw 'Cannot connect to the Mihomo core. Start Clash Verge Rev and verify the enhancement groups are present, then retry. The previous program will be restored if available.' }
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $target
    $shortcut.WorkingDirectory = $installRoot
    $shortcut.WindowStyle = 7
    $shortcut.Save()
    Start-Process -FilePath $target -WorkingDirectory $installRoot -WindowStyle Hidden
    Start-Sleep -Seconds 3
    $running = @(Get-CimInstance Win32_Process -Filter "Name='ClashCompatibilityMonitor.exe'" | Where-Object { Test-InstalledMonitorPath $_.ExecutablePath })
    if ($running.Count -ne 1) { throw 'Expected exactly one monitor process.' }
    foreach ($record in $before) {
        if ((Get-FileHash -LiteralPath $record.Path).Hash -ne $record.Hash) { throw 'A protected Clash file changed.' }
    }
    [pscustomobject]@{Version='0.7.0-preview.1';ProcessId=$running[0].ProcessId;Backup=$backup;ClashFilesUnchanged=$true} | ConvertTo-Json -Compress
} catch {
    Stop-InstalledMonitor
    Stop-InstalledBrowserHost
    foreach ($registration in $registrations) {
        if ($previousRegistrations.ContainsKey($registration)) {
            New-Item -Path $registration -Force | Out-Null
            Set-Item -LiteralPath $registration -Value $previousRegistrations[$registration]
        } elseif (Test-Path -LiteralPath $registration) {
            Remove-Item -LiteralPath $registration -Recurse -Force
        }
    }
    if (Test-Path -LiteralPath $browserExtensionTarget -PathType Container) {
        Remove-Item -LiteralPath $browserExtensionTarget -Recurse -Force
    }
    if (Test-Path -LiteralPath $browserHostTarget -PathType Leaf) {
        Remove-Item -LiteralPath $browserHostTarget -Force
    }
    if (Test-Path -LiteralPath $nativeManifestTarget -PathType Leaf) {
        Remove-Item -LiteralPath $nativeManifestTarget -Force
    }
    if (Test-Path -LiteralPath $target -PathType Leaf) {
        Remove-Item -LiteralPath $target -Force
    }
    foreach ($folder in @('state','logs')) {
        $existing = Join-Path $installRoot $folder
        if (Test-Path -LiteralPath $existing -PathType Container) {
            Remove-Item -LiteralPath $existing -Recurse -Force
        }
        $oldFolder = if ($backup) { Join-Path $backup $folder } else { $null }
        if ($oldFolder -and (Test-Path -LiteralPath $oldFolder -PathType Container)) {
            Copy-Item -LiteralPath $oldFolder -Destination $installRoot -Recurse -Force
        }
    }
    if (Test-Path -LiteralPath $shortcutPath -PathType Leaf) {
        Remove-Item -LiteralPath $shortcutPath -Force
    }
    $oldShortcut = if ($backup) { Join-Path $backup 'startup.lnk' } else { $null }
    if ($oldShortcut -and (Test-Path -LiteralPath $oldShortcut -PathType Leaf)) {
        Copy-Item -LiteralPath $oldShortcut -Destination $shortcutPath -Force
    }
    if ($backup) {
        $oldMonitor = Join-Path $backup 'ClashCompatibilityMonitor.exe'
        $oldHost = Join-Path $backup 'ClashCompatibilityMonitor.BrowserHost.exe'
        $oldManifest = Join-Path $backup 'browser-native-host.json'
        if (Test-Path -LiteralPath $oldMonitor) { Copy-Item -LiteralPath $oldMonitor -Destination $target -Force }
        if (Test-Path -LiteralPath $oldHost) { Copy-Item -LiteralPath $oldHost -Destination $browserHostTarget -Force }
        if (Test-Path -LiteralPath $oldManifest) { Copy-Item -LiteralPath $oldManifest -Destination $nativeManifestTarget -Force }
        $oldExtension = Join-Path $backup 'browser-extension'
        if (Test-Path -LiteralPath $oldExtension) { Copy-Item -LiteralPath $oldExtension -Destination $installRoot -Recurse -Force }
        if (Test-Path -LiteralPath $oldMonitor) { Start-Process -FilePath $target -WorkingDirectory $installRoot -WindowStyle Hidden }
    }
    throw
}
