# CLAUDE.md

@AGENTS.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this app is

A personal **Azure portfolio / ops dashboard**: a .NET 10 Blazor Web App whose WASM front-end shows
App Service health, cost, SSL expiry, zombie-app detection and downtime diagnosis. The backend is a
minimal-API **BFF** that reads an Azure inventory report out of Azure Table Storage (with a local
JSON fallback).

There are exactly **four pages**: `/` (the app catalog), `/azure` (the ops dashboard),
`/users` (who has signed in, across every Po app) and `/settings` (visitor preferences).
Everything else is an endpoint.

For years this said *two* pages, and that was load-bearing — the count is a budget, not a
tally. `/users` was added 2026-09-12 because the answer it gives lives nowhere else in the
estate: an Azure Monitor workbook nobody opens. Adding a fourth needs the same standard —
a question the existing pages cannot answer without becoming two pages in a trench coat.

**`/settings` is the fourth page by the owner's explicit call (2026-10-19), not by that standard.**
A first `/settings` (2026-10-04 to 2026-10-19) was deleted: reachable only from Ctrl+K, one of
its four controls saved and read by nothing, two duplicating palette commands, and its heading
icon rendering as the word "PALETTE". The owner then asked for a settings page that puts
optional features behind toggles defaulting to the simplest set, and the rebuild keeps the
rules the first one broke: it is the **only** home of theme, density, sound and effects (the
palette's toggles were removed, it has one "Go to Settings" row), it is reachable from Ctrl+K (its header link is hidden since 2026-10-20 — see "The header
is just the brand" below), every
control is read by something, and every default is the simplest choice. All four go through
`window.appSettings` in `theme-kit.js`; each value stays owned by the kit that applies it. The
default sign-in window is still `/users` remembering the last window picked (`pops:signin-days`).
Do not cite either page as a precedent for a fifth — that still needs the standard above.

`AGENT.MD` used to hold this context and no longer exists — this file is the single context map.
[AGENTS.md](AGENTS.md) exists only to point non-Claude agents here; never put rules in it.
Update it in the same change whenever you cross an architectural boundary; drift here actively
misleads the next agent.

## Working rules

Standing instructions from the owner. They outrank default habits and apply to every task here.

- **`master` only.** Commit straight to `master`; use another branch only when explicitly asked for
  one. See [Git workflow](#git-workflow--master-only) for the full rule.
- **Never push without being asked.** Not "asked once, months ago" — asked in the turn you are
  working on. A push deploys straight to production. Commit, then hand back and say it is ready.
  The one standing exception is the phrase **`git sync`**: typing it *is* the ask.
- **`git sync` means: stage everything, commit, push.** In that order, no questions asked in
  between. Commit **all** outstanding changes first — a sync that leaves a dirty tree behind ships
  half the work and hides the other half.
- **Git-sync commit messages are short and use American slang, so it seems a human wrote it.**
  One line, casual — "fixed the busted app cards", "cleaned up the dead scan code". No formal
  subject/body essays, no bullet lists, no changelog voice.
- **Restart the app after any code change and confirm it came back up.** `./SCRIPTS/run-dev.ps1`,
  then check it actually serves (`/healthz` returns 200) before claiming the change works. A build
  that compiles is not a running app — and this project's stale-process footgun means a "successful"
  build can leave the old binary serving on :8000.
- **No `dotnet user-secrets`.** Local config goes in `appsettings*.json`; real secrets go in the
  `kv-poshared2` Key Vault. Rationale: user secrets live outside the repo in a per-machine folder, so
  a value only one machine has looks like a bug on every other machine and in production.
  Resolved 2026-09-05: `<UserSecretsId>` removed from `PoPunkouterSoftware.API.csproj`; ASP.NET no
  longer auto-loads `%APPDATA%\Microsoft\UserSecrets\popunkouter-software-api\secrets.json` in
  Development. Re-add only with the owner's explicit go-ahead.
- **Run only the tests that cover what you changed.** Not the whole tier, and never all four tiers,
  after an ordinary code change — `--filter "FullyQualifiedName~<TypeUnderTest>Tests"` against the
  one project that holds them. The full fast tier is a **pre-push gate**, not a per-edit habit; see
  [Git workflow](#git-workflow--master-only). A minor edit (copy, comment, docs, a CSS value) can
  skip tests entirely.
- **Never hand the owner a command to type — or a portal to click through.** If you can run it,
  run it — build, test, restart, `curl` a local endpoint, read a log, check a workflow, and do via
  `az`/`gh` what would otherwise be a click in the Azure portal or GitHub UI. A reply ending in
  "now run this" (or "now go click that") is a task you left half-finished. The exceptions are the things you genuinely cannot do: pushing without the
  ask, out-of-band `az deployment` runs, and anything needing a credential you do not hold.
- **Answers over ~100 words end with a 20-word TLDR.**
- **A code change over 20 lines ends with a three-tier summary**, exactly one line each:
  **Toddler** (the simple idea), **Child** (what it does now), **Adult** (the technical or
  architectural change).
- **A UI change ships a before/after page.** Capture the old and new states, annotate what moved,
  save it as `SCREENSHOTS/YYMMDD-feature-name.html` (self-contained, images inlined) and give the
  owner its full absolute path. Also save the same before/after, with its notes, as one PNG
  beside it (`SCREENSHOTS/YYMMDD-feature-name.png`).
- **A change over 100 lines ends with three follow-up questions** about future direction or
  logic, unless questions are already pending with the owner.
- **Keep [README.md](README.md) in step with this file** whenever a dependency, configuration key
  or major endpoint changes — in the same change, like the rule above for this file.
- **Treat compile warnings as errors and make sure they are fixed.** The build command above
  already runs with `TreatWarningsAsErrors=true` solution-wide (see `Directory.Build.props`), so
  the rule is enforced by the compiler — a warning is a build failure. Don't ship a commit
  that builds with warnings; either fix the warning or, if it is intentional, suppress it at the
  smallest possible scope with a documented reason.
- **Check the root DOCS folder for a project summary — and here is that check's answer, already
  made, so do not redo it.** [docs/](docs/) is the GitHub Pages site: a landing page, `style.css`,
  per-app privacy policies and store art for PoBox, PoCross, PoDance, PoFlag, PoFootball, PoRacer,
  PoSoccer and PoSumo, none of which are in this portfolio. There is no project summary in it and
  no reason to expect one to appear. **This file is the summary.**

## Commands

```powershell
# Build. TreatWarningsAsErrors is ON solution-wide — a warning fails the build.
dotnet build PoPunkouterSoftware.sln

# Fast tier: unit then integration. ONE project per invocation — `dotnet test` rejects two
# project paths in one command (MSB1008). Integration needs Docker (Testcontainers Azurite).
dotnet test tests/PoPunkouterSoftware.Unit
dotnet test tests/PoPunkouterSoftware.Integration

# A single test or class
dotnet test tests/PoPunkouterSoftware.Integration --filter "FullyQualifiedName~ConfigEndpointTests"
dotnet test tests/PoPunkouterSoftware.Unit --filter "FullyQualifiedName~SecretMaskingTests"

# Run locally. Use this, NOT `dotnet run` — see the footguns below.
./SCRIPTS/run-dev.ps1              # kills stale instance, starts Azurite, watch-run on :8000
./SCRIPTS/run-dev.ps1 -NoWatch

# E2E — on demand only, never in CI. Requires the app already running.
# BASE_URL for both, default http://localhost:8000. Point it at production for post-deploy smoke.
dotnet test tests/PoPunkouterSoftware.E2EAPI                                    # pure HTTP
pwsh tests/PoPunkouterSoftware.E2EUI/bin/Debug/net10.0/playwright.ps1 install   # once
dotnet test tests/PoPunkouterSoftware.E2EUI                                     # Playwright
$env:HEADED=1; $env:BROWSER_CHANNEL='chrome'; dotnet test tests/PoPunkouterSoftware.E2EUI

# Rebuild the icon-font subset after adding an icon name (see "Things that will bite you").
python SCRIPTS/Build-IconFontSubset.py       # needs: pip install "fonttools[woff]" brotli

# `dotnet clean` leaves obj/ behind. When a build misbehaves for no visible reason:
Get-ChildItem -Recurse -Directory -Include bin,obj | Remove-Item -Recurse -Force
```

VS Code tasks wrap most of this: `build`, `test-unit`, `test-unit-watch`, `start-azurite`,
`start-api-server`, `deep-clean`. F5 runs `f5-prep` (kill stale dotnet → start Azurite → build).

### Never `dotnet run` this project directly

Three footguns, all documented in [SCRIPTS/run-dev.ps1](SCRIPTS/run-dev.ps1): a stale
`PoPunkouterSoftware.API.exe` locks port 8000 and the build DLLs (MSB3026 stalls, then a
half-replaced `wwwroot` gives the browser WASM 404s and SRI errors — the page hangs on the loading
skeleton forever); `--no-launch-profile` defaults to `ASPNETCORE_ENVIRONMENT=Production`, silently
attaching a local run to the **production** Key Vault, Table Storage and App Insights; and without
the profile it binds :5000 instead of :8000.

## Solution layout (vertical slices)

| Project | Role |
|---|---|
| [src/PoPunkouterSoftware.API/](src/PoPunkouterSoftware.API/) | ASP.NET Core host + BFF, and the Blazor WASM host. Slices are `Features/<Area>/<Area>Endpoints.cs` (**Config, Diag, Portfolio, SignIns**), each exposing `Map<Area>Endpoints(this WebApplication)` called from [Program.cs](src/PoPunkouterSoftware.API/Program.cs). Host-level plumbing belonging to no slice lives in `Host/`. **The namespace is flat** — every file is `PoPunkouterSoftware.API` regardless of folder — with one exception: `Platform/PoPlatform.cs` (namespace `.Platform`) is **generated** from `po-shared/src/PoPlatform.cs.tmpl` in a separate repo and shared by every Po app. Never edit this copy; change the template and run `python po-shared/sync.py`. |
| [src/PoPunkouterSoftware.Client/](src/PoPunkouterSoftware.Client/) | Blazor WASM (Radzen, mobile-first). `wwwroot` lives **only** here. The layout is **flat** — every component sits in the project root, named for what it is; there is no `Components/` tree. `wwwroot/js` holds the graphics and audio layers — see [Graphics and audio](#graphics-and-audio) below. |
| [src/PoPunkouterSoftware.Shared/](src/PoPunkouterSoftware.Shared/) | DTOs + `DomainVocabulary.cs` only. **No PackageReferences at all**, no server- or browser-only deps — it ships in the WASM bundle (`IsTrimmable`). |
| [src/PoPunkouterSoftware.Infrastructure/](src/PoPunkouterSoftware.Infrastructure/) | Azure adapters (Table Storage, ARM, pinger, incident, AI triage, telemetry) plus cross-slice helpers (`ReportFileCache`, `AttentionItemsBuilder`, `SecretMasking`). **Slices must not reference each other** — shared logic goes here. |

### Naming schema

- **One type per file, named for the type.** Two exceptions: a cohesive vocabulary of constants is
  named for the group (`DomainVocabulary.cs`, `TableStorageVocabulary.cs`), and a group of related
  DTOs is `<Concern>Models.cs` (`CostModels.cs`, `SecurityModels.cs`).
- **Partial-class aspect files are `<Type>.<Aspect>.cs`, and the aspect names the concern**, not the
  shape: `AzureReportService.Cost.cs`, `AzureDashboard.Presentation.cs`. A `.Charts.cs` holding no
  charts is the failure mode to avoid.
- **A file name must be greppable.** If the name is not a type inside the file and is not one of the
  two exceptions, the name is wrong.
- **Test files mirror the source file under test**, not one file per test class
  (`DomainVocabularyTests.cs` holds `ServiceHealthTests`, `SeverityLevelTests`, …).
- **Records crossing a component boundary get their own top-level file**
  ([PriorityQueueItem.cs](src/PoPunkouterSoftware.Client/PriorityQueueItem.cs),
  [ResourceExplorerItem.cs](src/PoPunkouterSoftware.Client/ResourceExplorerItem.cs)) — a private
  nested record cannot be a Blazor component parameter.

## Architecture: how data actually flows

**The report is the spine.** `AzureReportService.RunAsync` performs a ~14-step subscription scan and
produces one `AzureReport`. `AzureReportStore` persists it to Table Storage (gzipped blob + a small
precomputed `HistorySummary` row per scan). Everything the UI shows is a projection of that stored
report — **no endpoint queries Azure live on a page load**, with exactly one declared
exception: `/api/signins`, below.

**`/api/signins` is that exception, and the reason is the shape of the data, not convenience.**
`SignInQueryService` runs one Log Analytics query against the shared Application Insights
component on every load of `/users`, and once per `/azure` visit on the first open of an
app drawer (`AzureAppDetail` shows that app's reach and own-group cost) — lazily, never on
first paint. The inventory scan is a ~30-second ARM walk whose answer
changes slowly, so storing it and projecting it is right. A sign-in roster is one cheap query
whose whole value is being current: folding it into the nightly scan would mean somebody who
signed in an hour ago does not appear until tomorrow, which is the only question the page
exists to answer. Do not generalise this to a second endpoint without the same argument.
It also joins the STORED report (`SignInReportBuilder.AttachCosts`) to put each app's own
resource-group spend and cost-per-person beside its reach — that half is a projection, not
a live query.

**Two read contracts, deliberately split.** `/api/diag/summary` returns the compact `OpsSummary`
(built by `DiagEndpoints.BuildOpsSummary`) and is the *only* first-paint fetch for `/azure`. The full
`AzureReport` loads lazily, only when Advanced diagnostics is opened. Adding a
field to the first-paint path means adding it to `OpsSummary`, not returning the full graph — an
integration test asserts `/api/diag/summary` does not contain `webServices`.

**Refresh is asynchronous and pushed.** `POST /api/diag/refresh` returns 202 immediately;
`ReportRefreshRunner` runs the scan on a background task and broadcasts progress over the
`RefreshHub` SignalR hub (`/hubs/refresh`). The client also polls `/api/diag/summary` until
`refreshInProgress` clears, and shows an *indeterminate* progress bar when the hub is down, because
percent values arrive only over the hub. **A scan already running is JOINED, never refused**:
`OpsSummary.RefreshInProgress` makes a page that loads mid-scan (the visitor left and came back,
the nightly workflow, another tab) show its progress and Cancel, and a Rescan that gets 409
follows that scan. Before, the page showed an idle Rescan button that could only answer "Already
running". The home page's **Sync** button (owner's ask, 2026-10-19) starts the same scan and
simply polls `/api/portfolio` until `refreshInProgress` clears — it needs no progress UI. Both
buttons render only when `/api/config` reports `managementActionsEnabled`, so in Production
(where the endpoint also wants a management key the browser lacks) neither is shown.

**Degradation is a designed path, not an error path.** Table Storage unavailable falls back to a
local JSON report file (`ReportFileCache`); a corrupt cache degrades to "no report", never a 500.
`wwwroot/data/apps.json` is authoritative for the home page — live Azure inventory only *decorates*
entries, so a probe failure never removes an app from the portfolio.
**Degrading has to be FAST too, or it is an outage.** Every storage bootstrap (both table
stores and the screenshot blob container) caps its CreateIfNotExists probe at
`TableClientProvider.BootstrapTimeout` (10s) and then sits out a 30s cooldown. Until
2026-10-20 the blob container had no cooldown and no probe had a cap: with local Azurite not
running, the SDK retried a refused port (~2s each on Windows) and `/api/portfolio` took 70-90s
to fall back, so the client's 30s budget always lost and the home page said "could not be
loaded". Now ~20s once, then instant.

**The report cache is server-side and must stay off the web root.** `ReportFileCache` keeps two
paths apart on purpose: `GetCatalogDir` is `wwwroot/data` and holds **only** `apps.json`, which is
public by nature; `GetCacheDir` is `ContentRootPath/App_Data` and holds the report. The cache used
to sit beside `apps.json` in `wwwroot/data`, where `UseStaticFiles()` served it to anyone who
requested `/data/azure-full-report.json` — a full Azure inventory including the raw subscription id,
every resource id, cost figures and SSL state, and it was committed to the repo besides. It has
never had a browser consumer. Do not put it back under any served directory, and do not "fix" a
future leak with a static-file exclusion when the file simply does not belong there.
`App_Data/` is gitignored; the Integration fixture seeds its own copy (see below).
Local screenshot captures live there too (`App_Data/screenshots`, `GetScreenshotsDir`): they sat
in the Client's `wwwroot/screenshots`, were committed, churned the tree on every local capture,
and were served statically beside `/api/portfolio/screenshots/{host}`. Production reads blobs.

**Each fact appears once on `/azure`.** The page carries one `.azure-glance-grid` of three cards
(forecast, cost-by-resource-group, response times) with the uptime heatmap below it.
Two of those three draw with `MetricBars` — plain HTML/CSS rows — not `RadzenChart`. Both series
are a **top-N ranking** (`Take(5)` / `Take(6)`, pre-sorted server-side), and a chart was the wrong
shape for one three times over: it rebuilt an SVG scene graph on every parent render, it carried a
fixed pixel height with a horizontal category axis that could not survive 390px without an
`overflow-x` escape hatch, and it spent most of its ink on axis furniture around five numbers that
are now simply printed on the row. Reach for `RadzenChart` when a series is continuous
(`SignInTrend` on `/users` does); reach for `MetricBars` when it is a ranking. A `MetricBars` bar
column has a 2.5rem floor: with `minmax(0, 1fr)` grid gave the label its full 11rem first and
every bar in a ~190px card resolved to 0px.
There used to be a second grid and a fourth card: a donut restating the healthy/total ratio the hero
tile already gives as a percentage — the same number appeared in the hero, the card subtitle and the
donut centre — and a cost chart that drew `CostHistory`, the exact series the hero's cost sparkline
draws. Trend belongs in the sparkline, breakdown in the chart, ratio in the hero. `OpsSummary` lost
`FleetHealth` with the donut; if you add a field to the first-paint contract, make sure nothing on
the page already renders it.

The 2026-09-26 consolidation applied the same rule to the rest of the page (2026-10-19 went
further: see the pane order below). The AI narrative is
the hero's lead paragraph under the h1, not a glass card in a pane of its own above it (the page
opened by saying the same thing twice). Health % and the unavailable count are one tile
("80% · Health · 2 down"), so the hero has three tiles and `OpsSummary` lost `BrokenHistory`.
Freshness is stated once ("Updated 2h ago" plus a badge only when stale). "What changed since
the last scan" is part of the hero, under the lead paragraph, not a pane of its own. There is ONE
list of what needs doing: "Needs attention" shows the server's plain sentences on first paint and
is replaced in place by the ranked `AzurePriorityQueue` once Advanced diagnostics has loaded the
report (and stays replaced after its drawer closes) —
it used to be two lists, plus count badges that repeated two of the sentences. The "Cleanup
evidence" disclosure is gone (every row was already a "Remove Candidate" in the queue, with a
second snooze button), and so is the 30-day history disclosure with its `/api/diag/history`
endpoint (both charts were the hero sparklines' series). The resource explorer is a table, not
a card per resource. Methodology and owner-facing config text (uptime method, budget key,
management flag, cold starts) lives in an icon-only `AppHint`, not a paragraph.

**One design system, two dials.** Everything visual resolves from the token block at the top of
`modern-ui.css`, and two of those tokens are the only places a global visual decision is made:

- **`--app-density`** scales the whole spacing ladder (`--app-space-*` are all
  `calc(<base> * var(--app-density))`). Compacting the UI for a viewport is one declaration —
  0.9 at 820px, 0.78 at 640px — not a per-component `padding` override repeated in every
  breakpoint of every stylesheet, which is what it replaced.
- **`--app-step--2 … --app-step-5`** is the fluid type ladder. Every font-size above ~0.9rem
  resolves from a step. Do **not** write a new `clamp()`: the catalogue h1 grew at `5vw` while the
  dashboard hero grew at `3vw`, so the two pages' headings crossed over somewhere mid-range and
  neither size was ever chosen. Density does not scale type — the ladder already shrinks with the
  viewport and multiplying the two compounds into unreadable text. The bottom step is also the
  app's most-repeated size (`.app-kicker`, card subtitles, `.app-note`) and its floor is 0.76rem
  (12.16px at 390px) — it was 0.68rem/10.88px, below comfortable reading for a tracked all-caps
  kicker.
  **A literal below that floor is now the app's smallest text**, which is why `AzureDashboard.razor.css`
  no longer carries a parallel scale in raw rems: eleven declarations between .68 and .75rem are now
  `var(--app-step--2)`. The same sweep has NOT been done everywhere — Index/PortfolioAppCard/SignIns
  still hold literals in the 0.78–0.85rem band, which sit *above* the floor and are left alone.

(A third, `--app-viewport-fit`, sized the phone pager's panes and went with it.)

`--app-text-soft` is a **measured** value, not a taste call, in both themes: it is applied to the
smallest type in the app, so re-measure it against `--app-surface` (the darker of the two grounds
these sit on, and the one `[data-glass]` makes translucent), not against `--app-bg`.

**Every page scrolls ordinarily; `/` guarantees its first screen.** `/azure` PAGED on a phone
until 2026-10-19 — a `[data-snap-pager]` scroll-snap container one viewport tall, a pane per
screen, a JS dot strip — and that went (owner's call: "whatever is simpler"). Every pane had to
fill a whole screen, so "Needs attention" was ~150px of content in a 756px box, the hero sat
centred under 230px of nothing, and four screens of paging held what now fits in two of
scrolling; it also needed a `--tall` opt-out, a specificity hack and its own E2E test to stay
honest. Do not bring it back. `/` is a 10-item catalogue and guarantees its first screen is
complete (`Home_FirstScreen_ShowsAWholeCard`). **No route truncates.** Advanced diagnostics is
not a pane but a drawer (see "Drawers are native dialogs" below).
`Azure_MobilePortrait_UptimeOpensOnToday_AndNothingOverflows` holds the phone layout.

**At every width `/azure` is one column of four panes, in the order status, actions, spend,
uptime** — what is going on, what to do about it, then the evidence. "Needs attention" is
SECOND because it is the one list a visitor acts on; until 2026-10-19 it was last, below the fold
at 1440×1000 (y=1106 in a 935px viewport) and page five of the old phone pager. From 2026-09 to
2026-10-19 a `@media (min-width: 1100px)` block made the page two-up, but its only half-width
pair was uptime beside "what changed" — a 129px card next to a 342px one, a ~210px hole on every
visit — and every other pane spanned both columns anyway. With "what changed" in the hero there
is no pair left. The uptime grid's day columns are `minmax(var(--uptime-cell), 1fr)` (set inline
in `AzureUptimeHeatmap.GridStyle`): at least a cell wide, so a phone still scrolls the grid inside
its container, and stretched to the card on desktop, where `--uptime-cell` is 14px. The scroller
is `direction: rtl` (the grid inside it `ltr`) so it STARTS at the right edge: a phone opens on
today. It opened on the oldest day, so with a few days of history the card was all empty boxes.

`/users` is a roster of unknown length and scrolls ordinarily. Its one concession is
that the roster and the raw feed sit in `.signins-scroll` / the disclosure body, each its own
`overflow-x: auto` container — five columns of real text cannot honestly reflow to 390px, and
a per-table scroller is the escape hatch the "page body never scrolls sideways" rule allows.
Everything else on the page reflows.

The roster itself drops from five columns to three below 640px. Measured at 393px it was a
**493px table inside a 339px scroller**: the sign-in count and both dates were off-screen,
the app-name chips were cut mid-word at the clip edge, and nothing indicated the table
scrolled at all. "First seen"/"Last seen" are the two columns a visitor can most easily do
without side by side, and `.signin-person__dates` repeats them under the person's name —
`display: none` at every viewport where the real columns exist, so exactly one copy is in the
accessibility tree at any width. Three columns STILL did not fit until a masked address could
wrap: it has no break opportunity, so `SignInRoster.BreakAtSign` puts a `<wbr>` before the `@`
(not `overflow-wrap: anywhere`, which split "potraffic.d/ev"). The scroller stays (with `tabindex="0"` + `role="group"`,
the same treatment the uptime grid carries) because it is still needed at very narrow widths,
and a scroll region that is unreachable on exactly the screen where it scrolls is worse than
no scroller at all.

**Two big types are partial classes split by concern.** Find the concern, not the file:

- `AzureReportService.cs` — orchestrator + primary constructor, declared there **and only there**.
  Steps live in `.Discovery`, `.Metrics`, `.Cost`, `.Security`, `.Inventory`, `.Cleanup`,
  `.GitHubCorrelation`, `.Helpers`.
- `AzureDashboard.razor.cs` — state, lifecycle, loading, refresh, SignalR. Display mapping in
  `.Presentation`, sonification in `.Audio`. The pure projections are NOT a partial-class aspect any
  more: they live in `DashboardDerivations.cs`, a plain `public static` class, because as private
  members of the component ~460 lines of impact scoring, actionability tiering and cleanup
  reasoning were unreachable from a test even though both test projects reference this assembly.
  (`.DerivedViews` and `.Trends` were deleted with the cleanup and history disclosures.)
  Anything pure belongs in `DashboardDerivations`, with a test in
  `DashboardDerivationsTests`. Markup blocks are
  sibling components in the client root (`AzureStatusNarrative`, `AzureWhatChanged`,
  `AzureCostForecast`, `AzureUptimeHeatmap`, `Sparkline`, `AzurePriorityQueue`,
  `AzureResourceExplorer`, `AzureEvidenceDisclosures`, `AzureAppDetail`),
  plus the shared `AppDrawer`.

## Graphics and audio

Ten files in [wwwroot/js/](src/PoPunkouterSoftware.Client/wwwroot/js/). Read the header
comment in each before changing it; they document the reasoning, this is the map.

| File | Role |
|---|---|
| `motion-kit.js` | **The frame governor.** ONE `requestAnimationFrame` loop for the whole app, one gate (reduced-motion / hidden / blur / no runnable subscriber), and an adaptive quality controller. Must load first. |
| `audio-kit.js` | Programmatic Web Audio — synthesis only, **zero audio assets**. UI SFX, the refresh drone (plus a sub "boom" and a three-note outcome motif at its end), the `/azure` sonification, and the analyser tap the shaders read. Everything runs through one synthesised-impulse convolution reverb send. |
| `gpu-backdrop.js` | Orchestrator for `#app-gpu-backdrop`: tier selection, colour tokens, glass rects, audio energy, and the DOM reads that feed the simulations (pointer stir, `[data-fx-flock]` homes, uptime-scar seeds, palette lens/jump, the hero orb canvas). Owns no pixels. |
| `gfx-webgl.js` | WebGL2 renderer (curl-noise field, stable-fluids sim, transform-feedback particles with per-service flocks, dual-Kawase blur + bloom, Gray-Scott scars, glass composite with prism edges) with a WebGL1 field-only fallback. **The top of the ladder.** Also `createOrb()`, the hero's metaball health orb on its own small canvas. |
| `ui-physics.js` | Physics on the DOM, not a canvas: KPI values count up on a spring, catalogue cards tilt toward the mouse on an underdamped spring. Per-frame work is motionKit subscribers that unsubscribe when settled. |
| `helpers.js` | Topbar, clipboard, download, `appRevealFocus` (the `/azure?focus=` scroll), `appShowDialog` (opens an `AppDrawer`) — plus the Radzen accessibility repairs (`appRepairRadzenA11y`). Unrelated to the animation layers. (The `appMedia` matchMedia bridge went with the resource explorer's data-grid branch — it had exactly one caller.) |
| `theme-kit.js` | The visitor's light/dark choice and the compact-density flag, plus `window.appSettings` — the one facade `/settings` reads (`read()` → `theme|density|sound|fx`) and writes all four preferences through. Applies `data-app-theme` to `<html>` and rewrites the two Radzen theme sheets' `media` attributes. Must load before Blazor. |
| `app-boot.js` | Host-page boot: dismisses the loading splash, drives the nav progress bar, filters hot-reload console noise. Not an animation layer — no rAF. Was inline in `App.razor` until the CSP banned inline script. |
| `blazor-hooks.js` | Registers the enhanced-navigation callbacks. Must load **after** `blazor.web.js`, which defines `Blazor`. Also ex-inline. |
| `command-palette.js` | Ctrl+K / Cmd+K overlay: navigation (Settings included — it holds no setting toggles of its own), and a launcher — open / copy link / show uptime / repo / GitHub Actions for every app (from `/api/portfolio`, fetched on first open; repo rows come from the catalog's `githubRepo`. There is deliberately no Azure portal row: a portal deep link carries the subscription id every anonymous endpoint here masks) plus "Rescan Azure" when management actions are on. Fuzzy matching is hand-written: the CSP's `script-src 'self'` rules out a CDN library. |

**There is no service worker and no offline mode** (owner's call, 2026-10-19). One served every
page and script cache-first under a hand-bumped cache name, so a returning visitor saw the
PREVIOUS deploy until someone remembered to bump it — observed live: a header with no Settings
link. `wwwroot/service-worker.js` survives only as a self-removing stub (deletes its caches,
unregisters, has no fetch handler) because a browser that installed the old one keeps it until
that URL serves a replacement. Nothing registers it. Delete the file once old installs have aged out.

**One rAF loop, and reduced motion stops it.** Every animated layer is a named `motionKit`
subscriber. Nothing else may call `requestAnimationFrame` for animation. The governor's gate
is the *only* pause path and it truly cancels the callback — a layer that fades to
`opacity: 0` while still being scheduled still burns a phone's battery, so
`motionKit.stats().running` is what the E2E test asserts. It also measures its own dispatch
cost and steps render scale and particle count down when the device cannot afford them
(budget 6ms; both GPU tiers measure ~3ms on a desktop).

**The catalog starfield is gone (2026-10-19).** `starfield-backdrop.js` (593 lines) and a
vendored 608KB `three.min.js` drew a globe and an orbit field on `/` only, with the catalog's
HRTF fleet hums and the Sync arpeggio riding on it, plus a `motionKit.suppress()` API that
existed for its exclusivity claim. All of it went together; `/` now draws the same shared
backdrop as every other page.

**Silence is the default and it is an accessibility floor.** `audioKit.enabled` starts false;
the `AudioContext` is not even *constructed* until a user gesture, because one built during
page load is a suspended context that silently swallows sound plus an unrequested hardware
claim. Sound is switched on from `/settings` or `/azure`'s Listen
button; the header toggle and its `[data-sound-toggle]` wiring went on 2026-10-05.
`[data-sfx]` / `[data-sfx-hover]` are wired by **delegation on `document`** (enhanced
navigation re-inserts the header, so a direct listener would be lost or double-bound). Sound carries information in two places — the
refresh lifecycle (a ~30s scan is exactly how long it takes someone to switch tabs; its end
is a boom plus a motif that differs for success, recovery, failure and timeout) and the
`/azure` sonification. Each is a second *modality* on facts already on the page, not a
second copy of them. Only primitives cross the interop boundary.

**The backdrop is a capability ladder**: WebGL2 → WebGL1 → the CSS grid in
`modern-ui.css`. Every rung degrades silently to the next and records the outcome in
`#app-gpu-backdrop`'s `data-gpu`. A **WebGPU rung sat on top until 2026-09-05**
(`gfx-webgpu.js`, 524 lines, compute-shader particles, lazily fetched behind
`navigator.gpu`) and was removed: a second complete renderer, in a second shader language,
for a decorative layer whose WebGL2 rung draws the same thing at the same measured ~3ms.
Two implementations of one decoration is the most expensive code in the repo to keep
honest, because neither can be verified except by looking at pixels. Re-adding it also
means re-adding the canvas swap it forced — `getContext` is sticky per element, so a canvas
offered to WebGPU can never yield a WebGL context, and `build()` had to clone and replace
the node mid-init (reassigning `canvas` *before* `status()` ran, or the diagnostic landed
on a detached node). `build()` is synchronous now.

**Workflow moments drive the event effects, most of them uniforms on passes that already run.**
`gpu-backdrop.js` maps moments to renderer calls: the rescan's progress is a radar sweep;
its end is a shockwave from `[data-fx-origin="refresh"]` (red plus a colour-split tear on
failure); a scan that takes the fleet from broken to all-green (`refreshEnd('recovered')`,
decided in `RefreshAsync`) adds a particle bloom from `[data-fx-origin="health"]`; the particle
field orbits every visible `[data-fx-attract=weight]` (down/degraded uptime rows, spend
spikes); `/users` calls `appFx.comets(n)` once per visit for sign-ins in the last 24h; the
pointer (or phone tilt, Android only) lights the glass rims. The scan lifecycle reaches the
visuals as an `app:refresh` DOM event that `audio-kit.js` dispatches **before** its sound
gate, so the dashboard's existing `audioKit.refresh*` calls drive both layers and neither
references the other. Deferred effects run on a frame-advanced clock, not wall time, so a
scan that ends in a background tab plays its shockwave on return.

Added 2026-10-19, each documented in its shader's comment. **Fluid (and the scars that need
it), flocks, the orb and the palette jump are opt-in** — `/settings` → Effects → Full
(`localStorage` `pops:fx`, `appFx.setFull`). The default "Simple" set is the field,
particles, glass, bloom and the scan effects, so it is the one that has to look finished:
- **Fluid.** A ~128-cell stable-fluids step (splat → advect → divergence → 12 Jacobi →
  gradient) displaces the field and carries the particles. The pointer stirs it; the
  shockwave, bursts and the palette jump splat into it; glass rects add drag so flow bends
  around cards. Needs `EXT_color_buffer_float`; without it the sims are simply off.
- **Flocks.** `[data-fx-flock="up|warn|down"]` (uptime row labels, ≤8) gives each service a
  slice of 35% of the particles, coloured by health, circling a point beside its row (or
  a slow Lissajous path when the row is off screen). Separation comes from a splatted
  density field — its gradient is CAPPED, because uncapped it smeared every flock into a band.
- **Scars.** Gray-Scott coral grows out of the uptime card's edges beside each row with bad
  days, radius scaled by them. It runs on **RGBA32F**: on half float the per-step increments
  near 1.0 round away and the pattern silently stays a dot. It is stamped once, noisily — a
  constant point source grows concentric target rings, not coral.
- **Bloom and prism edges** in the composite: bloom reuses the Kawase pyramid plus one
  quarter-res step; glass edges split R/B along the edge normal, strongest near the pointer.
- **Hero orb** (`canvas[data-fx-orb]`, its own context: the hero is 88% opaque glass, so the
  backdrop would show through at 12%). Wyvill-kernel metaballs, one bead per service; the
  1/d² kernel's singularities cratered the shading. Hidden by CSS until it is drawing.
- **Palette** (`app:palette` events from `command-palette.js`): a lens toward the open
  palette; navigating plays a hyperspace jump (particles + fluid rush out, radial streaks),
  the target header link morphs into the new h1 through a View Transition, and in **dark
  theme only** the canvas is screened over the page for ~0.9s (on a light page it whited out).

**Sound and touch follow the visitor's one opt-in.** `[data-sfx]` clicks are panned to the
element's screen position; the sonification's stereo width *is* fleet health and its ticks
walk left→right. `navigator.vibrate` mirrors the outcome sounds and sits behind the same
`enabled` flag — no buzz for a visitor who never turned feedback on.

**Four pieces of polish are plain CSS/platform.** MetricBars spring in on `--app-ease-bounce`
(a damped spring sampled into `linear()`), drawn on the fill's `::before` so the fill's own
box — what `Azure_GlancePanels_DrawTheirMetrics` measures — is final from the first frame. The theme toggle is a circular View
Transition reveal from the button (`theme-kit.js` + `.is-theme-reveal` in `modern-ui.css`;
instant under reduced motion or without support). Cards and panes rise in on a scroll
timeline (`animation-timeline: view()`, entry range, backwards fill, **opacity + translate
only** — a `scale` there shrank off-screen panes and failed the pane-geometry test). The three
accent tokens are `color(display-p3 …)` on wide-gamut screens, luminance-matched to the sRGB
values so no contrast ratio moved; the WebGL canvas switches to `drawingBufferColorSpace =
'display-p3'` to match, and both backdrop colour readers parse that syntax.

**`data-glass` is the opt-in for shader-side glassmorphism.** `gpu-backdrop.js` collects the
on-screen rect of every tagged element (rate-limited, signature-guarded, corner radius cached
per element — `getComputedStyle` forces a style resolution and was the largest single cost)
and the renderer blurs and refracts the backdrop beneath them. This is what replaced the 23
per-card `backdrop-filter` roots: one blur for the whole page, in a pass that already runs.
CSS's only job is `html[data-gpu-backdrop] [data-glass]`, which makes those surfaces
translucent enough for it to show — gated so that a machine with no WebGL does not get
see-through cards over a flat background.

### Four bugs here that all failed silently — check for them before adding shader code

1. **An opaque `<body>` background hid every backdrop layer.** `#app-gpu-backdrop` and the
   `body::before` grid are both `position: fixed` with **negative** z-index, and negative-z
   descendants paint *before* an ancestor's in-flow block background. So `body` painting a
   colour covered both. The page colour now lives on `html`, and `html body` carries
   `background: transparent !important` — the `!important` is needed to beat something in the
   Radzen theme sheets. Proven by forcing the composite shader to solid opaque red and
   watching the page still render navy.
2. **`smoothstep(hi, lo, x)` is undefined** in both GLSL and WGSL when `edge0 > edge1`. It
   compiles, the layer reports itself healthy, and the driver may return zero everywhere.
   Always write `1.0 - smoothstep(lo, hi, x)`.
3. **`curl()` returns a true derivative** (the finite difference is divided by `2*epsilon`),
   so its magnitude is ~7, not ~1. Multiplying it by 0.085 displaced every sample point
   outside its own gaussian falloff and the whole field evaluated to zero — again with no
   error and no artefact. Check the *magnitude* of a noise term against the coordinate range
   it is perturbing.

4. **The canvas is non-premultiplied** (`premultipliedAlpha: false`), so anything added to
   `col.rgb` in the composite is multiplied by the field's near-zero alpha on screen — an
   effect lands at roughly its intensity squared. The shockwave, sweep and burst glow were
   all invisible this way while the layer reported `webgl2`. Emissive terms go into `emit`
   and are folded in premultiplied at the end of `COMPOSITE_FS`. The same trap hid the
   particles themselves (their blend squared alpha), which is why the particle pass uses
   `blendFuncSeparate(SRC_ALPHA, ONE, ONE, ONE)`.

The common thread: a decorative GPU layer has no failure mode that surfaces on its own. If
you change a shader, look at the rendered pixels — `dataset.gpu` reporting `webgl2` only
proves a context and program were created, not that anything reached the screen.

## Cross-cutting decisions

- **A projection component gets a `ShouldRender` guard.** Blazor treats a reference-typed
  parameter as "may have changed" on every parent render, so without one, a page render walks
  every child — 23 portfolio cards, a 30-day uptime grid, ~48 resource explorer cards. The
  guard is reference identity on the data (`AzureUptimeHeatmap`, `AzureCostForecast`,
  `AzureWhatChanged`, `AzurePriorityQueue`, `MetricBars`, `PortfolioAppCard`) plus any scalar that
  changes what is drawn (`AzureResourceExplorer` also compares `SelectedView`).
  This is sound only because the page **replaces** its DTOs rather than mutating them. Two rules
  follow: never include a `Func`/`EventCallback` parameter in the comparison — those are fresh
  objects every render and would make the guard a no-op — and **never add internal state to a
  guarded component**, because `ShouldRender` is consulted on its own `StateHasChanged` too and
  would block it. `AzureStatusNarrative` is deliberately unguarded for exactly that reason.
- **A hot signal must not re-render the page that shows it.** `POST /api/diag/refresh` streams ~20
  progress messages over ~30 seconds; each one used to call `StateHasChanged` on `AzureDashboard`.
  The strip lives in `AzureRefreshProgress` and the hub calls its `Update()` method directly — a
  method, not a `[Parameter]`, because setting a parameter requires the parent to render, which is
  the whole cost being removed. The page still renders on the transition that ends the scan.
- **Decorative layers ask the device first.** `motionKit.minimal` (Save-Data, `deviceMemory <= 2`,
  `hardwareConcurrency <= 2`, 2G) is a separate question from the quality tier: the tier says *how
  well* to draw, `minimal` says *whether to*. Under it the GPU backdrop never sets
  `data-gpu-backdrop`, so the CSS fallback stays.
  Both hint APIs are Chromium-only — every check is written so a missing hint reads as "no
  objection". The default is to draw.
- **Snoozing was removed (2026-10-19)** — `SnoozeStore`, three `/api/diag/snooze*` endpoints,
  `AzureSnoozedItems` and the shatter effect its button triggered. On a one-person dashboard
  with a ranked queue it mostly hid things that wanted fixing. So did the "Audit script"
  download (`/api/diag/automation-script`, an 805-line PowerShell report generator that
  duplicated the scan). The `snoozes` partition already in Azure is untouched.
- **Dead private code fails the build.** `.editorconfig` raises IDE0051 (unused private member)
  and IDE0052 (written, never read) to error, and `EnforceCodeStyleInBuild` in
  `Directory.Build.props` runs them at build time. Turning it on found two dead fields at once.
- **Every endpoint needs a consumer.** An endpoint whose only caller is its own test is dead code
  with a green check mark. Three whole slices (GitHub, Infra, Pinger) were deleted in 2026-07 for
  exactly that — all tested, none reachable from the UI. Before adding a route, know what calls it.
- **No real login, by design.** No `/auth/*`, no `AuthenticationStateProvider`, no OAuth, no login
  UI. [Host/FakeAuthHandler.cs](src/PoPunkouterSoftware.API/Host/FakeAuthHandler.cs) reads
  `X-Fake-User` / `X-Fake-Roles` to build a `ClaimsPrincipal` purely so a caller can opt into the
  `management` role; it is registered **only outside Production** and throws in its own constructor
  under Production. In Production no scheme is registered at all and the `Management` policy denies
  outright rather than challenging (a challenge with no handler is a 500). Nothing exposes the
  principal over HTTP any more: `/api/whoami`, `/api/logout` and `/api/impersonate` were affordances
  of the deleted Session/Logout slot, had no caller left, and were removed under the
  "every endpoint needs a consumer" rule — `/api/config` is now the only route in the Config slice.
  Do not add a real identity provider unless the owner reverses this.
- **The header is just the brand, at every width.** The four links stay in `MainLayout`'s markup
  but are `display: none` everywhere (owner's call, 2026-10-20: "the links exist but hidden from
  UI"; phones only since 2026-10-19, when a hamburger drawer that dropped over the page went).
  The other pages are reached from the brand link home, the 404 page's list, Ctrl+K, or a typed
  URL. The palette's View Transition still looks the link up and simply finds it unrendered.
  The empty `.app-topbar-session` div is a leftover spacer from the centred nav.
- **No login UI in the header, deliberately.** `MainLayout.razor` is statically server-rendered
  (`<Routes />` carries no render mode), so `@onclick` never fires and its injected `HttpClient` has
  no `BaseAddress`. A Session/Logout slot and "MOCK DATA" banner were built, could never render, and
  were deleted rather than kept as chrome that only looks functional (2026-08-03). Reinstating either
  requires a small `InteractiveWebAssembly` island in the header, not a change to `MainLayout`.
- **`<RadzenComponents />` in `MainLayout` carries its own `@rendermode`, and must.** It hosts the
  notification/dialog/tooltip outlets that `NotificationService` pushes into. Without a render mode
  it inherits the layout's static SSR: the `.rz-notification` container sits in the DOM but can
  never gain a child, and the page's `NotificationService` — resolved from the WASM container, a
  different one from the server's — pushes into an outlet that is not listening. Every toast in
  the app was silently swallowed, *every error path included*: "Refresh rejected", "Refresh
  failed", "Timeout", "Copy failed". Interactive WASM islands on one page share a single
  WebAssembly host and therefore one DI container (a scoped service is a per-host singleton there),
  which is why the island and the page resolve the same instance. Do not remove the attribute.
- **Push alerts go through ntfy, and every sender fires on a crossing, never a state.** `AlertNotifier` POSTs JSON to
  `Alerts:NtfyServer` (default ntfy.sh) when `Alerts:NtfyTopic` is set — Key Vault secret
  `PoPunkouterSoftware--Alerts--NtfyTopic`, because on ntfy.sh the topic name IS the credential.
  Senders: `ServicePingerService` on a debounced up/down transition (`OutageTracker`: 2
  consecutive failed probes, timeouts are no observation — the uptime grid's own rule, so an
  alert cannot disagree with the grid it links to); `ReportRefreshRunner` on a failed scan, on
  a NEW spend spike (anomalies the previous report already carried are not re-sent), and — via
  `AlertTriggers`, compared against the previous report — on an SSL certificate crossing 14
  days, on the month-end forecast crossing `Budget:MonthlyUsd`, on a page newly failing its
  catalog content check, and on a custom domain crossing 30 days to expiry. Outage alerts link to
  `/azure?focus=<service>`, which highlights and scrolls to that uptime row.
  Tracker state is in-memory: an app still down when this site restarts is re-alerted.
  Two senders live in GitHub Actions because only they hold the facts: `deploy.yml` on a
  rollback and `screenshots.yml` on a Lighthouse category dropping 15+ points overnight. Both
  read the optional `NTFY_TOPIC` GitHub secret and fall back to the Key Vault secret (which
  works only if the OIDC identity can read `kv-poshared2`); with neither they stay silent.
- **Two anonymous endpoints are rate-limited, globally.** `Host/RateLimits.cs`: `/api/signins`
  (live Log Analytics) 30/min, `POST /api/diag/ai` (paid completion) 20/hour. Global windows,
  not per-IP, on purpose — a hard cost ceiling IP rotation cannot beat, and no trust in
  `X-Forwarded-For`. Rejections are 429 + `Retry-After`. `ProductionBootTests` pins it.
- **`/api/portfolio/badge/{id}.svg` is consumed from OUTSIDE this repo** — each app's GitHub
  README embeds it. It is the one endpoint whose caller is not the WASM client, which is why
  the "every endpoint needs a consumer" rule is satisfied by a README, not by a component.
  Built from the same `BuildApps` list as the cards plus `BuildUptime`, with none of
  `GetPortfolio`'s side effects (no auto-rescan, no screenshot capture) — crawlers fetch it.
  It carries the clean streak ("up 12d · 99% 30d"): `UptimeRow.CleanDays`, OBSERVED days since
  the row's last down/degraded day — an unobserved day is not a clean one (it used to count
  them, so every card claimed "30d+" off one day of data) — which `/api/portfolio` also puts on each card
  (`PortfolioApp.CleanDays`, the flame chip on the card's host line — on that line, not a new
  one, because a taller card fails `Home_FirstScreen_ShowsAWholeCard`).
- **Every scanned service carries its last deploy.** GitHub correlation now runs for all services,
  not only broken ones, and asks for the latest **push**-event run on the default branch (a
  scheduled workflow would otherwise read as a deploy every night). It becomes
  `WebService.LastDeploy` → `ServiceHistoryPoint.LastDeployAt` → `UptimeRow.DeployDays` (dots
  on the grid) and a `deploy` change in "what changed", which an outage line cites when a
  deploy landed between the two scans. The matched repo's open Dependabot alert count rides the
  same correlation (`WebService.OpenSecurityAlerts` → the shield chip on the uptime row); it is
  null, not zero, when the repo has Dependabot off or the PAT lacks "Dependabot alerts: read".
  As of 2026-10-19 Dependabot alerts are **disabled on every Po repo**, so the chip never shows.
- **The scan reads three more Azure sources, all covered by the existing Reader grant.**
  `DowntimeDiagnosisService` asks **Resource Health** for each down app: `PlatformCaused` true
  becomes "Azure platform event", false "app-side, Azure reports the platform healthy", and
  the verdict rides on the first-paint attention sentence. **Azure Advisor**
  (`AzureReportService.Advisor`) feeds the priority queue as ONE row per recommendation, not
  per resource — live it returned 75 rows, all HighAvailability ("zone redundancy", "paid
  tier") — and only High-impact Cost/Security count toward the hero, or the hero would never
  say "everything looks good". A subscription-level recommendation's bare GUID is replaced
  with "subscription": `MaskSubscriptionIds` only rewrites `/subscriptions/<guid>` paths.
  **Exceptions per app over 24h** (`AzureReportService.AppErrors`) come from the shared App
  Insights component at `SignIns:ResourceId` — stored with the scan, so this is not a second
  live query — and carry the exception TYPE only, never the message, because messages can
  hold user data and this reaches the anonymous summary. Shown as the bug chip on the uptime
  row; null (no chip), never a manufactured 0, for an app that logged nothing. The top ten types
  per app, with first/last seen, are the app drawer's "Exceptions, 24 hours" table.
- **"Up" is checked past the status code, from four keyless public APIs (2026-10-30).** All go
  through the `public-api` HttpClient, never `github`, so the PAT stays with GitHub. Each is a
  scan step in its own partial and degrades to "no data", never a failed scan:
  - **Content checks** (`.ContentChecks`): an `apps.json` entry may set `expect` (today each
    app's `<title>`); the scan GETs the page and checks the body. Only a 2xx WITHOUT the text
    fails — a timeout or error status sets `Error` and claims nothing, the pinger's own rule.
  - **Packages** (`.Packages`): the matched repo's `Directory.Packages.props` (every Po .NET
    repo pins centrally; a `$(Property)` version is skipped), newest stable from nuget.org's
    flat container, advisories from ONE OSV.dev batch query. This is what the shield chip
    counts now that Dependabot is off everywhere (`DashboardDerivations.VulnerabilityCount`).
  - **Tier prices** (`.Pricing`): Azure Retail Prices for B1/S1/P0v3 per region and OS
    (`- Linux` suffix), ×730h — the app drawer's Plan row. Discovery normalises ARM's
    "West US 2" to "westus2" for it.
  - **Domains** (`.Domains`): RDAP (`rdap.org`) expiry of every custom host name on a site or
    Static Web App. **No Po app has a custom domain today**, so this step makes no calls; it
    exists so the first one is watched from day one.
  All four, plus a red build (the latest push run's `failure` conclusion, already fetched for
  deploy markers), are first-paint attention sentences (`AttentionItemsBuilder`) AND rows in
  the ranked queue (`DashboardDerivations.BuildPriorityQueue`, `FromQuality`) — the queue
  replaces the list in place, so a fact in one only would vanish when it loads. On the uptime
  row: `CI` and `page` chips; the name truncates, never the chips.
- **Spend spikes are per resource group, from one extra Cost Management query.**
  `GetResourceGroupCostsAsync` (30 days × daily × group, no nextLink paging — see its ponytail
  note) fills `CostInfo.ResourceGroups`; `DashboardInsightsBuilder.BuildCostAnomalies` flags a
  group whose latest day is ≥$1 over, ≥2× and >3σ above its own 14-day mean. Shown on the
  forecast card, capped at two lines so the spend pane still fits a phone.
- **Management gate.** Mutating/expensive endpoints (`/api/diag/refresh`, `/api/diag/cancel-refresh`,
  `/api/diag/restart/{name}` — the Restart button on Reliability rows of the priority queue)
  carry `.RequireManagementActions()`, which enforces `FeatureFlags:EnableManagementActions` (on in
  Development/Testing, otherwise opt-in) plus, **in Production only**, `Security:ManagementApiKey`
  via the `X-Management-Key` header (Key Vault `PoPunkouterSoftware--Security--ManagementApiKey`).
  Production only because a local run loads the whole shared vault through `az login`, key
  included, and would 401 its own Rescan. `/api/config` reports `managementActionsEnabled: false`
  in Production whatever the flag says — the browser never holds the key. `/api/diag/ai` is
  deliberately unprivileged.
  Restart resolves the site from the STORED report, never from the caller, so it can only touch
  a site the scan found; it starts a stopped site and restarts a running one. In Azure it also
  needs Website Contributor, which infra deliberately does not grant — it 403s cleanly there.
  Note a local Development run restarts the REAL site with your `az login`.
- **Routing.** `/api/diag/*` and `/api/portfolio/*` use `MapGroup`. `/health` (deep probe, one
  `IHealthCheck` per external dependency) and `/healthz` (static liveness) sit off the `/api` group.
  **`/openapi/v1.json` and `/scalar/v1` are mapped only outside Production** — they were
  unconditional, so the live site published its full route table, parameter shapes and response
  schemas (management routes included) to anonymous callers, for an API whose only client is
  compiled from this same solution.
  There is deliberately **no** `/api/health` alias, and deliberately **no** bare `/diag`: it was an
  unauthenticated page rendering ~130 lines of hand-built HTML with its own dark-only palette — a
  second design system — whose only caller was its own smoke test, and whose `?format=json` twin
  returned the server's absolute ContentRoot path, the environment name and masked-but-suffixed
  connection strings to anonymous callers in Production. Removed 2026-09-04.
- **An unmatched path gets the app, not JSON.** A browser that mistypes a URL, or follows a link
  to an app that was renamed, must land on the app's own chrome: shell, header, skip link, a way
  back, and a **real 404 status**. That is `NotFoundPage.razor`, rendered by
  `UseStatusCodePagesWithReExecute("/not-found", "?statusCode={0}")`. It is not a page in the
  page budget — nobody navigates to it — but it is a route, so the count of routes is now
  five.
  **Two ways to get this wrong, both tried here, both caught by tests and not by looking:**
  a plain `MapFallback` returns the right status with no page (it swallowed every unmatched path,
  so `<NotFound>` in `Routes.razor` was dead code for a browser navigation); a **catch-all route**
  (`@page "/{*path}"`) returns the page but a **200**, because a catch-all matches by definition
  and a component cannot set `Response.StatusCode` — the response has already begun by the time
  `OnInitialized` runs. Only the re-executing status page gives a real 404 with a real body.
  Do not "simplify" it back into a fallback or a catch-all. Unknown **`/api/*`** paths keep
  answering JSON (`app.Map("/api/{**rest}")` in [Program.cs](src/PoPunkouterSoftware.API/Program.cs)),
  and `NotFoundRouteRegressionTests` pins both halves in one test.
- **A health check does the real operation, with the app's own credential.** Every check used to be
  an anonymous `HttpClient.GetAsync` at the dependency's URL, which proves DNS and TLS and nothing
  else: Key Vault read "healthy" off an HTTP **404** and Table Storage "degraded" off a **400**,
  both meaningless, through a period when the app could not read a single Azure resource. They now
  list one page of secret properties and query one table row. `AzureInventory` is the check that
  would have caught it: ARM answers an *unauthorized* list with an **empty page, not a 403**, so a
  scan with no Reader role completes and stores a report describing zero resources — the dashboard
  then renders truthful zeros and nothing anywhere reports a fault. **No check returns Unhealthy**,
  deliberately: `MapHealthChecks` answers Unhealthy with 503, and this app has no hard dependency
  (see "Degradation is a designed path"). A fault shows as `"status": "degraded"` plus the check's
  own verdict. The masked `config` block is a development diagnostic and is **omitted in
  Production** — `/health` is anonymous, and it was publishing the environment name and a
  masked-but-suffixed vault URI whose last four characters name the vault.
- **`/api/diag/report` masks the subscription id.** It is anonymous by necessity (the Advanced
  diagnostics panel is WASM with no credential) and returns every resource id in the estate.
  `SecretMasking.MaskSubscriptionIds` rewrites `/subscriptions/<guid>` to `/subscriptions/****` on
  the serialized JSON — on the JSON, not the object graph, because the id appears inside free-text
  strings across a dozen nested record types and a per-record rewrite would miss whichever one is
  added next. Resource groups and names stay: they are already public in the hostnames the
  portfolio links to. Use `MaskedJson`, never `Results.Json`, for a report in that slice.
- **`/api/signins` masks every email address, and the mask keeps the last character on
  purpose.** The `/users` roster is the app's only payload containing personal data about
  *other people* — the live data includes an address at a customer domain, not just the
  owner's. The endpoint is anonymous for the same unavoidable reason `/api/diag/report` is
  (the page is WASM with no credential, and this app has no identity provider), so masking
  happens server-side in `SecretMasking.MaskEmail` before anything reaches the wire, and
  `SignInReportBuilderTests` asserts it over the whole serialized payload rather than field by
  field so a field added later cannot quietly bypass it. **The display name is masked too**
  when it looks like an address: several providers send the email *as* the display name, and
  masking only the email column while printing the same address in the name column next to it
  is not masking. The local part keeps its first two characters **and its last one** because
  a prefix-only mask rendered `punkouter26@gmail.com` and `punkouter27@gmail.com` — two
  different people, same display name — as one identical roster row; a mask that collapses
  distinct identities makes the page assert something false, which is worse than not masking
  at all. The domain survives because "someone at a company tried it" and "another throwaway
  gmail" are the two readings the page exists to support.
- **Sign-in rows are grouped by email address, never by `UserId`.** The "Po Sign-ins" workbook
  groups on the object id and is wrong to: the same human gets a different id per app
  registration, so the owner's own account rendered as two people with half the sign-ins each.
  `SignInReportBuilder.IdentityKey` falls back to the id only when no address is present.
- **Security headers come from `Host/SecurityHeaders.cs`, and the CSP forbids inline script.**
  They lived in `wwwroot/staticwebapp.config.json` — Static Web Apps configuration, in an App
  Service app, read by nothing — so production sent no CSP, no `nosniff` and no Referrer-Policy
  while a file in the repo said otherwise. A control that exists only in an unread file is worse
  than none, because it stops anyone noticing the gap. `script-src 'self' 'wasm-unsafe-eval'`
  carries **no** `'unsafe-inline'`, which is why App.razor's boot and Blazor-hook blocks are now
  `js/app-boot.js` and `js/blazor-hooks.js`: **a new inline `<script>` in the host page will
  silently stop running** — put it in a file under `wwwroot/js`. `style-src` keeps `'unsafe-inline'`
  because Radzen and Blazor both write inline style attributes. `X-Powered-By` is stripped by
  `web.config`, not middleware: on Windows App Service IIS appends it downstream of the managed
  pipeline, so a middleware `Remove()` is a no-op that reads like a fix.
- **Resilience.** Typed clients `github` and `azure-arm` use `AddStandardResilienceHandler`.
  `health`, `azure-probe`, `public-api` and `ai-hf` deliberately have **none** — they must report real reachability
  (or degrade), not retry through the outages they exist to detect.
- **Named `HttpClient`s are pooled and shared.** Never reassign `DefaultRequestHeaders` on an
  instance from `CreateClient(name)` — handler chains are pooled, so a per-call mutation leaks that
  header, credentials included, to every other consumer. The `github` PAT is bound once in
  `Program.cs`. Pass per-call values on the `HttpRequestMessage`.
- **The theme is a choice, not only a media query.** `theme-kit.js` owns it: the `/settings`
  Theme row (System / Light / Dark, via `appSettings.set`) writes
  `localStorage` `pops:theme` and sets `data-app-theme` on `<html>`. Rejected alternatives, so they
  are not re-litigated: Radzen's `RadzenTheme`/`RadzenAppearanceToggle` would make a second theme
  authority alongside this app's own token block, which is the same class of bug as the Radzen token
  bridge; `light-dark()` would collapse the palette to one line per token but fails *silently and
  totally* on a browser that lacks it, because every token becomes invalid at computed-value time.
  Consequences to keep in step:
  - **The light palette exists twice** in `modern-ui.css` — once under
    `@media (prefers-color-scheme: light) { :root:not([data-app-theme="dark"]) … }` (the no-choice
    case, which applies before any script runs and so cannot flash) and once under
    `:root[data-app-theme="light"]`. CSS has no way to share a declaration block across those two
    selectors. **Change one, change both.** `boot.css` and `.azure-ops-page`'s `--tone-*` block have
    the same two-place shape, and the tones matter most: they are data-viz strokes graded against a
    3:1 non-text floor, and the old plain media query left a light green at 2.91:1 on a dark card.
  - **Radzen's two theme sheets are switched by rewriting their `media` attributes**, because
    `App.razor` scopes them with media queries and no CSS selector can reach a stylesheet's `media`
    attribute. Skipping that leaves every Radzen surface (badges, buttons, chart legend) on the
    system's theme while the page around them flips.
- **Radzen's own markup is repaired from `js/helpers.js`, not at 22 call sites.** Two defects in
  what the library renders: an icon is real text (`<i class="rzi">refresh</i>`), so every Radzen icon
  button's accessible name is the ligature name plus its label — measured as `"refresh Rescan
  Azure"`, `"graphic_eq Listen"`, `"timelineSign-ins over time"`; and chart legend items ship
  `aria-disabled="False.ToString().ToLowerInvariant()"`, a literal from an expression that never ran.
  `appRepairRadzenA11y` fixes both from one `MutationObserver` — `aria-hidden` cannot be set from
  CSS and `RadzenButton` exposes no way to reach the `<i>` inside itself, so per-call-site fixes mean
  22 places to remember in markup whose point is that a new Radzen control just works. The
  hand-written `<span class="material-symbols-outlined" aria-hidden="true">` glyphs in `MainLayout`
  were always correct, which is exactly why this went unnoticed: it only affects Radzen's path.
- **A hint is `AppHint.razor`, not a `title`.** Native `title` never appears on touch and is not
  announced on focus, so ~40 of them — the exact read time, the "auto-updates"
  explanation, the config keys that would unlock refresh — were desktop-mouse-only. `AppHint` wraps
  Radzen's `TooltipService` (mouseenter, focus, click) and renders **no** `title`: keeping one as a
  fallback prints a native tooltip under the Radzen one a second later. `Focusable` is opt-in per
  use — a hint on each of twelve catalogue cards would otherwise add twenty-four tab stops.
  With **no child content** it renders an ⓘ glyph plus the text as sr-only content: that is
  where read-once prose goes (methodology, owner-facing config keys) instead of a paragraph.
- **Drawers are native `<dialog>`s, not Radzen's side dialog.** `AppDrawer.razor` renders a
  `<dialog>` INSIDE the page and opens it with `showModal()` (`js/helpers.js` `appShowDialog`);
  closing is a `<form method="dialog">` button, Escape, or `closedby="any"` on the backdrop.
  `DialogService.OpenSide` was rejected because it renders in the `<RadzenComponents>` outlet in
  `MainLayout`, outside the page: the content loses the page's state (a filter click would not
  re-render it) and every `.azure-ops-page ::deep …` rule, so it arrives unstyled. `showModal()`
  lifts the dialog into the top layer without moving it in the DOM, so scoped CSS still reaches it.
  Two consequences: **no Radzen popup inside a drawer** (a `RadzenSplitButton` menu escapes the
  modal and is then inert — the diagnostics download is two plain buttons for that reason), and
  the drawer background is the surface tone laid over `--app-bg`, fully opaque, because the GPU
  glass pass never sees the top layer. `/azure` has two: Advanced diagnostics (it was an inline
  `.app-pane--tall` pane) and one per app, opened from an uptime row name (`AzureAppDetail`: the
  row's sentence, response time, attention lines and changes naming it, and its reach/cost).
- **KPI tiles, disclosures and tables are shared classes** in `modern-ui.css`: `.app-kpis`,
  `.app-disclosure` (`__heading`, `__title`, `__body`), `.app-table` and `.app-table-scroll`.
  Each used to exist once per page stylesheet and the copies had drifted. A page stylesheet
  keeps only what is page-specific; its scoped (0,3,0) rules still win over these where it must
  differ. A catalogue card has ONE link — the title, stretched over the card by `::after`.
- **Radzen's four status colours are bridged in the token block, and badges were unreadable without
  it.** The bridge mapped `--rz-primary*` only, so `.rz-badge-<style>` kept the theme's pastel fill
  with white text on top: `"3 actionable"` measured **1.64:1**, `"AI"`/`"Current"` 2.29:1,
  `"1 security"` 2.63:1 — all below AA at 12px, in both schemes. `--rz-success/--rz-warning/
  --rz-danger/--rz-info` and their `--rz-on-*` pairs are now dark fills with white text (5.9–6.7:1).
  A `.rz-badge-danger.rz-variant-fill` override used to sit in the stylesheet claiming to fix the
  danger badge; Radzen emits `rz-variant-filled`, never `rz-variant-fill`, so it matched nothing and
  the badge it was written for stayed at 2.63:1 — do not re-add a per-style badge override.
- **Package versions go in `Directory.Packages.props` only** (Central Package Management, transitive
  pinning on). Some entries are *security pins* for transitive packages with no direct reference —
  `Microsoft.OpenApi` is one; removing it because "nothing references it" reintroduces a vulnerable
  version. `MinVer` is referenced from `Directory.Build.props`, not from any csproj.
- **Status narrative (AI).** `AiTriageService` calls **Azure AI Foundry** chat completions on the
  shared `po-aiservices-shared2` account in the `poshared` resource group — deployment
  `gpt-5.4-nano`, the cheapest one there that writes a coherent paragraph. **The prompt asks for
at most two sentences that do NOT repeat the numbers the hero tiles print** — "use the actual
numbers" produced ~90 words restating health %, cost, projection, budget and three cost drivers
(2026-10-19). The rule-based fallback follows the same rule. Changing the prompt means bumping
the `"narrator:N"` input in `StatusFacts.ToHashInputs`, or cached text from the old prompt is
served until the facts happen to change. It produces the
  plain-English lead paragraph on `/azure` from the full fact set assembled by
  `StatusFactsBuilder`, not from the attention list alone. On by default
  (`FeatureFlags:EnableAiSummary`), and it degrades to a rule-based narrative
  (`BuildNarrativeFallback`), never a dead "unavailable" state. The per-scan paragraph is
  precomputed by `ReportRefreshRunner` and attached to the report before
  `AzureReportStore.SaveAsync`, so it round-trips through the existing storage paths;
  `POST /api/diag/ai` serves only the on-demand "Rewrite this" button. Regeneration is skipped
  (`Source: "cached"`) when the fact hash is unchanged. There is deliberately exactly **one** AI
  client — this replaced the Hugging Face `flan-t5-base` integration wholesale.
  **Auth order:** `StatusNarrator:ApiKey` → Entra ID via the shared `TokenCredential` → the app's
  existing `AzureOpenAI:ApiKey` vault secret. The keyless path needs the `Cognitive Services OpenAI
  User` role, declared in `infra/modules/cognitive-services-openai-user.bicep`; note that unlike
  `kv-poshared2` (RBAC **disabled**, so Key Vault access is a classic access policy), Cognitive
  Services data-plane access is always RBAC. Until that role is applied, production runs on the key
  fallback and works either way.
- **Never name a config section after a generic Azure service.** `AppKeyVaultSecretManager` loads
  **every** secret in the shared vault into configuration, and configuration keys are
  **case-insensitive**. The vault holds estate-wide `AzureAI--ApiKey` / `AzureAI--Endpoint` secrets
  pointing at a different account, so a section named `AzureAi` silently inherited a foreign API key
  and every model call returned 401. The narrator's settings live under `StatusNarrator:*`, which
  collides with nothing. Its endpoint and deployment deliberately do **not** fall back to the vault's
  `AzureOpenAI--*` values either — `PoPunkouterSoftware--AzureOpenAI--DeploymentName` is stale
  (`gpt-4.1-nano`, which 404s on that account). Only the API key falls back there.
- **The forecast card's "Spent this month" is month to date** (the burn-rate series the
  projection is extrapolated from), not `Cost.TotalCost30Days`. It was the 30-day total until
  2026-10-19, when the card read "spent $22.01, projected $7.08" — $17.67 of those 30 days had
  landed in September. The 30-day figure lives on the hero's cost tile.
- **History-backed dashboard insights.** `DashboardInsightsBuilder` (pure, Unit-tested) derives
  "what changed since last scan", the month-end cost forecast, and the 30-day uptime grid. All three
  are projected onto `OpsSummary`, so they cost the first paint nothing extra. Two honesty rules are
  load-bearing: an uptime **cell** is worst-wins per day (one bad scan marks the day), but the
  **percentage** counts observations, not days — deriving it from the cells made 2-of-3 healthy scans
  in one day read as "0% uptime" beside a paragraph correctly calling the same app healthy.
- **The uptime grid has two data sources, and needs both.** Full scans are not scheduled: one runs
  only on a manual rescan or when a visitor loads a stale (>12h) portfolio, so an untrafficked day
  would be a blank column. `ServicePingerService` therefore also persists per-day reachability
  tallies (`UptimeSampleStore`, `uptime-samples` partition — one row per service per day, not per
  sweep, or the read path would scan ~35,000 rows). `BuildUptime` merges both: a day counts as
  observed if either saw it, and cells stay worst-wins across sources.
  - **Both writers must key on `NameMatching.ServiceIdentity`.** The grid joins scan rows and ping
    rows on that string. They disagreed once — `HistorySummaryMapper` wrote `PoMemeVideo` while the
    pinger wrote `app-pomemevideo2` — and every service rendered as two half-populated rows.
  - **A ping `timeout` is recorded as neither up nor down.** These are F1 apps that sleep; a 14s
    probe missing a cold start is not evidence of downtime. `unreachable` and 5xx still count.
  - **Both writers must also agree on what "up" means.** They did not: the pinger counted anything
    under 500 as reachable while the scan's `ProbeUrlAsync` judged on `IsSuccessStatusCode` alone.
    The probe runs with `AllowAutoRedirect = false`, so an app that redirects `/` to a
    sign-in page answers 302 — and PoRedoImage and PoRepoLineTracker were both reported
    "unavailable" on the home page while serving normally. The scan now counts any status under
    400 as reachable. Keep 4xx broken: a card is a link a visitor clicks, and PoSeeReview's flat
    403 is a real outage, not an auth handshake.
  - **The scan probes with GET (headers only), the same request the pinger sends — never HEAD.**
    An ASP.NET app with no HEAD route answers 405, so until 2026-10-19 this site's own production
    instance was "broken" and ranked FIX NOW on its own dashboard while serving every GET with a 200.
    The rule is the owner's: an app is up when it answers a plain request with OK.
  - **The app drawer's status is the latest scan's probe (`UptimeRow.Current`), not today's
    cell.** Cells are worst-wins, so one failed ping this morning made the drawer say "Down"
    beside a home card saying healthy.
  - The pinger stops when the app does (F1 has no Always-On), which is why
    `.github/workflows/uptime-scan.yml` exists — see CI/CD below.
- **A local scan uses YOUR `az login`, pinned to the personal tenant.** `launchSettings.json`
  sets `AZURE_TENANT_ID=28e2074a-…` (the punkouter27 account's tenant), which every
  `DefaultAzureCredential()` here honours. Without it, a CLI defaulted to a work account fails
  every scan with `InvalidAuthenticationTokenTenant`. With it, the CLI still needs a live
  login to that tenant (`az login --tenant 28e2074a-9756-49cb-9bde-314aba4d2bbe`); a stale one
  fails with AADSTS90072. Production uses the managed identity and ignores this.
- **Secrets.** Key Vault `kv-poshared2`, prefix `PoPunkouterSoftware--`, loaded at startup via
  System-Assigned Managed Identity, and **skipped entirely under the `Testing` environment**. The
  grant is a classic access policy, not RBAC — the vault is shared across Po* apps, so switching
  models is an estate-wide change.
- **The managed identity needs subscription-wide read, and that is not optional.** `infra/main.bicep`
  granted the site's identity Key Vault (access policy) and Cognitive Services (RBAC) and nothing
  else, so the two data sources the whole app exists to read were the two it could not touch:
  production reported 0 services, 0 resources, `$0.00` and an empty uptime grid while every health
  check showed green. `modules/subscription-inventory-reader.bicep` assigns **Reader**, **Cost
  Management Reader** (a separate data plane — Reader does not cover
  `Microsoft.CostManagement/query/action`, which is why the report said "Cost data unavailable
  (rate-limited or request failed)") and **Monitoring Reader** at subscription scope; main.bicep
  assigns **Storage Table/Blob Data Contributor** on the app's own account. Infrastructure is
  applied out-of-band — `deploy.yml` deliberately does not run bicep — so editing these files
  changes nothing until someone runs `az deployment group create`. All six assignments were
  applied on 2026-09-05. **Run `what-if` before ever deploying the whole template**: it reported
  `tags -> None` on the site, because the portal-written
  `hidden-link: /app-insights-resource-id` was not declared and an omitted `tags` block deletes
  the tag. It is declared now (`appInsightsResourceId`), but the lesson generalises — this file
  claims to describe what already exists, and anything the portal wrote that it does not name,
  it deletes.
- **Telemetry.** Serilog → Console + File; Azure Monitor via OpenTelemetry is the sole App Insights
  pipeline (`writeToProviders: true` on `UseSerilog` is load-bearing — without it application logs
  never reach App Insights). Traces are fixed-rate sampled at 10%
  (`ApplicationInsights:SamplingRatio`); exceptions are never lost to it because
  `GlobalExceptionHandler` logs them through `ILogger` and the logs pipeline is not trace-sampled.
  `cloud_RoleName` comes from the OTel resource `service.name`, set by reflection **in the API only,
  never in WASM**.
- **Storage & retention.** Azure Table Storage; local dev runs Azurite in Docker
  ([docker-compose.yml](docker-compose.yml), `UseDevelopmentStorage=true`). History rows are pruned
  after `Retention:HistoryDays` (default 30) on each save; blobs age out via the lifecycle policy in
  [infra/main.bicep](infra/main.bicep). There is no incident log: `IncidentService` wrote a row per
  health transition on every scan, kept them forever, and **nothing ever read them** — no endpoint,
  no DTO on the wire, no UI. "What changed since last scan" is the feature it looked like, and
  `DashboardInsightsBuilder.BuildDelta` already provides that from history. Removed 2026-09-04
  along with `IncidentEntry`, `IncidentTypes`, the `incidents` partition and the never-configured
  `Incidents:WebhookUrl`. The `incidents` table already in Azure is untouched; drop it by hand.
- **Caching.** None beyond the framework's. `HybridCache` and the sized `IMemoryCache` were removed
  along with the slices that used them; add one back deliberately if a read-through cache is needed.
- **No CORS.** Single-origin Blazor Web App — the WASM client is served from this same host.

## Things that will bite you

- **Client JSON is source-generated.** Every type the WASM client (de)serialises needs a
  `[JsonSerializable]` entry in [AppJsonContext.cs](src/PoPunkouterSoftware.Client/AppJsonContext.cs).
  `PublishTrimmed` + `EnableTrimAnalyzer` are on for `.Client` with no `WarningsNotAsErrors` escape
  hatch, so reflection-based JSON fails the build.
- **The whole app has one rAF loop.** It lives in `js/motion-kit.js`. Do not add another —
  see [Graphics and audio](#graphics-and-audio).
- **The icon font is a SUBSET, and adding an icon means rebuilding it.** Radzen.Blazor ships the
  Material Symbols variable font (~3,600 glyphs, 1,068,920 bytes) and self-hosts it as family
  `"Material Symbols"`. This app draws about thirty of those glyphs, so
  [SCRIPTS/Build-IconFontSubset.py](SCRIPTS/Build-IconFontSubset.py) builds
  `wwwroot/fonts/material-symbols-subset.woff2` (17,344 bytes, from the Radzen version pinned in
  `Directory.Packages.props` — the script used to take the "newest" cache folder by string
  sort, which picked 8.7.3) and `modern-ui.css` declares the
  family against that. **Material Symbols renders through LIGATURES** — the element's text is the
  icon's name — so a name missing from the subset does not degrade to a blank box: it renders the
  literal word "refresh" inside the button. Add the name to `APP_ICONS` and re-run the script; it
  verifies every ligature survived and fails loudly if one did not, and
  `Azure_EveryIcon_RendersAsAGlyphNotItsName` catches a miss in a real browser. Two subtleties the
  script documents: this font's features are `rlig`/`rclt`, not `liga`, and `--text` alone saves
  only 11% because all 3,600 names share the same 26 letters, so unused ligatures must be pruned
  out of GSUB before subsetting. Also: do not re-add the Google Fonts `<link>` `App.razor` used to
  carry, and keep the `@font-face` in `modern-ui.css` rather than inheriting Radzen's — theirs are
  `media`-scoped by colour scheme, an `@font-face` in a non-matching media context does not apply,
  and ours coming later in the cascade is what stops the 1 MB file being fetched at all.
- **A `RadzenChart` over time needs a DATE category, not a formatted label.** Bound to strings,
  `RadzenCategoryAxis` draws one tick per category and honours neither `Step` nor
  `TickDistance` — 30 day-labels on `/users` ran into each other and 90 were a solid grey band,
  through two attempts at thinning them from the markup. Give the axis `DateTime` values and a
  `FormatString`, and it thins its own ticks to the available width at every viewport with no
  breakpoint. `SignInTrendPoint.Day` is a `DateTime` for exactly this reason.
- **A page root that is `display: grid` needs `grid-template-columns: minmax(0, 1fr)`.** The
  default `auto` track is floored at the min-content width of its widest item, so one child with
  an intrinsic minimum — a `RadzenChart` with a legend — pushed `/users` to 441px inside a 390px
  viewport and every sibling stretched with it. **It does not show up as a horizontal
  scrollbar**: the overflow is clipped, `document.scrollWidth` stays exactly `clientWidth`, and
  the header, KPI tiles and card borders are simply cropped off the right. An E2E test that
  measures `scrollWidth` passes throughout. `Users_RostersRender_AndNothingOverflowsTheBody`
  therefore measures **element bounds**, excluding anything inside a deliberate `overflow-x`
  container — copy that shape for any new page rather than the scrollWidth check.
- **One application stylesheet.** `wwwroot/css/modern-ui.css` is the whole thing (plus `boot.css`
  for the pre-Blazor splash). It was a four-line aggregator over `modern-ui.base/.components/.responsive`;
  CSS `@import` is serial, so the split cost three extra round trips on the critical path and bought
  nothing a section comment does not. Order inside the file is load-bearing: tokens, then components
  that consume them, then breakpoint overrides.
- **Scoped CSS does not cross component boundaries.** Blazor stamps the scope attribute only on the
  owning component's own elements, so moving markup from a page into a child component silently kills
  every scoped rule that styled it. `AzureDashboard.razor.css` is written as
  `.azure-ops-page ::deep …` for exactly that reason — keep **every** rule in that form, media-query
  overrides included. `::deep` also moves the scope attribute leftward, so mixing forms breaks
  specificity: `.azure-ops-page ::deep .x` compiles to `.azure-ops-page[b-id] .x` (0,3,0) while a
  bare `.x` compiles to `.x[b-id]` (0,2,0). Media queries add no specificity, so a bare override
  **loses to its own base rule** and the layout never reflows — that is how the Azure glance grid
  stayed 3-up at 390px with correct-looking responsive rules, and `minmax(0,1fr)` hid it from the two
  E2E tests that check for overflow.
- **A global rule that must beat a page-root scoped rule needs (0,2,1), not (0,2,0).** A page root
  such as `.azure-ops-page` carries a scoped rule compiling to `.azure-ops-page[b-hash]` — (0,2,0)
  — and `App.razor` loads `PoPunkouterSoftware.Client.styles.css` **after** `modern-ui.css`, so a
  global selector that merely ties loses the tie on document order. Prefix it with `html`. The
  removed phone pager learned this the hard way: its `display: block` lost to the scoped
  `display: grid`, the panes became grid rows a sixth of a screen tall painting over one another,
  and every E2E assertion that measured CONTENT still passed. Measure the rendered box.
- **`Testing` is the hermetic switch.** It skips Key Vault entirely. Integration tests boot the real
  entry point via `WebApplicationFactory`, and collections run sequentially (`AssemblyInfo.cs`)
  because concurrent entry-point boots race.
- **UI tests must cover both viewports.** `PortfolioUiTests` drives every test at mobile-portrait
  (390×844) and desktop-landscape (1440×1000) through the shared `Viewports` theory data. Visual
  parity is a hard rule, not a preference.
- **Screenshots pin `PLAYWRIGHT_BROWSERS_PATH`** to the persistent `%HOME%` share and install only
  the Chromium headless shell — the worker's ephemeral disk previously filled with a full Chromium
  download and broke every deployment (2026-07-10). Kill switch:
  `FeatureFlags:EnableScreenshots=false`. Since 2026-10-05, a cache miss on
  `GET /api/portfolio/screenshots/{host}` launches a headless browser and captures that host on
  demand (`AppScreenshotService.CaptureOneAsync`). While the flag is on, every card gets a
  `screenshotUrl` even if no image exists yet. On-demand capture never waits behind a batch run,
  and it backs off for 30 minutes after a failure. That endpoint is anonymous, so leave the flag
  off anywhere a crawler can reach. In-process capture is off in Production for that reason,
  and `appsettings.json` said the capture happened in `screenshots.yml` instead — **a workflow that
  did not exist**, so every production card rendered `screenshotUrl: null` for months. It exists now
  (see CI/CD below) and writes the same container, blob names and viewport as `AppScreenshotService`,
  so the two paths are interchangeable. Serving those blobs also needs `AzureBlobStorage:Endpoint`,
  without which `GetContainerAsync` returns null and stored images never appear —
  indistinguishable from never having captured any.

## Tests — four projects, one per tier (budget 100/50/25/25, currently 100/45/21/24)

**The budget is a ceiling, not a target.** All four tiers are at or under it. Adding a test means
finding one to remove, so prefer widening an existing test's assertions to adding a new method — the
two tiers that had to be cut back were full of one-assertion Facts that each re-fetched the same
document (nine for `/health`, twelve for `Result<T>`), and collapsing those to whole-contract
assertions cost no coverage at all. Parameterise only where the cases are genuinely different
branches; a `[Theory]` with six casing variants of one rule is six tests' worth of budget for one
rule's worth of coverage.

- **`PoPunkouterSoftware.Unit`** — strictly no I/O (HTTP stubs, in-memory). DTO-mapping tests live in
  `.Integration`, never here.
- **`PoPunkouterSoftware.Integration`** — `WebApplicationFactory` + Testcontainers Azurite, including
  a fixture that runs Azurite inside the factory with explicit teardown. `TestWebApp` seeds its own
  report cache into `App_Data` — it previously got a report for free from the committed
  `wwwroot/data/azure-full-report.json`, an undeclared dependency on a production data dump that
  made "hermetic" untrue. The seed now **displaces** any file already there (restored on dispose):
  bailing out when one existed meant a developer who had run the app locally ran this whole tier
  against their own `App_Data` report — different names, different counts, a real subscription id —
  which is the same undeclared dependency one directory over. `ProductionBootTests` boots
  the entry point under `Production` with every external dependency blanked — the only coverage of
  that environment's hosting pipeline.
- **`PoPunkouterSoftware.E2EAPI`** / **`.E2EUI`** — pure HTTP and Playwright against a live instance
  via `BASE_URL`. On demand, not in CI. E2EUI runs **axe-core** (`Deque.AxeCore.Playwright`,
  WCAG 2.1 A/AA) over all four pages at both viewports and fails on serious/critical only. Its
  first run found three real defects: the active nav link at 4.37:1, the Sync button's
  aria-label not containing "Sync" (WCAG 2.5.3), and `aria-label` on the role-less loading
  skeleton. It waits for `appRepairRadzenA11y` first, or it reports icon ligatures in names.

## Git workflow — master only

**This repository uses `master` and nothing else.** Commit directly to `master`. Use another branch
only when the owner asks for one by name. Do not create feature branches, topic branches, or PR
branches on your own initiative, and do not leave one behind after a piece of work — if a branch
exists for any reason, delete it locally and on the remote once its commits are on `master`. There
is no review gate here to justify the indirection, and a stale branch on a solo repo is just a
second version of the truth.

This overrides any default "branch before committing to the default branch" habit.

**Committing is yours; pushing is the owner's.** Never `git push` unless the owner asks for it in
that turn, because **every push to `master` deploys to production** via
[deploy.yml](.github/workflows/deploy.yml). Commit freely, then stop and say the work is ready to
push — the pipeline will not run the tests for you, and it will not ask before shipping.

**`git sync` is the ask, and it is the one place the fast tier runs in full.** On those two words:
commit every outstanding change, run `dotnet test` on the Unit and Integration projects (not just
the filtered subset that covered the edit — this is the gate that stands in for a review), then
push. Everywhere else, run only the tests covering what changed.

**CI/CD:** the workflows below, plus the estate-wide `ci-watch.yml` and `conventions.yml`. Only `deploy.yml` runs tests, and only the Unit tier — the other
three tiers still run locally before a push.

- [deploy.yml](.github/workflows/deploy.yml) — build, **unit tests**, back up, deploy, **smoke
  test**, roll back. The live site is downloaded through Kudu's zip API (same OIDC identity)
  before the deploy; afterwards `/healthz`, `/health` and `/api/diag/summary` (200, or 404
  before any scan) must answer within ~3 minutes of retries, or the backup is redeployed, the
  job goes red and ntfy is told. No backup (first deploy, SCM down) means no rollback, with a
  warning. Target is App
  Service `app-popunkoutersoftware2` via OIDC, no required secrets in the workflow. Unit is the only tier
  that belongs in a deploy pipeline: no Docker, no network, no live host, ~0.5s for 100 tests
  against an assembly the job already built. Integration needs Testcontainers Azurite; both E2E
  tiers need a running app and a `BASE_URL`. Adding any of them would trade a fast gate for a
  slow flaky one and start blocking deploys on infrastructure rather than on code.
- [weekly-digest.yml](.github/workflows/weekly-digest.yml) — Monday 07:05 UTC ntfy push built
  from `/api/diag/summary` and `/api/signins?days=7`: health, 30-day spend against a week ago
  (the last `costHistory` point labelled that date), month projection, lowest uptime,
  sign-ins, top five attention items. A workflow, not a timer, because F1 sleeps.
- [uptime-scan.yml](.github/workflows/uptime-scan.yml) — nightly (06:17 UTC) `POST /api/diag/refresh`
  so the uptime grid gets one data point per day even when nobody visits and the F1 site is asleep.
  It wakes the site first (cold start), treats 409 "already refreshing" as success, and then
  **confirms a fresh report actually landed**. Its last step pings `HEALTHCHECKS_PING_URL`
  (optional secret; `/fail` on any failure) so healthchecks.io emails when the run fails OR
  never runs at all — a 202 only proves the scan started, and a scan that
  fails every night would otherwise show as a green workflow forever.
  **Requires** `FeatureFlags:EnableManagementActions=true` in Production plus a
  `Security:ManagementApiKey` matching the `MANAGEMENT_API_KEY` GitHub secret. `ManagementActionFilter`
  **fails closed** in Production when the flag is on without a key — that combination would leave a
  free, repeatable, ~30-second subscription scan open to anonymous callers.
  ⚠️ **This ran red every night from at least 2026-08-29 to 2026-09-05** because neither setting was
  ever applied: `/api/config` reported `managementActionsEnabled: false`, so `/api/diag/refresh`
  answered 403 to a correct key and an absent one alike, and the workflow's own 403 branch fired
  nightly. Nothing else schedules a scan, so the consequence was not a missing data point — it was
  an empty `History` table (`/health` showed `TableStorage: readable, rowsVisible: 0`), no uptime
  grid, and a report that only ever refreshed when a visitor happened to load one past the 12h
  staleness line. **A red scheduled workflow here means the dashboard has no data**, not that a
  nightly nicety was skipped. Check its run history before believing the grid.
  It went red again until 2026-10-20 for the same reason; the flag is now `true` in
  `appsettings.json` and the key is in both Key Vault and the GitHub secret (same value).
  Integration hosts blank the vault with `UseSetting("KeyVault:Uri", "")` — an in-memory
  override arrives after `Program.cs` reads the URI, so `ProductionBootTests` used to load the
  real vault through the developer's login.
- [screenshots.yml](.github/workflows/screenshots.yml) — nightly (04:40 UTC) Playwright capture of
  every card's URL at 390×844 (red every night until 2026-10-20: `npx playwright install` does not
  make `import 'playwright'` resolvable — it `npm install`s the package first now), uploaded to the
  `app-screenshots` blob container under the host name
  the card looks up. A second pass calls PageSpeed Insights (mobile; `PAGESPEED_API_KEY`
  optional) and MERGES the scores into `lighthouse.json` in the same container, which
  `AppScreenshotService.LoadScoresAsync` reads into `PortfolioApp.Scores` — the card's chips.
  The merge also appends each scored host's performance to `performanceHistory` (30 nights),
  the sparkline on the card's foot line. **Anonymous PageSpeed calls answer 429**: the run on
  2026-10-30 scored 0/19 and production `lighthouse.json` holds no scores, so neither the
  chips nor the trend render until a `PAGESPEED_API_KEY` secret exists. Exists because in-process capture cannot run on the Windows F1 sandbox.
  Uploads with the storage **account key**, resolved at run time through ARM by the same OIDC
  identity `deploy.yml` uses — Contributor on the resource group covers `listKeys`, so this needs
  no new role assignment and no new secret. It was written with `--auth-mode login` and no
  matching data-plane grant anywhere, which would have failed on its first run. The stricter
  option is `--auth-mode login` plus **Storage Blob Data Contributor** scoped to the container;
  prefer it if you know the identity's object id.
