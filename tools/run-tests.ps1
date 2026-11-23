<#
.SYNOPSIS
  Automated end-to-end test: seed a TEST source mailbox, copy the whole tree to the target, verify, report PASS or FAIL.

.DESCRIPTION
  Steps (each is a LIVE action; the script asks once at the start, and you must be at the keyboard for the browser sign-ins):
    1. Build.
    2. Seed   (sign in as the seed account)        writes synthetic mail into the TEST source mailbox only.
    3. Copy   (sign in as source, then as target)   copy-tree into a new, timestamped container folder, then
                                                      reconcile counts and compare every message field by field.
    4. Cleanup (optional, -Cleanup; one more sign-in as the seed account) deletes the ZZ-Seed-* folders from the source.
  The script never reads or prints mail content. The target container is NOT deleted: the tool has no delete on the target by design.
  Delete the ZZ-Test-* folder in the target mailbox yourself when you are done.

.PARAMETER Size
  small (about 110 messages) or volume (10,000 messages and three 25 MB attachments).
.PARAMETER Cleanup
  Remove the seeded ZZ-Seed-* folders from the source after the copy.
.PARAMETER Yes
  Skip the confirmation prompt (use only when you are sure).
#>
param(
    [ValidateSet('small', 'volume')][string]$Size = 'small',
    [switch]$Cleanup,
    [switch]$Merge,          # M2: merge into the target's REAL Inbox/Sent/etc. instead of a ZZ-Test container (adds mail that cannot be removed by the tool)
    [switch]$AllowNonEmpty,  # with -Merge: add alongside mail already in the target's folders
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
$cfgPath = Join-Path $repo 'smartmailboxcopy.config.json'
if (-not (Test-Path $cfgPath)) { throw "smartmailboxcopy.config.json not found in $repo" }
$cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json

$seedUpn = $cfg.seedUpn
if ([string]::IsNullOrWhiteSpace($seedUpn) -or $seedUpn.StartsWith('<')) { throw 'seedUpn is not set in the config.' }
if ($cfg.targetUpn -eq $seedUpn) { throw 'The seed account is the target account. Refusing.' }
if (@($cfg.protectedMailboxes) -contains $seedUpn -or @($cfg.protectedMailboxes) -contains $cfg.targetUpn) { throw 'A protected mailbox is configured as the seed or target account. Refusing.' }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$container = "ZZ-Test-$stamp"
$outDir = Join-Path $env:LOCALAPPDATA 'SmartMailboxCopy\test-runs'
New-Item -ItemType Directory -Force $outDir | Out-Null
$transcript = Join-Path $outDir "test-$stamp.txt"

Write-Host ''
Write-Host 'This test will:' -ForegroundColor Cyan
Write-Host "  1. WRITE $(if ($Size -eq 'volume') { 'about 10,000' } else { 'about 110' }) synthetic messages into the TEST mailbox $seedUpn (sign-in 1)"
if ($Merge) { Write-Host "  2. MERGE that mailbox's whole mail tree into the REAL Inbox, Sent Items, Drafts etc. of $($cfg.targetUpn) (sign-ins 2 and 3). The tool cannot remove what it adds." }
else { Write-Host "  2. COPY that mailbox's whole mail tree into a new folder '$container' in $($cfg.targetUpn) (sign-ins 2 and 3)" }
Write-Host '  3. Verify counts and every message, field by field'
if ($Cleanup) { Write-Host "  4. DELETE the ZZ-Seed-* folders from $seedUpn (sign-in 4)" }
Write-Host "The source is only read during the copy. Your protected mailbox is never used."
if (-not $Yes) { if ((Read-Host 'Type YES to start') -ne 'YES') { Write-Host 'Cancelled.'; exit 1 } }

function Run-Step([string]$Name, [string]$Exe, [string[]]$StepArgs) {
    Write-Host "`n##### $Name" -ForegroundColor Cyan
    $lines = New-Object System.Collections.Generic.List[string]
    & $Exe @StepArgs 2>&1 | ForEach-Object { $s = "$_"; $lines.Add($s); Write-Host $s }
    $code = $LASTEXITCODE
    Add-Content -Path $transcript -Value "##### $Name (exit code $code)"
    Add-Content -Path $transcript -Value $lines
    [pscustomobject]@{ Code = $code; Text = ($lines -join "`n") }
}

$results = [ordered]@{}
$failures = New-Object System.Collections.Generic.List[string]
function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    $results[$Name] = $Ok
    if (-not $Ok) { $failures.Add("$Name $Detail".Trim()) }
    Write-Host ("  [{0}] {1} {2}" -f $(if ($Ok) { 'PASS' } else { 'FAIL' }), $Name, $Detail) -ForegroundColor $(if ($Ok) { 'Green' } else { 'Red' })
}

