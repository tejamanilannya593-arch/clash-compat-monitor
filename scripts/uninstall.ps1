$ErrorActionPreference = 'Stop'
$installRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ClashCompatibilityMonitor'
$target = Join-Path $installRoot 'ClashCompatibilityMonitor.exe'
$browserHostTarget = Join-Path $installRoot 'ClashCompatibilityMonitor.BrowserHost.exe'
$expectedRoot = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ClashCompatibilityMonitor'))
if ([IO.Path]::GetFullPath($installRoot) -ne $expectedRoot) { throw 'Unexpected uninstall target.' }
Get-CimInstance Win32_Process -Filter "Name='ClashCompatibilityMonitor.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath) -eq [IO.Path]::GetFullPath($target) } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Get-CimInstance Win32_Process -Filter "Name='ClashCompatibilityMonitor.BrowserHost.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath) -eq [IO.Path]::GetFullPath($browserHostTarget) } |
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
