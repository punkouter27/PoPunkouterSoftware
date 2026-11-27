<#
.SYNOPSIS
  Runs an overnight training queue made on the site's /global/queue page (through the local
  helper, scripts/global/local_helper.py).

.DESCRIPTION
  Jobs run one after another. Before each GPU job it waits until the GPU is at or below
  CoolTemp (nvidia-smi), up to CoolMaxWaitMin. Each job gets its hours; a self-limiting job
  (its own --hours flag) gets 30 min of grace, any other job is stopped at the limit
  (stop_file first, then the process tree is killed). The PC is kept awake while it runs.
  After every job it writes .local/runs/<stamp>/summary.json + summary.md (new .onnx files,
  exit code, duration), so a crash still leaves a report. Ends with refresh_and_push.ps1 when
  refresh_after is true, so the site's briefing shows the night.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\global\run_gpu_queue.ps1 -QueueFile scripts\global\.local\queues\tonight.json
  powershell -ExecutionPolicy Bypass -File scripts\global\run_gpu_queue.ps1 -QueueFile ... -DryRun
#>
param(
    [Parameter(Mandatory = $true)][string]$QueueFile,
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
$QueueDir = Join-Path $PSScriptRoot '.local'
$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Root = Split-Path $Repo -Parent          # the workspace that holds every Po repo
$q = Get-Content -Raw -LiteralPath $QueueFile | ConvertFrom-Json

$coolTemp = if ($q.cool_temp) { [int]$q.cool_temp } else { 70 }
$coolMax = if ($q.cool_max_wait_min) { [int]$q.cool_max_wait_min } else { 20 }
$gapMin = if ($null -ne $q.gap_min) { [int]$q.gap_min } else { 5 }

$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$runDir = Join-Path $QueueDir "runs\$stamp"
New-Item -ItemType Directory -Force -Path $runDir | Out-Null
Copy-Item -LiteralPath $QueueFile -Destination (Join-Path $runDir 'queue.json')

Add-Type -Namespace Po -Name Power -MemberDefinition '[DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint f);'
function Stay-Awake { [void][Po.Power]::SetThreadExecutionState([uint32]'0x80000001') }   # ES_CONTINUOUS|ES_SYSTEM_REQUIRED
function Allow-Sleep { [void][Po.Power]::SetThreadExecutionState([uint32]'0x80000000') }

function Get-GpuTemp {
    try { return [int]((& nvidia-smi --query-gpu=temperature.gpu --format=csv,noheader,nounits) | Select-Object -First 1) } catch { return $null }
}
function Log($msg) {
    $line = "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $msg
    Write-Host $line
    Add-Content -LiteralPath (Join-Path $runDir 'queue.log') -Value $line
}
$skip = '\\(\.venv|venv|Library|Intermediate|DerivedDataCache|Saved|node_modules|\.git)\\'
function Get-NewOnnx($repoPath, [datetime]$since) {
    Get-ChildItem -LiteralPath $repoPath -Recurse -Filter *.onnx -File -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -gt $since -and $_.FullName -notmatch $skip } |
        ForEach-Object { $_.FullName.Substring($Root.Length + 1).Replace('\', '/') }
}

$results = @()
function Write-Summary {
    $obj = [ordered]@{ stamp = $stamp; queue = (Split-Path $QueueFile -Leaf); cool_temp = $coolTemp; jobs = $results }
    $obj | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runDir 'summary.json') -Encoding UTF8
    $md = @("# Overnight queue $stamp", "", "| Job | Repo | Outcome | Ran | New brains |", "|---|---|---|---|---|")
    foreach ($r in $results) { $md += "| $($r.id) | $($r.repo) | $($r.outcome) | $($r.minutes) min | $(@($r.new_onnx).Count) |" }
    $md += ""; foreach ($r in $results) { if (@($r.new_onnx).Count) { $md += "**$($r.id)**: " + ((@($r.new_onnx) | Select-Object -First 12) -join ', ') } }
    $md -join "`n" | Set-Content -LiteralPath (Join-Path $runDir 'summary.md') -Encoding UTF8
}

Log "Queue $stamp - $(@($q.jobs).Count) job(s), cool to ${coolTemp}C, gap ${gapMin} min$(if ($DryRun) { ' [DRY RUN]' })"
Stay-Awake
try {
    $i = 0
    foreach ($job in $q.jobs) {
        $i++
        $hours = [double]$job.hours
        $cwd = Join-Path $Root ($job.cwd -replace '/', '\')
        $cmd = ($job.command -replace '\{hours\}', [string]$hours)
        $logFile = Join-Path $runDir ("{0:D2}_{1}.log" -f $i, $job.id)
        $rec = [ordered]@{ id = $job.id; repo = $job.repo; command = $cmd; cwd = $job.cwd; hours = $hours;
                           started = $null; ended = $null; minutes = 0; exit_code = $null; outcome = 'pending'; new_onnx = @(); log = (Split-Path $logFile -Leaf) }
        $missing = @($job.requires | Where-Object { $_ -and -not (Test-Path (Join-Path $Root ($_ -replace '/', '\'))) })
        if ($missing.Count) { $rec.outcome = "blocked: missing $($missing -join ', ')"; $results += $rec; Log "SKIP $($job.id): $($rec.outcome)"; Write-Summary; continue }
        if (-not (Test-Path $cwd)) { $rec.outcome = "blocked: cwd $($job.cwd) missing"; $results += $rec; Log "SKIP $($job.id)"; Write-Summary; continue }

        if ($job.device -ne 'cpu') {
            $waited = 0
            while ($true) {
                $t = Get-GpuTemp
                if ($null -eq $t -or $t -le $coolTemp -or $waited -ge $coolMax) { break }
                if ($waited -eq 0) { Log "GPU at ${t}C - cooling to ${coolTemp}C (max $coolMax min)" }
                Start-Sleep -Seconds 60; $waited++
            }
        }
        Log "START $($job.id) ($hours h) in $($job.cwd): $cmd"
        if ($DryRun) { $rec.outcome = 'dry-run'; $results += $rec; Write-Summary; continue }

        $start = Get-Date
        $rec.started = $start.ToString('s')
        $script = "Set-Location -LiteralPath '$($cwd -replace "'", "''")'; $cmd *>&1 | Out-File -LiteralPath '$($logFile -replace "'", "''")' -Encoding utf8; exit `$LASTEXITCODE"
        $enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
        $p = Start-Process -FilePath powershell.exe -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $enc -WindowStyle Minimized -PassThru
        $limitMs = [int](($hours * 3600 + $(if ($job.self_limiting) { 1800 } else { 0 })) * 1000)
        if (-not $p.WaitForExit($limitMs)) {
            $createdStop = $false
            if ($job.stop_file) {
                $sf = Join-Path $Root ($job.stop_file -replace '/', '\')
                if (-not (Test-Path $sf)) { New-Item -ItemType File -Path $sf | Out-Null; $createdStop = $true }
                Log "Time limit: wrote stop file $($job.stop_file), waiting up to 20 min for a clean stop"
                [void]$p.WaitForExit(20 * 60 * 1000)
            }
            if (-not $p.HasExited) { Log "Time limit: stopping process tree $($p.Id)"; & taskkill /T /F /PID $p.Id | Out-Null; $rec.outcome = 'stopped at time limit' }
            else { $rec.outcome = 'stopped via stop file' }
            if ($createdStop -and (Test-Path $sf)) { Remove-Item -LiteralPath $sf -Force }
        } else {
            $rec.exit_code = $p.ExitCode
            $rec.outcome = if ($p.ExitCode -eq 0) { 'finished' } else { "exited with code $($p.ExitCode)" }
        }
        $end = Get-Date
        $rec.ended = $end.ToString('s'); $rec.minutes = [int]($end - $start).TotalMinutes
        $rec.new_onnx = @(Get-NewOnnx (Join-Path $Root $job.repo) $start)
        $results += $rec
        Log "END $($job.id): $($rec.outcome) after $($rec.minutes) min, $(@($rec.new_onnx).Count) new .onnx"
        Write-Summary
        if ($i -lt @($q.jobs).Count -and $gapMin -gt 0) { Start-Sleep -Seconds ($gapMin * 60) }
    }
} finally {
    Allow-Sleep
    Write-Summary
}
if ($q.refresh_after -and -not $DryRun) {
    Log 'Refreshing the site snapshot (refresh_and_push.ps1)'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'refresh_and_push.ps1') | Out-Null
}
Log "Done. Report: $runDir\summary.md"
