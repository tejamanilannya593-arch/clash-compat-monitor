$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Build or tests failed.' }
& node (Join-Path $root 'clash\enhancement.test.js')
if ($LASTEXITCODE -ne 0) { throw 'Enhancement tests failed.' }
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tests\Release.Tests.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Release checks failed.' }
$distRoot = Join-Path $root 'dist'
$release = Join-Path $distRoot 'ClashCompatibilityMonitor-v0.6.2'
$resolvedRoot = [IO.Path]::GetFullPath($distRoot).TrimEnd('\') + '\'
$resolvedRelease = [IO.Path]::GetFullPath($release)
if (!$resolvedRelease.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe release path.' }
if (Test-Path -LiteralPath $release) { Remove-Item -LiteralPath $release -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $release 'scripts'),(Join-Path $release 'clash'),(Join-Path $release 'docs\images'),(Join-Path $release 'docs\release-notes'),(Join-Path $release 'browser-extension') | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'bin\ClashCompatibilityMonitor.exe') -Destination $release
Copy-Item -LiteralPath (Join-Path $root 'bin\ClashCompatibilityMonitor.BrowserHost.exe') -Destination $release
Copy-Item -LiteralPath (Join-Path $root 'README.md'),(Join-Path $root 'README.en.md'),(Join-Path $root 'QUICKSTART.md'),(Join-Path $root 'LICENSE'),(Join-Path $root 'Install.cmd'),(Join-Path $root 'Diagnose.cmd'),(Join-Path $root 'SECURITY.md'),(Join-Path $root 'CONTRIBUTING.md'),(Join-Path $root 'CODE_OF_CONDUCT.md') -Destination $release
Copy-Item -LiteralPath (Join-Path $root 'scripts\install.ps1'),(Join-Path $root 'scripts\upgrade.ps1'),(Join-Path $root 'scripts\uninstall.ps1'),(Join-Path $root 'scripts\diagnose.ps1') -Destination (Join-Path $release 'scripts')
Copy-Item -LiteralPath (Join-Path $root 'clash\enhancement.js') -Destination (Join-Path $release 'clash')
Copy-Item -LiteralPath (Join-Path $root 'docs\images\service-incident-flow.png') -Destination (Join-Path $release 'docs\images')
Copy-Item -LiteralPath (Join-Path $root 'docs\release-notes\v0.6.2.md') -Destination (Join-Path $release 'docs\release-notes')
$extensionFiles = @('manifest.json','service-worker.js','adapter-core.js','chatgpt-adapter.js','gemini-adapter.js','content-chatgpt.js','content-gemini.js','native-host-template.json','README.md')
foreach ($name in $extensionFiles) {
    Copy-Item -LiteralPath (Join-Path $root ('browser-extension\' + $name)) -Destination (Join-Path $release 'browser-extension')
}
$zip = $release + '.zip'
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -LiteralPath $release -DestinationPath $zip
$archiveHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
$checksumFile = $zip + '.sha256'
Set-Content -LiteralPath $checksumFile -Value ($archiveHash + '  ' + (Split-Path -Leaf $zip)) -Encoding ASCII
[pscustomobject]@{Release=$release;Archive=$zip;ChecksumFile=$checksumFile;Sha256=$archiveHash} | ConvertTo-Json -Compress
