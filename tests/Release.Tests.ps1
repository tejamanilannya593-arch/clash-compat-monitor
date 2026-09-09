$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$required = @(
    'README.md', 'LICENSE', 'package-release.ps1',
    'scripts\install.ps1', 'scripts\upgrade.ps1', 'scripts\uninstall.ps1'
)
foreach ($relative in $required) {
    $path = Join-Path $root $relative
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing release file: $relative" }
}
$packageScript = Get-Content -LiteralPath (Join-Path $root 'package-release.ps1') -Raw
$installScript = Get-Content -LiteralPath (Join-Path $root 'scripts\install.ps1') -Raw
$readme = Get-Content -LiteralPath (Join-Path $root 'README.md') -Raw -Encoding UTF8
$program = Get-Content -LiteralPath (Join-Path $root 'src\Program.cs') -Raw
$trayHost = Get-Content -LiteralPath (Join-Path $root 'src\TrayHost.cs') -Raw
if (!$packageScript.Contains('ClashCompatibilityMonitor-v0.2.0')) { throw 'Release package version is not v0.2.0.' }
if (!$installScript.Contains("Version='0.2.0'")) { throw 'Installer status version is not v0.2.0.' }
if (!$installScript.Contains('EndsWith($monitorSuffix')) { throw 'Installer does not stop virtualized monitor paths.' }
if (!$readme.Contains('v0.2.0') -or !$readme.Contains('HTTP') -or !$readme.Contains('preferences.state') -or
    !$program.Contains('InstanceActivation.TryOwn') -or !$trayHost.Contains('NotifyIcon')) {
    throw 'README does not describe v0.2.0 tray and intent behavior.'
}
$scriptFiles = Get-ChildItem (Join-Path $root 'scripts') -Filter '*.ps1' -File
foreach ($scriptFile in $scriptFiles) {
    $tokens = $null
    $parseErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($scriptFile.FullName, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) { throw "PowerShell parse failure: $($scriptFile.Name)" }
}
$published = @($required | ForEach-Object { Join-Path $root $_ }) +
    @(Get-ChildItem (Join-Path $root 'src') -File | ForEach-Object FullName) +
    @(Get-ChildItem (Join-Path $root 'clash') -File | ForEach-Object FullName)
$forbidden = @([Environment]::GetFolderPath('UserProfile'), ('set-your-' + 'secret'))
foreach ($value in $forbidden) {
    if (Select-String -LiteralPath $published -SimpleMatch $value -Quiet) {
        throw 'Release sources contain machine-specific values.'
    }
}
$forbiddenPatterns = @(
    '(?i)^\s*secret:\s+["'']?[^"''\s]+',
    '(?i)https?://[^\s]+(?:token|key|subscription)='
)
foreach ($pattern in $forbiddenPatterns) {
    if (Select-String -LiteralPath $published -Pattern $pattern -Quiet) {
        throw 'Release sources contain a credential-like value.'
    }
}
Write-Output 'PASS release files exist and contain no machine-specific values'
