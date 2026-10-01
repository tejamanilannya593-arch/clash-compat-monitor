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

$userProfile = [Environment]::GetFolderPath('UserProfile')
if ([String]::IsNullOrWhiteSpace($userProfile)) { throw 'Cannot resolve the current user profile.' }
$physicalLocalAppData = Join-Path $userProfile 'AppData\Local'
$installRoot = Join-Path $physicalLocalAppData 'ClashCompatibilityMonitor'
$target = Join-Path $installRoot 'ClashCompatibilityMonitor.exe'
$legacyLauncherTarget = Join-Path $installRoot 'launcher.vbs'
$legacyHost = Join-Path $installRoot 'ClashCompatibilityMonitor.BrowserHost.exe'
$legacyManifest = Join-Path $installRoot 'browser-native-host.json'
$legacyExtension = Join-Path $installRoot 'browser-extension'
$startup = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
$shortcutPath = Join-Path $startup 'Clash Compatibility Monitor.lnk'
$legacyHostSuffix = '\ClashCompatibilityMonitor\ClashCompatibilityMonitor.BrowserHost.exe'
$registrations = @(
    'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.clashcompatibilitymonitor.browser',
    'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.clashcompatibilitymonitor.browser'
)

$redirectedTarget = $null
if (Test-Path -LiteralPath $shortcutPath -PathType Leaf) {
    $shortcutShell = New-Object -ComObject WScript.Shell
    $previousTarget = $shortcutShell.CreateShortcut($shortcutPath).TargetPath
    if (![String]::IsNullOrWhiteSpace($previousTarget)) {
        $resolvedPreviousTarget = [IO.Path]::GetFullPath($previousTarget)
        $redirectedPattern = [Regex]::Escape((Join-Path $userProfile 'AppData\Local\Packages')) +
            '\\[^\\]+\\LocalCache\\Local\\ClashCompatibilityMonitor\\ClashCompatibilityMonitor\.exe$'
        if ($resolvedPreviousTarget -match $redirectedPattern) { $redirectedTarget = $resolvedPreviousTarget }
    }
}

$clashRoot = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'io.github.clash-verge-rev.clash-verge-rev'
$diagnosis = & (Join-Path $PSScriptRoot 'diagnose.ps1') -ClashDirectory $clashRoot -AsObject
if ($diagnosis.Status -ne 'CONFIG_READY') { throw ($diagnosis.Status + ': ' + $diagnosis.NextStep + ' See QUICKSTART.md.') }
$protected = @('profiles.yaml', 'clash-verge.yaml') | ForEach-Object { Join-Path $clashRoot $_ }
foreach ($path in $protected) { if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required Clash file is missing: $path" } }
$before = @(Get-FileHash -LiteralPath $protected)

function Test-IsInstalledMonitorProcess {
    param($Process)
    if (!$Process) { return $false }
    if ([String]::IsNullOrWhiteSpace($Process.ExecutablePath)) { return $false }
    $resolvedProcess = [IO.Path]::GetFullPath($Process.ExecutablePath)
    if ($resolvedProcess.Equals(
        [IO.Path]::GetFullPath($target), [StringComparison]::OrdinalIgnoreCase)) { return $true }
    return $redirectedTarget -and $resolvedProcess.Equals(
        $redirectedTarget, [StringComparison]::OrdinalIgnoreCase)
}
function Get-InstalledMonitorProcesses {
    $named = @(Get-CimInstance Win32_Process -Filter "Name='ClashCompatibilityMonitor.exe'" -ErrorAction SilentlyContinue)
    $hidden = @($named | Where-Object { [String]::IsNullOrWhiteSpace($_.ExecutablePath) })
    if ($hidden.Count -gt 0) {
        throw 'Close any elevated ClashCompatibilityMonitor window and run the installer again.'
    }
    return @($named | Where-Object { Test-IsInstalledMonitorProcess $_ })
}
function Stop-InstalledMonitor {
    $installed = @(Get-InstalledMonitorProcesses)
    foreach ($item in @($installed | Where-Object { $_.CommandLine -match '(?i)--startup-supervisor' }) +
        @($installed | Where-Object { $_.CommandLine -notmatch '(?i)--startup-supervisor' })) {
            if ($item) {
            $process = Get-Process -Id $item.ProcessId -ErrorAction SilentlyContinue
            if ($process) {
                try { $process.Kill() }
                catch { throw 'Close any elevated ClashCompatibilityMonitor window and run the installer again.' }
                if (!$process.WaitForExit(5000)) { throw 'Monitor did not stop.' }
            }
            }
        }
}
function Stop-InstalledLauncher {
    $resolvedLauncher = [IO.Path]::GetFullPath($legacyLauncherTarget)
    Get-CimInstance Win32_Process -Filter "Name='wscript.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and
            $_.CommandLine.IndexOf($resolvedLauncher, [StringComparison]::OrdinalIgnoreCase) -ge 0 } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
}
function Stop-LegacyHost {
    Get-CimInstance Win32_Process -Filter "Name='ClashCompatibilityMonitor.BrowserHost.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath).EndsWith($legacyHostSuffix, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
}

