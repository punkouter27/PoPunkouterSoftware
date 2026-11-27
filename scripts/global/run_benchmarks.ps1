<#
 Runs each RL project's own benchmark (<repo>\benchmark.ps1 -Run), one after another, so the
 site's /global/rl page gets fresh speed numbers. Each result is recorded by the repo in
 <repo>\benchmark\history.json and picked up by the next crawl (refresh_and_push.ps1).

   run_benchmarks.ps1                         all 10 RL projects
   run_benchmarks.ps1 -Repos PoBox,PoSoccer   only those
   run_benchmarks.ps1 -TimeoutMin 30          allow longer per project (default 20; first Unity run builds a player)
   run_benchmarks.ps1 -NoRefresh              skip the crawl and push at the end
#>
param(
    [string[]]$Repos,
    [int]$TimeoutMin = 20,
    [switch]$NoRefresh
)
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$root = Split-Path $repo -Parent          # the workspace that holds every Po repo
# -File passes "A,B" as one string; split it so both -Repos A,B and -Repos A B work.
if ($Repos) { $Repos = @($Repos | ForEach-Object { $_ -split ',' } | Where-Object { $_ }) }
$all = 'PoBasketball','PoBox','PoFootball','PoHillKing','PoOlympic','PoRaceUnreal','PoRobotTrain','PoSlapUnreal','PoSoccer','PoSumo'
if (-not $Repos) { $Repos = $all }
$stamp = Get-Date -Format 'yyyy-MM-dd_HHmm'
$logDir = Join-Path $PSScriptRoot ".local\benchmarks\$stamp"
New-Item -ItemType Directory -Force $logDir | Out-Null
$results = @()

Write-Host ""
Write-Host "=== Benchmarking $($Repos.Count) project(s), one at a time ===" -ForegroundColor Yellow
Write-Host "Tip: close Unity/Unreal editors and games first - they use the GPU and skew the numbers." -ForegroundColor DarkGray

$i = 0
foreach ($r in $Repos) {
    $i++
    $script = Join-Path $root "$r\benchmark.ps1"
    if (-not (Test-Path $script)) { Write-Host "[$i/$($Repos.Count)] $r  SKIP (no benchmark.ps1)"; $results += [pscustomobject]@{Repo=$r; Result='skipped'; Seconds=0; Detail='no benchmark.ps1'}; continue }
    Write-Host "[$i/$($Repos.Count)] $r ..." -NoNewline
    $out = Join-Path $logDir "$r.log"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process powershell -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$script`"",'-Run' `
         -WorkingDirectory (Join-Path $root $r) -PassThru -WindowStyle Hidden -RedirectStandardOutput $out -RedirectStandardError "$out.err"
    if (-not $p.WaitForExit($TimeoutMin * 60000)) {
        taskkill /PID $p.Id /T /F | Out-Null
        $res = 'TIMEOUT'
    } else {
        $p.WaitForExit()   # needed so ExitCode is populated when output is redirected
        $recorded = (Test-Path $out) -and (Select-String -Path $out -Pattern 'Recorded in' -Quiet)
        if ($p.ExitCode -eq 0 -or ($null -eq $p.ExitCode -and $recorded)) { $res = 'ok' }
        elseif ($recorded) { $res = 'ok' }
        else { $res = "FAILED (exit $($p.ExitCode))" }
    }
    $sw.Stop()
    $detail = ''
    if (Test-Path $out) { $detail = (Get-Content $out -Tail 40 | Where-Object { $_ -match 'steps/s|step_time|ms|recorded' } | Select-Object -Last 1) }
    if (-not $detail -and (Test-Path "$out.err")) { $detail = (Get-Content "$out.err" -Tail 1) }
    $color = if ($res -eq 'ok') { 'Green' } else { 'Red' }
    Write-Host ("  {0}  ({1:n0}s)  {2}" -f $res, $sw.Elapsed.TotalSeconds, $detail) -ForegroundColor $color
    $results += [pscustomobject]@{Repo=$r; Result=$res; Seconds=[int]$sw.Elapsed.TotalSeconds; Detail=$detail}
}

Write-Host ""
Write-Host "=== Summary ===" -ForegroundColor Yellow
$results | Format-Table -AutoSize | Out-String | Write-Host
$results | ConvertTo-Json | Set-Content (Join-Path $logDir 'summary.json')
Write-Host "Logs: $logDir"
$bad = @($results | Where-Object { $_.Result -notin 'ok','skipped' }).Count
if ($bad) { Write-Host "$bad project(s) did not finish - see their .log / .log.err files." -ForegroundColor Red }

if (-not $NoRefresh) {
    Write-Host ""
    Write-Host "=== Refreshing the site snapshot with the new numbers ===" -ForegroundColor Yellow
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'refresh_and_push.ps1')
}
