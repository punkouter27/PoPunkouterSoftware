<#
.SYNOPSIS
    One command for the before/after page a UI change ships with (AGENTS.md, "A UI change ships a before/after page").

.DESCRIPTION
    1. Builds each side LOCALLY: `-Before` (a git ref, checked out in a temporary detached worktree) and `-After`
       (a git ref, or the working tree when empty) are each published Release, like deploy.yml ships, and started on
       -Port with the same safe settings as run-e2eui.ps1 (Development, pinger and outside probes off, so nothing
       reaches the live Po apps). `-BeforeDir` skips the before build and reads PNGs named `*<screen>_<w>x<h>.png`.
       Never points at the live site.
    2. Captures every screen at 1366x768 and 390x844 (phone: touch + mobile emulation) with the repo's own
       Playwright (tests/PoPunkouterSoftware.E2EUI). Plain screens run with reduced motion so the animated backdrop
       cannot show up as a change; a screen with a click step runs with motion on (the click is usually an effect).
    3. Compares before and after pixel by pixel (in Chromium) and boxes the changed areas in red on the after shot.
    4. Writes SCREENSHOTS/<yyMMdd>-<Feature>.html (self-contained, images inlined; unchanged screens shown once,
       small) and SCREENSHOTS/<yyMMdd>-<Feature>.png (only the changed screens, before | after, with the notes).

    Needs Docker (Azurite) unless something already answers on :10002, and Playwright Chromium
    (`pwsh tests/PoPunkouterSoftware.E2EUI/bin/Release/net10.0/playwright.ps1 install chromium` once).

.PARAMETER Screens
    name=route, optionally followed by |<css selector to click> (e.g. 'home-fallen=/|[data-letter-fall]').

.PARAMETER Crops
    Close-ups shown first, before | after at 3x: <screen>_<w>x<h>=<x>,<y>,<width>,<height>
    (e.g. 'home-fallen_1366x768=1166,688,200,80' for the version stamp).

.EXAMPLE
    pwsh ./scripts/screenshots.ps1 -Feature letter-fall-stamp -Notes 'The vYYMMDD stamp stays put when the letters fall.'
    pwsh ./scripts/screenshots.ps1 -Feature v4-audit -Before 3f17d2a -After 0684748
    pwsh ./scripts/screenshots.ps1 -Feature diag-rows -BeforeDir C:\temp\old-shots -Screens 'diag=/diag'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]+$')][string]$Feature,
    [string]$Before = 'HEAD~1',
    [string]$BeforeDir = '',
    [string]$After = '',
    [string[]]$Screens = @('home=/', 'azure=/azure', 'users=/users', 'settings=/settings', 'diag=/diag'),
    [string[]]$Notes = @(),
    [string[]]$Crops = @(),
    [string]$Date = (Get-Date).ToUniversalTime().ToString('yyMMdd'),
    [int]$Port = 8010,
    [switch]$KeepWork
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $repoRoot 'SCREENSHOTS'
$work = Join-Path ([IO.Path]::GetTempPath()) "popunkoutersoftware-shots-$PID"
$viewports = @(@{ W = 1366; H = 768; Mobile = $false }, @{ W = 390; H = 844; Mobile = $true })
$screenList = foreach ($s in $Screens) {
    if ($s -notmatch '^([a-z0-9-]+)=(/[^|]*)(?:\|(.+))?$') { throw "Screen '$s' is not name=/route[|click-selector]" }
    [pscustomobject]@{ Name = $Matches[1]; Route = $Matches[2]; Click = $Matches[3] }
}
if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) { throw "Port $Port is in use; pass -Port." }

# ── Playwright: the E2EUI project's own assembly and browser ─────────────────
$pwDir = Join-Path $repoRoot 'tests/PoPunkouterSoftware.E2EUI/bin/Release/net10.0'
if (-not (Test-Path (Join-Path $pwDir 'Microsoft.Playwright.dll'))) {
    dotnet build (Join-Path $repoRoot 'tests/PoPunkouterSoftware.E2EUI') -c Release | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'building tests/PoPunkouterSoftware.E2EUI failed' }
}
Add-Type -Path (Join-Path $pwDir 'Microsoft.Playwright.dll')
function Wait-Task($task) { $task.GetAwaiter().GetResult() }

