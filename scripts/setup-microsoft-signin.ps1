#requires -Version 7
<#
.SYNOPSIS
  One-time setup of Microsoft sign-in for the owner-only pages (/azure, /users, /global).

.DESCRIPTION
  Creates (or reuses) the Entra app registration "PoPunkouterSoftware sign-in", limited to
  this directory, makes a client secret, and stores the four settings the site reads in the
  shared Key Vault:

    PoPunkouterSoftware--MicrosoftAuth--ClientId
    PoPunkouterSoftware--MicrosoftAuth--ClientSecret
    PoPunkouterSoftware--MicrosoftAuth--TenantId
    PoPunkouterSoftware--MicrosoftAuth--OwnerEmail

  Only the OwnerEmail account can complete sign-in (src/PoPunkouterSoftware.Api/Host/OwnerAccess.cs).
  The secret is never printed. Run again to rotate the secret. Needs `az login` as the owner.

.EXAMPLE
  pwsh ./scripts/setup-microsoft-signin.ps1 -OwnerEmail you@example.com
#>
param(
    [Parameter(Mandatory)] [string] $OwnerEmail,
    [string] $VaultName = 'kv-poshared2',
    [string] $SiteUrl = 'https://app-popunkoutersoftware2.azurewebsites.net',
    [string] $SiteName = 'app-popunkoutersoftware2',
    [string] $ResourceGroup = 'PoShared',
    [string] $DisplayName = 'PoPunkouterSoftware sign-in'
)

$ErrorActionPreference = 'Stop'

function Invoke-Az {
    $out = az @args
    if ($LASTEXITCODE -ne 0) { throw "az $($args -join ' ') failed" }
    $out
}

$tenant = Invoke-Az account show --query tenantId -o tsv
Write-Host "Tenant: $tenant"

$appId = Invoke-Az ad app list --display-name $DisplayName --query '[0].appId' -o tsv
if (-not $appId) {
    $appId = Invoke-Az ad app create --display-name $DisplayName --sign-in-audience AzureADMyOrg `
        --web-redirect-uris "$SiteUrl/signin-oidc" 'http://localhost:8000/signin-oidc' --query appId -o tsv
    Write-Host "Created app registration $appId"
}
else {
    Write-Host "Reusing app registration $appId"
}

$spId = az ad sp show --id $appId --query id -o tsv 2>$null
if (-not $spId) { Invoke-Az ad sp create --id $appId --query id -o tsv | Out-Null }

$secret = Invoke-Az ad app credential reset --id $appId --display-name 'site' --years 2 --query password -o tsv
if (-not $secret) { throw 'No client secret was returned.' }

$prefix = 'PoPunkouterSoftware--MicrosoftAuth'
$values = [ordered]@{
    ClientId     = $appId
    ClientSecret = $secret
    TenantId     = $tenant
    OwnerEmail   = $OwnerEmail
}
foreach ($key in $values.Keys) {
    Invoke-Az keyvault secret set --vault-name $VaultName --name "$prefix--$key" --value $values[$key] --query name -o tsv | Out-Null
    Write-Host "Stored $prefix--$key"
}

# The site reads the vault at startup.
Invoke-Az webapp restart --name $SiteName --resource-group $ResourceGroup | Out-Null
Write-Host "Restarted $SiteName. Open $SiteUrl and use 'Sign in with Microsoft'."
