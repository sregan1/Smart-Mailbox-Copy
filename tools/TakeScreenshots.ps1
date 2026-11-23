<#
.SYNOPSIS
  Renders the wizard's screens to PNG files for the user guide, using the built-in demo mode.

.DESCRIPTION
  Demo mode (--demo) fills the wizard with fictional Contoso and Fabrikam data. It makes no network calls and never reads or
  writes the real saved settings, so this is safe to run anywhere. Each screen opens briefly on the desktop and closes itself.

  Build the app first (dotnet build SmartMailboxCopy.slnx -c Release). Output goes to docs\screenshots.
#>
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\src\SmartMailboxCopy.App\bin\Release\net10.0-windows\SmartMailboxCopyApp.exe'),
    [string]$OutDir = (Join-Path $PSScriptRoot '..\docs\screenshots')
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Exe)) { throw "Build the Release app first. Not found: $Exe" }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

# step, variant, file name
$shots = @(
    @('0', '',         '01-welcome'),
    @('1', '',         '02-source-tenant'),
    @('2', '',         '03-target-tenant'),
    @('3', '',         '04-choose-mailboxes'),
    @('4', '',         '05-match-mailboxes'),
    @('5', '',         '06-options'),
    @('5', 'filters',  '07-options-merge-and-limits'),
    @('5', 'calendar', '08-options-calendar-only'),
    @('6', '',         '09-review'),
    @('7', '',         '10-copy-progress'),
    @('8', '',         '11-results-needs-a-look'),
    @('8', 'clean',    '12-results-complete'),
    @('registrations', '', '13-app-registrations')
)

foreach ($s in $shots) {
    $path = Join-Path $OutDir ($s[2] + '.png')
    $argList = @('--demo', '--step', $s[0], '--screenshot', $path)
    if ($s[1] -ne '') { $argList += @('--variant', $s[1]) }
    Write-Host "Rendering $($s[2])..."
    Start-Process -FilePath $Exe -ArgumentList $argList -Wait
    if (-not (Test-Path $path)) { Write-Warning "No image was written for $($s[2])." }
}
Write-Host "Done. Images are in $OutDir"