New-Item -ItemType Directory -Force $work | Out-Null
$startedAzurite = $false
$worktrees = @()
$pw = $null; $browser = $null
try {
    if (-not (Test-NetConnection 127.0.0.1 -Port 10002 -WarningAction SilentlyContinue).TcpTestSucceeded) {
        docker compose -f (Join-Path $repoRoot 'docker-compose.yml') up -d azurite | Out-Null
        $startedAzurite = $true
    }
    $pw = Wait-Task ([Microsoft.Playwright.Playwright]::CreateAsync())
    $browser = Wait-Task ($pw.Chromium.LaunchAsync([Microsoft.Playwright.BrowserTypeLaunchOptions]@{ Headless = $true }))

    function Get-Source([string]$ref, [string]$side) {
        if (-not $ref) { return $repoRoot }
        $dir = Join-Path $work "src-$side"
        git -C $repoRoot worktree add --detach $dir $ref 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "git worktree add $ref failed" }
        $script:worktrees += $dir
        return $dir
    }

    function Invoke-Capture([string]$src, [string]$side) {
        $pub = Join-Path $work "pub-$side"
        Write-Host "[$side] publishing $src" -ForegroundColor Cyan
        dotnet publish (Join-Path $src 'src/PoPunkouterSoftware.Api/PoPunkouterSoftware.Api.csproj') -c Release -o $pub | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "[$side] publish failed" }
        # The stored Azure scan the pages render (the repo's local copy; never fetched from live here).
        $scan = Join-Path $repoRoot 'src/PoPunkouterSoftware.Api/App_Data/azure-full-report.json'
        if (Test-Path $scan) { New-Item -ItemType Directory -Force (Join-Path $pub 'App_Data') | Out-Null; Copy-Item $scan (Join-Path $pub 'App_Data') }

        $env:ASPNETCORE_ENVIRONMENT = 'Development'
        $env:ASPNETCORE_URLS = "http://localhost:$Port"
        $env:Pinger__Enabled = 'false'
        $env:Diag__ProbeServices = 'false'
        $app = Start-Process -FilePath (Join-Path $pub 'PoPunkouterSoftware.Api.exe') -WorkingDirectory $pub -PassThru `
            -RedirectStandardOutput (Join-Path $work "$side.out.log") -RedirectStandardError (Join-Path $work "$side.err.log")
        $shots = Join-Path $work "shots-$side"
        New-Item -ItemType Directory -Force $shots | Out-Null
        try {
            $deadline = (Get-Date).AddSeconds(90)
            do {
                Start-Sleep -Seconds 2
                try { $ok = (Invoke-WebRequest "http://localhost:$Port/health/live" -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200 } catch { $ok = $false }
            } until ($ok -or (Get-Date) -gt $deadline)
            if (-not $ok) { throw "[$side] the app did not answer on :$Port (see $work\$side.err.log)" }
            foreach ($s in $screenList) {
                foreach ($v in $viewports) {
                    $ctx = Wait-Task ($browser.NewContextAsync([Microsoft.Playwright.BrowserNewContextOptions]@{
                        ViewportSize = [Microsoft.Playwright.ViewportSize]@{ Width = $v.W; Height = $v.H }
                        IsMobile = $v.Mobile; HasTouch = $v.Mobile; DeviceScaleFactor = 1
                        ReducedMotion = $(if ($s.Click) { [Microsoft.Playwright.ReducedMotion]::NoPreference } else { [Microsoft.Playwright.ReducedMotion]::Reduce })
                    }))
                    try {
                        $page = Wait-Task ($ctx.NewPageAsync())
                        Wait-Task ($page.GotoAsync("http://localhost:$Port$($s.Route)", [Microsoft.Playwright.PageGotoOptions]@{ WaitUntil = [Microsoft.Playwright.WaitUntilState]::Load; Timeout = 60000 })) | Out-Null
                        # Network-idle when the page gets there; a page that keeps a request open is shot anyway.
                        try { Wait-Task ($page.WaitForLoadStateAsync([Microsoft.Playwright.LoadState]::NetworkIdle, [Microsoft.Playwright.PageWaitForLoadStateOptions]@{ Timeout = 20000 })) | Out-Null } catch { Write-Host "[$side] $($s.Name): no network-idle after 20 s, shooting anyway" -ForegroundColor Yellow }
                        Wait-Task ($page.WaitForSelectorAsync('main', [Microsoft.Playwright.PageWaitForSelectorOptions]@{ Timeout = 40000 })) | Out-Null
                        # Let finite animations (page fade-ins) land, then a beat for late data.
                        Wait-Task ($page.EvaluateAsync('() => Promise.all(document.getAnimations().filter(a => a.effect?.getTiming().iterations !== Infinity).map(a => a.finished.catch(() => {})))', $null)) | Out-Null
                        Start-Sleep -Milliseconds 1500
                        if ($s.Click) {
                            Wait-Task ($page.Locator($s.Click).First.ClickAsync()) | Out-Null
                            Start-Sleep -Seconds 4
                        }
                        $file = Join-Path $shots "$($s.Name)_$($v.W)x$($v.H).png"
                        Wait-Task ($page.ScreenshotAsync([Microsoft.Playwright.PageScreenshotOptions]@{ Path = $file })) | Out-Null
                        Write-Host "[$side] $($s.Name) $($v.W)x$($v.H)"
                    }
                    finally { Wait-Task ($ctx.CloseAsync()) | Out-Null }
                }
            }
        }
        finally { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
        return $shots
    }

    $beforeShots = if ($BeforeDir) { $BeforeDir } else { Invoke-Capture (Get-Source $Before 'before') 'before' }
    $afterShots = Invoke-Capture (Get-Source $After 'after') 'after'

    # ── Compare, annotate, compose ───────────────────────────────────────────
    $tool = Wait-Task ($browser.NewPageAsync())
    $diffJs = @'
async ([a, b]) => {
  const load = src => new Promise((ok, no) => { const i = new Image(); i.onload = () => ok(i); i.onerror = no; i.src = src; });
  const [ia, ib] = await Promise.all([load(a), load(b)]);
  const w = Math.max(ia.width, ib.width), h = Math.max(ia.height, ib.height);
  const pix = img => { const c = new OffscreenCanvas(w, h); const x = c.getContext('2d'); x.drawImage(img, 0, 0); return x.getImageData(0, 0, w, h).data; };
  const pa = pix(ia), pb = pix(ib);
  const CELL = 16, gw = Math.ceil(w / CELL), gh = Math.ceil(h / CELL), grid = new Uint8Array(gw * gh);
  let changed = 0;
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
    const i = (y * w + x) * 4;
    if (Math.abs(pa[i] - pb[i]) + Math.abs(pa[i + 1] - pb[i + 1]) + Math.abs(pa[i + 2] - pb[i + 2]) > 48) { changed++; grid[((y / CELL) | 0) * gw + ((x / CELL) | 0)] = 1; }
  }
  // Changed cells grow into boxes (8-connected, one cell of slack), one red box per cluster.
  const seen = new Uint8Array(gw * gh), boxes = [];
  for (let s = 0; s < grid.length; s++) {
    if (!grid[s] || seen[s]) continue;
    let x0 = 1e9, y0 = 1e9, x1 = -1, y1 = -1; const st = [s]; seen[s] = 1;
    while (st.length) {
      const c = st.pop(), cx = c % gw, cy = (c / gw) | 0;
      x0 = Math.min(x0, cx); y0 = Math.min(y0, cy); x1 = Math.max(x1, cx); y1 = Math.max(y1, cy);
      for (let dy = -2; dy <= 2; dy++) for (let dx = -2; dx <= 2; dx++) {
        const nx = cx + dx, ny = cy + dy, n = ny * gw + nx;
        if (nx >= 0 && ny >= 0 && nx < gw && ny < gh && grid[n] && !seen[n]) { seen[n] = 1; st.push(n); }
      }
    }
    boxes.push([x0 * CELL, y0 * CELL, (x1 + 1) * CELL, (y1 + 1) * CELL]);
  }
  const c = new OffscreenCanvas(ib.width, ib.height), x = c.getContext('2d');
  x.drawImage(ib, 0, 0); x.strokeStyle = '#e00'; x.lineWidth = 3;
  for (const [l, t, r, btm] of boxes) x.strokeRect(l - 3, t - 3, r - l + 6, btm - t + 6);
  const blob = await c.convertToBlob({ type: 'image/png' });
  const marked = await new Promise(ok => { const f = new FileReader(); f.onload = () => ok(f.result); f.readAsDataURL(blob); });
  return JSON.stringify({ pct: Math.round(changed * 10000 / (w * h)) / 100, boxes: boxes.length, marked });
}
'@
    $cropJs = @'
async ([src, x, y, w, h]) => {
  const i = await new Promise((ok, no) => { const m = new Image(); m.onload = () => ok(m); m.onerror = no; m.src = src; });
  const c = document.createElement('canvas'); c.width = w * 3; c.height = h * 3;
  const g = c.getContext('2d'); g.imageSmoothingEnabled = false; g.drawImage(i, x, y, w, h, 0, 0, w * 3, h * 3);
  return c.toDataURL('image/png');
}
'@
    $enc = [Net.WebUtility]
    $rows = foreach ($s in $screenList) {
        foreach ($v in $viewports) {
            $key = "$($s.Name)_$($v.W)x$($v.H)"
            $b = Get-ChildItem $beforeShots -Filter "*$key.png" | Select-Object -First 1
            $a = Join-Path $afterShots "$key.png"
            $bUri = if ($b) { 'data:image/png;base64,' + [Convert]::ToBase64String([IO.File]::ReadAllBytes($b.FullName)) } else { $null }
            $aUri = 'data:image/png;base64,' + [Convert]::ToBase64String([IO.File]::ReadAllBytes($a))
            $d = if ($bUri) { Wait-Task ($tool.EvaluateAsync[string]($diffJs, [string[]]@($bUri, $aUri))) | ConvertFrom-Json } else { $null }
            [pscustomobject]@{ Screen = $s; W = $v.W; H = $v.H; Before = $bUri; After = $aUri; Diff = $d
                Changed = (-not $d) -or $d.pct -gt 0 }
        }
    }

    $closeups = foreach ($c in $Crops) {
        if ($c -notmatch '^([a-z0-9-]+_\d+x\d+)=(\d+),(\d+),(\d+),(\d+)$') { throw "Crop '$c' is not <screen>_<w>x<h>=x,y,w,h" }
        $key = $Matches[1]; $box = @([int]$Matches[2], [int]$Matches[3], [int]$Matches[4], [int]$Matches[5])
        $r = $rows | Where-Object { "$($_.Screen.Name)_$($_.W)x$($_.H)" -eq $key } | Select-Object -First 1
        if (-not $r) { throw "Crop '$c' names no captured screen" }
        $cut = { param($uri) if ($uri) { Wait-Task ($tool.EvaluateAsync[string]($cropJs, [object[]]@($uri, $box[0], $box[1], $box[2], $box[3]))) } }
        [pscustomobject]@{ Key = $key; Box = $box -join ','; Before = (& $cut $r.Before); After = (& $cut $r.After) }
    }

    $title = "$Date $Feature"
    $beforeLabel = if ($BeforeDir) { "PNGs from $(Split-Path $BeforeDir -Leaf)" } else { "git $Before ($(git -C $repoRoot rev-parse --short $Before))" }
    $afterLabel = if ($After) { "git $After ($(git -C $repoRoot rev-parse --short $After))" } else { "working tree on $(git -C $repoRoot rev-parse --short HEAD)" }
    $css = 'body{font:14px system-ui,sans-serif;margin:16px;background:#fff;color:#111}h1{font-size:20px}h2{font-size:16px;margin:20px 0 6px}' +
        '.pair{display:flex;gap:12px;align-items:flex-start;flex-wrap:wrap}figure{margin:0}figcaption{font-weight:600;margin-bottom:4px}' +
        'img{border:1px solid #bbb;display:block}.same{color:#555}.chg{color:#b00;font-weight:600}'
    function Format-Page([object[]]$items, [double]$deskScale, [double]$phoneScale, [bool]$all) {
        $sb = [Text.StringBuilder]::new()
        [void]$sb.Append("<!doctype html><html lang=`"en`"><head><meta charset=`"utf-8`"><title>$($enc::HtmlEncode($title))</title><style>$css</style></head><body>")
        [void]$sb.Append("<h1>$($enc::HtmlEncode($title)): before / after</h1><p>Before: $($enc::HtmlEncode($beforeLabel)). After: $($enc::HtmlEncode($afterLabel)). Local Release publish, 1366x768 and 390x844; red boxes mark changed pixels.</p>")
        if ($Notes) { [void]$sb.Append('<ul>' + (($Notes | ForEach-Object { "<li>$($enc::HtmlEncode($_))</li>" }) -join '') + '</ul>') }
        foreach ($c in $closeups) {
            [void]$sb.Append("<h2>Close-up: $($c.Key) at $($c.Box) (3x)</h2><div class=`"pair`">")
            if ($c.Before) { [void]$sb.Append("<figure><figcaption>Before</figcaption><img alt=`"before close-up`" src=`"$($c.Before)`"></figure>") }
            [void]$sb.Append("<figure><figcaption>After</figcaption><img alt=`"after close-up`" src=`"$($c.After)`"></figure></div>")
        }
        foreach ($r in $items) {
            $scale = if ($r.W -gt 768) { $deskScale } else { $phoneScale }
            $wpx = [int]($r.W * $scale)
            $what = if (-not $r.Diff) { '<span class="chg">no before shot</span>' }
                elseif ($r.Changed) { "<span class=`"chg`">changed: $($r.Diff.pct)% of pixels, $($r.Diff.boxes) area(s)</span>" }
                else { '<span class="same">no visible change</span>' }
            [void]$sb.Append("<h2>$($r.Screen.Name) ($($enc::HtmlEncode($r.Screen.Route))$(if ($r.Screen.Click) { ', after clicking ' + $enc::HtmlEncode($r.Screen.Click) })) at $($r.W)x$($r.H): $what</h2><div class=`"pair`">")
            if ($r.Changed -or $all) {
                if ($r.Before) { [void]$sb.Append("<figure><figcaption>Before</figcaption><img width=`"$wpx`" alt=`"before`" src=`"$($r.Before)`"></figure>") }
                $shown = if ($r.Diff) { $r.Diff.marked } else { $r.After }
                [void]$sb.Append("<figure><figcaption>After</figcaption><img width=`"$wpx`" alt=`"after`" src=`"$shown`"></figure>")
            }
            else { [void]$sb.Append("<figure><figcaption>Both</figcaption><img width=`"$([int]($wpx / 2))`" alt=`"unchanged`" src=`"$($r.After)`"></figure>") }
            [void]$sb.Append('</div>')
        }
        [void]$sb.Append('</body></html>')
        $sb.ToString()
    }

    New-Item -ItemType Directory -Force $outDir | Out-Null
    $html = Join-Path $outDir "$Date-$Feature.html"
    $png = Join-Path $outDir "$Date-$Feature.png"
    [IO.File]::WriteAllText($html, (Format-Page $rows 0.5 0.6 $false), [Text.UTF8Encoding]::new($false))

    # The one PNG: changed screens only (or the first screen when nothing changed), shrunk until it is small.
    $pngRows = @($rows | Where-Object Changed)
    if (-not $pngRows) { $pngRows = @($rows | Select-Object -First 2) }
    foreach ($scale in 0.45, 0.35, 0.28, 0.22) {
        $shot = Wait-Task ($browser.NewPageAsync([Microsoft.Playwright.BrowserNewPageOptions]@{ ViewportSize = [Microsoft.Playwright.ViewportSize]@{ Width = 1300; Height = 800 } }))
        Wait-Task ($shot.SetContentAsync((Format-Page $pngRows $scale ($scale * 1.4) $true))) | Out-Null
        Wait-Task ($shot.ScreenshotAsync([Microsoft.Playwright.PageScreenshotOptions]@{ Path = $png; FullPage = $true })) | Out-Null
        Wait-Task ($shot.CloseAsync()) | Out-Null
        if ((Get-Item $png).Length -le 300KB) { break }
    }
    Write-Host ''
    Write-Host "Page: $html ($([int]((Get-Item $html).Length / 1KB)) KB)" -ForegroundColor Green
    Write-Host "PNG:  $png ($([int]((Get-Item $png).Length / 1KB)) KB)" -ForegroundColor Green
    $rows | ForEach-Object { "{0,-14} {1,4}x{2,-4} {3}" -f $_.Screen.Name, $_.W, $_.H, $(if (-not $_.Diff) { 'no before' } elseif ($_.Changed) { "changed $($_.Diff.pct)%" } else { 'same' }) }
}
finally {
    if ($browser) { Wait-Task ($browser.CloseAsync()) | Out-Null }
    if ($pw) { $pw.Dispose() }
    foreach ($w in $worktrees) { git -C $repoRoot worktree remove --force $w 2>&1 | Out-Null }
    if ($startedAzurite) { docker compose -f (Join-Path $repoRoot 'docker-compose.yml') rm --stop --force azurite 2>&1 | Out-Null }
    if (-not $KeepWork) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
}
