$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $root 'bin'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
$refs = @('/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Net.Http.dll', '/reference:System.Runtime.Serialization.dll', '/reference:System.Web.Extensions.dll', '/reference:System.Windows.Forms.dll')
$allSources = @(Get-ChildItem (Join-Path $root 'src\*.cs') -ErrorAction SilentlyContinue | ForEach-Object FullName)
$legacyPolicyFiles = @(
    'AutomaticDecisionStateMachine.cs',
    'ConnectionAssurance.cs',
    'FailoverController.cs',
    'OpportunityCandidatePlanner.cs',
    'OpportunityOptimization.cs',
    'RankedOpportunitySelector.cs',
    'ServiceEvidencePolicy.cs',
    'ServiceIncidentPolicy.cs',
    'StartupRecovery.cs',
    'TrafficGuard.cs'
)
$sources = @($allSources | Where-Object { $legacyPolicyFiles -notcontains (Split-Path -Leaf $_) })
$testSources = @($allSources | Where-Object {
    (Split-Path -Leaf $_) -ne 'RuntimeConnectionAssurance.cs' -and
    (Split-Path -Leaf $_) -ne 'RuntimePrimitives.cs' -and
    (Split-Path -Leaf $_) -ne 'RuntimeCandidateDelayMeasurement.cs'
})
$manifest = Join-Path $root 'assets\ClashCompatibilityMonitor.manifest'
$icon = Join-Path $root 'assets\ClashCompatibilityMonitor.ico'
$testInputs = @($testSources) + @(Get-ChildItem (Join-Path $root 'tests\*.cs') | ForEach-Object FullName)
& $csc /nologo /warnaserror /platform:x86 /target:exe /main:Tests /out:"$out\Monitor.Tests.exe" @refs @testInputs
if ($LASTEXITCODE) { exit $LASTEXITCODE }
& "$out\Monitor.Tests.exe"
if ($LASTEXITCODE) { exit $LASTEXITCODE }
& $csc /nologo /warnaserror /platform:x86 /target:winexe /main:Program /win32manifest:"$manifest" /win32icon:"$icon" /out:"$out\ClashCompatibilityMonitor.exe" @refs @sources
exit $LASTEXITCODE
