param([string]$SourceExe)
$ErrorActionPreference = 'Stop'
$installer = Join-Path $PSScriptRoot 'install.ps1'
if ([string]::IsNullOrWhiteSpace($SourceExe)) {
    & $installer
} else {
    & $installer -SourceExe $SourceExe
}
