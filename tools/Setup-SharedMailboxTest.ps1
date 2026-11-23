<#
.SYNOPSIS
  Creates a TEST shared mailbox in one Microsoft 365 tenant and gives a test user Full Access to it.
  Developer tool for the migration tests; not part of the product.

.DESCRIPTION
  Run it once in the SOURCE tenant and once in the TARGET tenant, with the SAME -Alias in both, so that a domain swap
  (alias@source-domain to alias@target-domain) matches them with no editing.

  It needs the ExchangeOnlineManagement module and an Exchange administrator of that tenant. It opens the usual Microsoft sign-in
  (you must be at the keyboard). It shows what it will do and asks you to type YES. Nothing is deleted. A shared mailbox needs no license.

  After it runs in the SOURCE tenant, put some mail in the shared mailbox, for example with the seeder:
    SmartMailboxCopySeed seed --mailbox <alias@source-domain> --confirm-test-mailbox <alias@source-domain> --wellknown --size small

.PARAMETER AdminUpn
  An Exchange administrator of the tenant, used to sign in.
.PARAMETER Alias
  The part before the @ of the shared mailbox (default smartcopytest).
.PARAMETER DisplayName
  The mailbox's display name.
.PARAMETER GrantTo
  The test user who gets Full Access (with auto-mapping, so it appears in their Outlook). In the source tenant this should be the seed account.
#>
param(
    [Parameter(Mandatory)][string]$AdminUpn,
    [string]$Alias = 'smartcopytest',
    [string]$DisplayName = 'SmartCopy Test Shared',
    [Parameter(Mandatory)][string]$GrantTo
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Module -ListAvailable -Name ExchangeOnlineManagement)) {
    throw "The ExchangeOnlineManagement module is not installed. Run:  Install-Module ExchangeOnlineManagement -Scope CurrentUser"
}
Import-Module ExchangeOnlineManagement

Write-Host ''
Write-Host 'This will, in the tenant you sign in to:' -ForegroundColor Cyan
Write-Host "  1. create the shared mailbox '$DisplayName' (alias '$Alias') if it does not already exist (no license needed)"
Write-Host "  2. give $GrantTo Full Access to it, with auto-mapping"
Write-Host 'Nothing is deleted or changed on any other mailbox.'
if ((Read-Host 'Type YES to continue') -ne 'YES') { Write-Host 'Cancelled.'; exit 1 }

Connect-ExchangeOnline -UserPrincipalName $AdminUpn -ShowBanner:$false
try {
    $existing = Get-Mailbox -Identity $Alias -ErrorAction SilentlyContinue
    if ($existing) {
        if ($existing.RecipientTypeDetails -ne 'SharedMailbox') { throw "A mailbox with alias '$Alias' already exists but it is a $($existing.RecipientTypeDetails), not a shared mailbox. Choose another -Alias." }
        Write-Host "The shared mailbox already exists: $($existing.PrimarySmtpAddress)" -ForegroundColor Yellow
        $address = $existing.PrimarySmtpAddress
    }
    else {
        $created = New-Mailbox -Shared -Name $DisplayName -DisplayName $DisplayName -Alias $Alias
        $address = $created.PrimarySmtpAddress
        Write-Host "Created the shared mailbox: $address" -ForegroundColor Green
    }

    Add-MailboxPermission -Identity $Alias -User $GrantTo -AccessRights FullAccess -AutoMapping $true -Confirm:$false | Out-Null
    Write-Host "$GrantTo now has Full Access to $address (it can take a few minutes to show in Outlook)." -ForegroundColor Green
    Write-Host ''
    Write-Host "Shared mailbox address: $address"
}
finally {
    Disconnect-ExchangeOnline -Confirm:$false | Out-Null
}
