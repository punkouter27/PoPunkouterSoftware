<#
.SYNOPSIS
  Points the Windows scheduled task "CODEMATT Morning Briefing" at this repo: every day at
  07:00 it runs refresh_and_push.ps1 (crawl, commit the snapshot, push), so the /global
  pages on the live site are current each morning.

.DESCRIPTION
  Replaces the task that ran GLOBAL\RUN_ME.bat. Runs only while you are logged on, and
  runs late if the PC was asleep. Remove it with:
    Unregister-ScheduledTask -TaskName 'CODEMATT Morning Briefing' -Confirm:$false

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\global\register_daily_refresh.ps1 [-At 07:00]
#>
param([string]$At = '07:00')

$script = Join-Path $PSScriptRoot 'refresh_and_push.ps1'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$action = New-ScheduledTaskAction -Execute 'powershell.exe' `
    -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$script`"" -WorkingDirectory $repo
$trigger = New-ScheduledTaskTrigger -Daily -At $At
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 1) `
    -DontStopIfGoingOnBatteries -AllowStartIfOnBatteries
Register-ScheduledTask -TaskName 'CODEMATT Morning Briefing' -Action $action -Trigger $trigger -Settings $settings `
    -Description 'Refreshes the PoPunkouterSoftware /global snapshot and pushes it (scripts/global/refresh_and_push.ps1).' `
    -Force | Out-Null
Get-ScheduledTask -TaskName 'CODEMATT Morning Briefing' |
    Select-Object TaskName, State, @{ n = 'Next run'; e = { (Get-ScheduledTaskInfo $_).NextRunTime } }
