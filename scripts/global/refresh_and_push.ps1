<#
.SYNOPSIS
  Refreshes the GLOBAL snapshot the /global pages read and, unless -NoPush, commits and
  pushes it so the live site shows it.

.DESCRIPTION
  1. python scripts/global/build_global_data.py   (crawl of every repo, about 2 minutes)
  2. commits ONLY src/PoPunkouterSoftware.Api/App_Data/global when it changed
  3. git fetch, rebase onto origin/master, push   (the push deploys; about 5 minutes)

  It never touches other files: anything else that is modified or staged in the repo stays
  exactly as it is. It does nothing unless the repo is on master. Used by the 07:00
  scheduled task (register_daily_refresh.ps1), the local helper's Update button, the GPU
  queue and the benchmark runner. Log: scripts/global/.local/logs/refresh.log

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\global\refresh_and_push.ps1
  powershell -ExecutionPolicy Bypass -File scripts\global\refresh_and_push.ps1 -NoPush
  powershell -ExecutionPolicy Bypass -File scripts\global\refresh_and_push.ps1 -NoCrawl   # push config edits only
#>
param([switch]$NoPush, [switch]$NoCrawl)

# 'Continue': under Windows PowerShell 5.1 a native tool writing to stderr would otherwise stop
# the script; every step checks $LASTEXITCODE instead.
$ErrorActionPreference = 'Continue'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$workspace = Split-Path $repo -Parent
$snapshot = 'src/PoPunkouterSoftware.Api/App_Data/global'
$logDir = Join-Path $PSScriptRoot '.local\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = Join-Path $logDir 'refresh.log'

function Log($msg) {
    $line = "[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg
    Write-Host $line
    Add-Content -LiteralPath $log -Value $line
}

$python = Join-Path $workspace 'PoBox\.venv\Scripts\python.exe'
if (-not (Test-Path $python)) { $python = 'python' }

Log "Refresh started (push: $(-not $NoPush), crawl: $(-not $NoCrawl))"
if (-not $NoCrawl) {
    & $python (Join-Path $PSScriptRoot 'build_global_data.py') 2>&1 | ForEach-Object { Add-Content -LiteralPath $log -Value "  $_" }
    if ($LASTEXITCODE -ne 0) { Log "Crawl failed with exit code $LASTEXITCODE - nothing committed."; exit 1 }
    Log 'Crawl finished.'
}
if ($NoPush) { Log 'Done (no push requested).'; exit 0 }

$branch = (& git -C $repo rev-parse --abbrev-ref HEAD).Trim()
if ($branch -ne 'master') { Log "Repo is on '$branch', not master - snapshot left uncommitted."; exit 1 }

& git -C $repo add -- $snapshot
& git -C $repo diff --cached --quiet -- $snapshot
if ($LASTEXITCODE -eq 0) { Log 'Snapshot unchanged - nothing to commit.'; exit 0 }

$stamp = Get-Date -Format 'yyyy-MM-dd HH:mm'
& git -C $repo commit --quiet -m "Refresh GLOBAL snapshot $stamp" -- $snapshot
if ($LASTEXITCODE -ne 0) { Log 'Commit failed.'; exit 1 }

& git -C $repo fetch --quiet origin
& git -C $repo rebase --autostash origin/master 2>&1 | ForEach-Object { Add-Content -LiteralPath $log -Value "  $_" }
if ($LASTEXITCODE -ne 0) {
    & git -C $repo rebase --abort 2>$null
    Log 'Rebase onto origin/master failed - the snapshot commit is local only. Resolve and push by hand.'
    exit 1
}
& git -C $repo push --quiet origin master 2>&1 | ForEach-Object { Add-Content -LiteralPath $log -Value "  $_" }
if ($LASTEXITCODE -ne 0) { Log 'Push failed - the snapshot commit is local only.'; exit 1 }
Log 'Pushed. The site redeploys in about 5 minutes.'