# ---- 1. build ----
Write-Host "`n##### Build" -ForegroundColor Cyan
dotnet build SmartMailboxCopy.slnx -v q --nologo | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$cli = Join-Path $repo 'src\SmartMailboxCopy.Cli\bin\Debug\net8.0\SmartMailboxCopy.dll'
$seed = Join-Path $repo 'tools\SmartMailboxCopy.Seed\bin\Debug\net8.0\SmartMailboxCopySeed.dll'

# ---- 2. seed ----
$s = Run-Step 'Seed the test source mailbox' 'dotnet' @($seed, 'seed', '--confirm-test-mailbox', $seedUpn, '--size', $Size)
Check 'Seed finished' ($s.Code -eq 0)
$seedFolder = if ($s.Text -match 'Seeding (ZZ-Seed-[0-9-]+)') { $Matches[1] } else { '' }
Check 'Seed created messages with no failures' ($s.Text -match 'Done\. (\d+) messages created, 0 bulk failures') ($(if ($s.Text -match 'Done\. (\d+) messages created') { "($($Matches[1]) messages)" } else { '' }))

# ---- 3. copy ----
if ($failures.Count -eq 0) {
    $copyArgs = @($cli, 'copy-tree', '--max', '20000')
    if ($Merge) { $copyArgs += '--merge-wellknown'; if ($AllowNonEmpty) { $copyArgs += '--allow-nonempty' } } else { $copyArgs += @('--target-container', $container) }
    $c = Run-Step "$(if ($Merge) { 'Merge the whole tree into the real folders' } else { "Copy the whole tree into '$container'" }) and verify" 'dotnet' $copyArgs
    Check 'Copy exited cleanly (exit code 0)' ($c.Code -eq 0) "(exit code $($c.Code))"
    Check 'No item failed to import' ($c.Text -match 'failed 0\.' -or $c.Text -match 'Imported \d+, failed 0')
    Check 'Every folder count matches' ($c.Text -match "Every folder's item count matches" -or $c.Text -match 'Every folder holds at least its source')
    $m = [regex]::Match($c.Text, '(\d+) of (\d+) items identical on every compared field; (\d+) could not be paired')
    Check 'Every message identical field by field' ($m.Success -and $m.Groups[1].Value -eq $m.Groups[2].Value -and [int]$m.Groups[2].Value -gt 0 -and $m.Groups[3].Value -eq '0') $(if ($m.Success) { "($($m.Groups[1].Value) of $($m.Groups[2].Value) identical, $($m.Groups[3].Value) unpaired)" } else { '(verification did not run)' })
    $tot = [regex]::Match($c.Text, '(\d+) folders to copy, \d+ skipped, (\d+) items in total')
    if ($tot.Success) { Write-Host "  Copied tree: $($tot.Groups[1].Value) folders, $($tot.Groups[2].Value) items." }
    $ver = [regex]::Match($c.Text, 'Fields that differed, by number of items: (.+)')
    if ($ver.Success) { Write-Host "  Fields that differed: $($ver.Groups[1].Value)" }
}

# ---- 4. cleanup ----
if ($Cleanup) {
    $k = Run-Step 'Clean up the seeded folders in the source' 'dotnet' @($seed, 'cleanup', '--confirm-test-mailbox', $seedUpn)
    Check 'Cleanup finished' ($k.Code -eq 0)
}

# ---- verdict ----
Write-Host ''
$pass = $failures.Count -eq 0
Write-Host ("RESULT: {0}  ({1} of {2} checks passed)" -f $(if ($pass) { 'PASS' } else { 'FAIL' }), @($results.Values | Where-Object { $_ }).Count, $results.Count) -ForegroundColor $(if ($pass) { 'Green' } else { 'Red' })
if (-not $pass) { $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red } }
Write-Host "Transcript: $transcript"
Write-Host "Logs:       $env:LOCALAPPDATA\SmartMailboxCopy\logs   Reports: $env:LOCALAPPDATA\SmartMailboxCopy\reports"
if (-not $Merge) { Write-Host "The target container '$container' remains in $($cfg.targetUpn); delete it in Outlook when finished." }
else { Write-Host "Mail was added to the real folders of $($cfg.targetUpn); remove it in Outlook if you do not want it." }
if (-not $Cleanup -and $seedFolder) { Write-Host "The seeded folder '$seedFolder' remains in the source; run with -Cleanup or the seeder's cleanup command to remove it." }
exit $(if ($pass) { 0 } else { 1 })
