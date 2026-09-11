param(
    [string]$ClashDirectory = (Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'io.github.clash-verge-rev.clash-verge-rev'),
    [string]$OutputPath,
    [switch]$AsObject
)
$ErrorActionPreference = 'Stop'
$configPath = Join-Path $ClashDirectory 'clash-verge.yaml'
$configExists = Test-Path -LiteralPath $configPath -PathType Leaf
$profilesExist = Test-Path -LiteralPath (Join-Path $ClashDirectory 'profiles.yaml') -PathType Leaf
$readable = $false
$probeMarker = $false
$portMarker = $false
if ($configExists) {
    try {
        if ((Get-Item -LiteralPath $configPath).Length -le 10MB) {
            $config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8
            $readable = $true
            $probeMarker = $config.Contains('compatibility-probe')
            $portMarker = $config -match '(?m)^\s*port:\s*7896\s*$'
        }
    } catch { $readable = $false }
}
$status = if (!$configExists -or !$profilesExist) { 'CLASH_CONFIG_MISSING' }
    elseif (!$readable) { 'CONFIG_UNREADABLE' }
    elseif (!$probeMarker -or !$portMarker) { 'ENHANCEMENT_MISSING' }
    else { 'CONFIG_READY' }
$nextStep = switch ($status) {
    'CLASH_CONFIG_MISSING' { 'Start Clash Verge Rev, import your subscription, and apply it once.' }
    'CONFIG_UNREADABLE' { 'Check local file permissions and configuration size; do not paste your configuration into an issue.' }
    'ENHANCEMENT_MISSING' { 'Enable the included clash/enhancement.js in Clash Verge Rev, apply the configuration, then run this check again.' }
    default { 'Configuration markers found. Install performs a separate core connection self-test; website access is not verified by this report.' }
}
$report = [pscustomobject][ordered]@{
    SchemaVersion = 1
    CapturedUtc = [DateTime]::UtcNow.ToString('o')
    Status = $status
    ConfigExists = [bool]$configExists
    ProfilesExist = [bool]$profilesExist
    ConfigReadable = $readable
    ProbeMarkerFound = $probeMarker
    ProbePortMarkerFound = [bool]$portMarker
    PowerShellMajor = $PSVersionTable.PSVersion.Major
    WindowsVersion = [Environment]::OSVersion.Version.ToString()
    NextStep = $nextStep
}
if ($OutputPath) {
    $report | ConvertTo-Json | Set-Content -LiteralPath $OutputPath -Encoding UTF8
}
if ($AsObject) { $report } else { $report | ConvertTo-Json }
