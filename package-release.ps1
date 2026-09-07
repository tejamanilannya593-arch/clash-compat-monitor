$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Build or tests failed.' }
& node (Join-Path $root 'clash\enhancement.test.js')
if ($LASTEXITCODE -ne 0) { throw 'Enhancement tests failed.' }
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tests\Release.Tests.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Release checks failed.' }
$distRoot = Join-Path $root 'dist'
$release = Join-Path $distRoot 'ClashCompatibilityMonitor-v0.1.1'
$resolvedRoot = [IO.Path]::GetFullPath($distRoot).TrimEnd('\') + '\'
$resolvedRelease = [IO.Path]::GetFullPath($release)
if (!$resolvedRelease.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe release path.' }
if (Test-Path -LiteralPath $release) { Remove-Item -LiteralPath $release -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $release 'scripts'),(Join-Path $release 'clash') | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'bin\ClashCompatibilityMonitor.exe') -Destination $release
Copy-Item -LiteralPath (Join-Path $root 'README.md'),(Join-Path $root 'LICENSE') -Destination $release
Copy-Item -LiteralPath (Join-Path $root 'scripts\install.ps1'),(Join-Path $root 'scripts\upgrade.ps1'),(Join-Path $root 'scripts\uninstall.ps1') -Destination (Join-Path $release 'scripts')
Copy-Item -LiteralPath (Join-Path $root 'clash\enhancement.js') -Destination (Join-Path $release 'clash')
$zip = $release + '.zip'
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -LiteralPath $release -DestinationPath $zip
[pscustomobject]@{Release=$release;Archive=$zip;Sha256=(Get-FileHash $zip).Hash} | ConvertTo-Json -Compress
