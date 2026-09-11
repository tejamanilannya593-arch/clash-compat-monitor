$ErrorActionPreference = 'Stop'
$installRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ClashCompatibilityMonitor'
$target = Join-Path $installRoot 'ClashCompatibilityMonitor.exe'
$expectedRoot = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ClashCompatibilityMonitor'))
if ([IO.Path]::GetFullPath($installRoot) -ne $expectedRoot) { throw 'Unexpected uninstall target.' }
Get-CimInstance Win32_Process -Filter "Name='ClashCompatibilityMonitor.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath) -eq [IO.Path]::GetFullPath($target) } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
$shortcut = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)) 'Clash Compatibility Monitor.lnk'
if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut -Force }
if (Test-Path -LiteralPath $installRoot) { Remove-Item -LiteralPath $installRoot -Recurse -Force }
Write-Output 'Clash Compatibility Monitor was removed. Clash configuration was not changed.'
