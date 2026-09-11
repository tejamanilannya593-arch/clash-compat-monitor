param([string]$SourceExe)
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($SourceExe)) {
    $packaged = Join-Path $projectRoot 'ClashCompatibilityMonitor.exe'
    $built = Join-Path $projectRoot 'bin\ClashCompatibilityMonitor.exe'
    $SourceExe = if (Test-Path -LiteralPath $packaged) { $packaged } else { $built }
}
$SourceExe = [IO.Path]::GetFullPath($SourceExe)
if (!(Test-Path -LiteralPath $SourceExe -PathType Leaf)) { throw 'ClashCompatibilityMonitor.exe was not found.' }

$installRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ClashCompatibilityMonitor'
$target = Join-Path $installRoot 'ClashCompatibilityMonitor.exe'
$clashRoot = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'io.github.clash-verge-rev.clash-verge-rev'
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
if (Test-Path -LiteralPath $target) {
    $backup = Join-Path $installRoot ('upgrade-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path $backup | Out-Null
    Copy-Item -LiteralPath $target -Destination (Join-Path $backup 'ClashCompatibilityMonitor.exe')
    foreach ($folder in @('state','logs')) {
        $existing = Join-Path $installRoot $folder
        if (Test-Path -LiteralPath $existing) { Copy-Item -LiteralPath $existing -Destination (Join-Path $backup $folder) -Recurse }
    }
}

function Stop-InstalledMonitor {
    Get-CimInstance Win32_Process -Filter "Name='ClashCompatibilityMonitor.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath) -eq [IO.Path]::GetFullPath($target) } |
        ForEach-Object {
            $process = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
            if ($process) { $process.Kill(); if (!$process.WaitForExit(5000)) { throw 'Monitor did not stop.' } }
        }
}

New-Item -ItemType Directory -Force -Path $installRoot | Out-Null
Stop-InstalledMonitor
try {
    Copy-Item -LiteralPath $SourceExe -Destination $target -Force
    $check = Start-Process -FilePath $target -WorkingDirectory $installRoot -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru
    if ($check.ExitCode -ne 0) { throw 'Installed monitor self-test failed.' }
    $startup = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
    $shortcutPath = Join-Path $startup 'Clash Compatibility Monitor.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $target
    $shortcut.WorkingDirectory = $installRoot
    $shortcut.WindowStyle = 7
    $shortcut.Save()
    Start-Process -FilePath $target -WorkingDirectory $installRoot -WindowStyle Hidden
    Start-Sleep -Seconds 3
    $running = @(Get-CimInstance Win32_Process -Filter "Name='ClashCompatibilityMonitor.exe'" | Where-Object { $_.ExecutablePath -eq $target })
    if ($running.Count -ne 1) { throw 'Expected exactly one monitor process.' }
    foreach ($record in $before) {
        if ((Get-FileHash -LiteralPath $record.Path).Hash -ne $record.Hash) { throw 'A protected Clash file changed.' }
    }
    [pscustomobject]@{Version='0.1.0';ProcessId=$running[0].ProcessId;Backup=$backup;ClashFilesUnchanged=$true} | ConvertTo-Json -Compress
} catch {
    Stop-InstalledMonitor
    if ($backup) {
        Copy-Item -LiteralPath (Join-Path $backup 'ClashCompatibilityMonitor.exe') -Destination $target -Force
        Start-Process -FilePath $target -WorkingDirectory $installRoot -WindowStyle Hidden
    }
    throw
}
