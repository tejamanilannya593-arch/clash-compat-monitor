$ErrorActionPreference = 'Stop'
$userProfile = [Environment]::GetFolderPath('UserProfile')
if ([String]::IsNullOrWhiteSpace($userProfile)) { throw 'Cannot resolve the current user profile.' }
$physicalLocalAppData = Join-Path $userProfile 'AppData\Local'
$installRoot = Join-Path $physicalLocalAppData 'ClashCompatibilityMonitor'
$target = Join-Path $installRoot 'ClashCompatibilityMonitor.exe'
$launcherTarget = Join-Path $installRoot 'launcher.vbs'
$browserHostTarget = Join-Path $installRoot 'ClashCompatibilityMonitor.BrowserHost.exe'
$monitorSuffix = '\ClashCompatibilityMonitor\ClashCompatibilityMonitor.exe'
$browserHostSuffix = '\ClashCompatibilityMonitor\ClashCompatibilityMonitor.BrowserHost.exe'
$expectedRoot = [IO.Path]::GetFullPath((Join-Path (Join-Path $userProfile 'AppData\Local') 'ClashCompatibilityMonitor'))
if ([IO.Path]::GetFullPath($installRoot) -ne $expectedRoot) { throw 'Unexpected uninstall target.' }
function Stop-InstalledLauncher {
    $resolvedLauncher = [IO.Path]::GetFullPath($launcherTarget)
    Get-CimInstance Win32_Process -Filter "Name='wscript.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and
            $_.CommandLine.IndexOf($resolvedLauncher, [StringComparison]::OrdinalIgnoreCase) -ge 0 } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
}
Stop-InstalledLauncher
Get-CimInstance Win32_Process -Filter "Name='ClashCompatibilityMonitor.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath).EndsWith($monitorSuffix, [StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Get-CimInstance Win32_Process -Filter "Name='ClashCompatibilityMonitor.BrowserHost.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath).EndsWith($browserHostSuffix, [StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
$registrations = @(
    'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.clashcompatibilitymonitor.browser',
    'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.clashcompatibilitymonitor.browser'
)
foreach ($registration in $registrations) {
    if (Test-Path -LiteralPath $registration) { Remove-Item -LiteralPath $registration -Recurse -Force }
}
$shortcut = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)) 'Clash Compatibility Monitor.lnk'
if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut -Force }
if (Test-Path -LiteralPath $installRoot) { Remove-Item -LiteralPath $installRoot -Recurse -Force }
Write-Output 'Clash Compatibility Monitor was removed. Clash configuration was not changed.'
