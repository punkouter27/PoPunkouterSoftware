<#
.SYNOPSIS
    Runs the E2E UI tier against the trimmed Release publish — the same output deploy.yml ships.

.DESCRIPTION
    1. dotnet publish the Api (and with it the trimmed WASM client) to a temp folder.
    2. Puts a stored Azure scan in the publish's App_Data (the repo's local copy, or the live one).
    3. Starts Azurite (docker compose) if :10002 is closed, then the published app on :8000 (Development, pinger
       and outside-service probes off, so no test traffic reaches the live Po apps).
    4. Runs the E2E tiers (-Tiers, default E2EUI) with BASE_URL, then stops the app and removes any Azurite it started.

.EXAMPLE
    pwsh ./scripts/run-e2eui.ps1
    pwsh ./scripts/run-e2eui.ps1 -Filter "FullyQualifiedName~Azure_"
    pwsh ./scripts/run-e2eui.ps1 -Tiers E2EAPI,E2EUI
#>
[CmdletBinding()]
param(
    [int]$Port = 8000,
    [string]$Filter = '',
    [string[]]$Tiers = @('E2EUI')
)

$ErrorActionPreference = 'Stop'
$Tiers = $Tiers | ForEach-Object { $_ -split ',' } | Where-Object { $_ }
$repoRoot = Split-Path -Parent $PSScriptRoot
$publish = Join-Path ([IO.Path]::GetTempPath()) 'popunkoutersoftware-e2eui'
$baseUrl = "http://localhost:$Port"

if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) {
    throw "Port $Port is already in use. Stop that process first."
}

Remove-Item -Recurse -Force $publish -ErrorAction SilentlyContinue
dotnet publish (Join-Path $repoRoot 'src/PoPunkouterSoftware.Api/PoPunkouterSoftware.Api.csproj') -c Release -o $publish
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

$scan = Join-Path $publish 'App_Data/azure-full-report.json'
New-Item -ItemType Directory -Force (Split-Path $scan) | Out-Null
$localScan = Join-Path $repoRoot 'src/PoPunkouterSoftware.Api/App_Data/azure-full-report.json'
if (Test-Path $localScan) { Copy-Item $localScan $scan }
else { Invoke-WebRequest 'https://app-popunkoutersoftware2.azurewebsites.net/api/diag/report' -OutFile $scan }

$startedAzurite = $false
if (-not (Test-NetConnection 127.0.0.1 -Port 10002 -WarningAction SilentlyContinue).TcpTestSucceeded) {
    docker compose -f (Join-Path $repoRoot 'docker-compose.yml') up -d azurite
    $startedAzurite = $true
}

$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = $baseUrl
# Tests never reach the live Po apps (F1 quotas) or outside services: no background pinger sweep of the other
# apps' /health/live, and /diag shows its outside-service rows as "Not probed in this environment."
$env:Pinger__Enabled = 'false'
$env:Diag__ProbeServices = 'false'
$app = Start-Process -FilePath (Join-Path $publish 'PoPunkouterSoftware.Api.exe') -WorkingDirectory $publish `
    -RedirectStandardOutput (Join-Path $publish 'app.out.log') -RedirectStandardError (Join-Path $publish 'app.err.log') -PassThru
try {
    $deadline = (Get-Date).AddSeconds(90)
    do {
        Start-Sleep -Seconds 2
        try { $ok = (Invoke-WebRequest "$baseUrl/health/live" -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200 } catch { $ok = $false }
    } until ($ok -or (Get-Date) -gt $deadline)
    if (-not $ok) { throw "The published app did not answer $baseUrl/health/live (see $publish\app.err.log)" }

    $env:BASE_URL = $baseUrl
    $code = 0
    foreach ($tier in $Tiers) {
        $testArgs = @('test', (Join-Path $repoRoot "tests/PoPunkouterSoftware.$tier"), '-c', 'Release')
        if ($Filter) { $testArgs += @('--filter', $Filter) }
        dotnet @testArgs
        if ($LASTEXITCODE -ne 0) { $code = $LASTEXITCODE }
    }
}
finally {
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    # Remove (not just stop) the Azurite container this run started; one someone else started is left alone.
    if ($startedAzurite) { docker compose -f (Join-Path $repoRoot 'docker-compose.yml') rm --stop --force azurite | Out-Null }
}
exit $code
