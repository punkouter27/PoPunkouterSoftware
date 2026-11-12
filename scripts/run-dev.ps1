<#
.SYNOPSIS
    Runs the app locally the safe way: kills any stale instance first, pins the
    Development environment, and uses dotnet watch for hot reload.

.DESCRIPTION
    Fixes the three footguns that make a bare `dotnet run` hang or misbehave here:

    1. A previous PoPunkouterSoftware.Api.exe still running keeps port 8000 and the build
       output DLLs locked — the next build stalls on MSB3026 file-lock retries for ~30s
       and then fails, and a half-replaced wwwroot leaves the browser with WASM 404s /
       SRI integrity errors (the page hangs on the loading skeleton forever).
    2. `dotnet run --no-launch-profile` defaults to ASPNETCORE_ENVIRONMENT=Production,
       which silently attaches your local run to the PRODUCTION Key Vault, table
       storage endpoint, and Application Insights.
    3. Without the launch profile the app also binds :5000, colliding with whatever
       else is on the default Kestrel port instead of the expected :8000.

.EXAMPLE
    ./scripts/run-dev.ps1            # kill stale instance, watch-run on :8000
    ./scripts/run-dev.ps1 -NoWatch   # plain dotnet run instead of watch
#>
[CmdletBinding()]
param(
    [switch]$NoWatch,
    [int]$Port = 8000
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src/PoPunkouterSoftware.Api/PoPunkouterSoftware.Api.csproj'

# ── 1. Kill any stale app instance holding the port or the build outputs ─────
Get-Process -Name 'PoPunkouterSoftware.Api' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Stopping stale PoPunkouterSoftware.Api instance (PID $($_.Id))..." -ForegroundColor Yellow
    Stop-Process -Id $_.Id -Force
}
# Anything else squatting on the port is a hard error — don't silently fight it.
$owner = (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1).OwningProcess
if ($owner) {
    $name = (Get-Process -Id $owner -ErrorAction SilentlyContinue).ProcessName
    throw "Port $Port is in use by '$name' (PID $owner). Stop it first or pass -Port."
}

# ── 2. Pin the Development environment and the expected URL ──────────────────
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = "http://localhost:$Port"

# ── 3. Azurite must be up (AzureTableStorage uses UseDevelopmentStorage=true) ─
$azurite = Test-NetConnection -ComputerName 127.0.0.1 -Port 10002 -WarningAction SilentlyContinue
if (-not $azurite.TcpTestSucceeded) {
    Write-Host 'Azurite (table :10002) is not reachable — starting docker compose...' -ForegroundColor Yellow
    docker compose -f (Join-Path $repoRoot 'docker-compose.yml') up -d azurite
}

Write-Host "Starting PoPunkouterSoftware (Development) on http://localhost:$Port" -ForegroundColor Green
if ($NoWatch) {
    dotnet run --project $project
} else {
    dotnet watch --project $project run
}
