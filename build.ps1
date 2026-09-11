$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $root 'bin'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
$refs = @('/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Net.Http.dll', '/reference:System.Runtime.Serialization.dll', '/reference:System.Web.Extensions.dll', '/reference:System.Windows.Forms.dll')
$sources = @(Get-ChildItem (Join-Path $root 'src\*.cs') -ErrorAction SilentlyContinue | ForEach-Object FullName)
$manifest = Join-Path $root 'assets\ClashCompatibilityMonitor.manifest'
$icon = Join-Path $root 'assets\ClashCompatibilityMonitor.ico'
$testInputs = @($sources) + @((Join-Path $root 'tests\Tests.cs'))
& $csc /nologo /warnaserror /platform:x86 /target:exe /main:Tests /out:"$out\Monitor.Tests.exe" @refs @testInputs
if ($LASTEXITCODE) { exit $LASTEXITCODE }
& "$out\Monitor.Tests.exe"
if ($LASTEXITCODE) { exit $LASTEXITCODE }
& $csc /nologo /warnaserror /platform:x86 /target:winexe /main:Program /win32manifest:"$manifest" /win32icon:"$icon" /out:"$out\ClashCompatibilityMonitor.exe" @refs @sources
exit $LASTEXITCODE