New-Item -ItemType Directory -Force -Path $installRoot | Out-Null
$backup = Join-Path $installRoot ('upgrade-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $backup | Out-Null
foreach ($item in @('ClashCompatibilityMonitor.exe','launcher.vbs','ClashCompatibilityMonitor.BrowserHost.exe','browser-native-host.json')) {
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

$null = @(Get-InstalledMonitorProcesses)
Stop-InstalledLauncher
Stop-InstalledMonitor
Stop-LegacyHost
try {
    Copy-Item -LiteralPath $SourceExe -Destination $target -Force
    if (Test-Path -LiteralPath $legacyLauncherTarget -PathType Leaf) { Remove-Item -LiteralPath $legacyLauncherTarget -Force }
    foreach ($legacy in @($legacyHost,$legacyManifest)) { if (Test-Path -LiteralPath $legacy -PathType Leaf) { Remove-Item -LiteralPath $legacy -Force } }
    if (Test-Path -LiteralPath $legacyExtension -PathType Container) { Remove-Item -LiteralPath $legacyExtension -Recurse -Force }
    foreach ($registration in $registrations) { if (Test-Path -LiteralPath $registration) { Remove-Item -LiteralPath $registration -Recurse -Force } }
    $check = Start-Process -FilePath $target -WorkingDirectory $installRoot -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru
    if ($check.ExitCode -ne 0) { throw 'Cannot connect to the Mihomo core. Start Clash Verge Rev and verify the enhancement groups are present.' }
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $target
    $shortcut.Arguments = '--startup-supervisor'
    $shortcut.WorkingDirectory = $installRoot
    $shortcut.WindowStyle = 7
    $shortcut.Save()
    $savedTarget = $shell.CreateShortcut($shortcutPath).TargetPath
    if (![IO.Path]::GetFullPath($savedTarget).Equals(
        [IO.Path]::GetFullPath($target), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Startup shortcut was redirected away from the physical install directory.'
    }
    Start-Process -FilePath $target -WorkingDirectory $installRoot -ArgumentList '--startup-supervisor' -WindowStyle Hidden
    Start-Sleep -Seconds 3
    $running = @(Get-InstalledMonitorProcesses | Where-Object {
        [IO.Path]::GetFullPath($_.ExecutablePath).Equals(
            [IO.Path]::GetFullPath($target), [StringComparison]::OrdinalIgnoreCase)
    })
    $supervisors = @($running | Where-Object { $_.CommandLine -match '(?i)--startup-supervisor' })
    $workers = @($running | Where-Object { $_.CommandLine -notmatch '(?i)--startup-supervisor' })
    if ($supervisors.Count -ne 1 -or $workers.Count -ne 1) { throw 'Expected one startup supervisor and one monitor process.' }
    foreach ($record in $before) { if ((Get-FileHash -LiteralPath $record.Path).Hash -ne $record.Hash) { throw 'A protected Clash file changed.' } }
    [pscustomobject]@{Version='0.7.0-preview.35';ProcessId=$workers[0].ProcessId;SupervisorProcessId=$supervisors[0].ProcessId;Backup=$backup;ClashFilesUnchanged=$true;LegacyBrowserCompanionRemoved=$true} | ConvertTo-Json -Compress
} catch {
    Stop-InstalledLauncher
    Stop-InstalledMonitor
    Stop-LegacyHost
    foreach ($registration in $registrations) {
        if ($previousRegistrations.ContainsKey($registration)) { New-Item -Path $registration -Force | Out-Null; Set-Item -LiteralPath $registration -Value $previousRegistrations[$registration] }
        elseif (Test-Path -LiteralPath $registration) { Remove-Item -LiteralPath $registration -Recurse -Force }
    }
    foreach ($item in @('ClashCompatibilityMonitor.exe','launcher.vbs','ClashCompatibilityMonitor.BrowserHost.exe','browser-native-host.json')) {
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
    if (Test-Path -LiteralPath $oldShortcut -PathType Leaf) { Start-Process -FilePath $shortcutPath }
    elseif (Test-Path -LiteralPath $target -PathType Leaf) { Start-Process -FilePath $target -WorkingDirectory $installRoot -WindowStyle Hidden }
    throw
}
