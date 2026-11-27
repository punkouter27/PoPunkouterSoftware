# Architecture — PoPunkouterSoftware

The architecture map and the decisions behind it (moved here from CLAUDE.md on 2026-11-20). Rules for
agents, commands and verification are in [AGENTS.md](../AGENTS.md); the test tiers are in
[testing.md](testing.md).

## What this app is

A personal **Azure portfolio / ops dashboard**: a .NET 10 Blazor Web App whose WASM front-end shows
App Service health, cost, SSL expiry, zombie-app detection and downtime diagnosis. The backend is a
minimal-API **BFF** that reads an Azure inventory report out of Azure Table Storage (with a local
JSON fallback).

There are exactly **five pages**: `/` (the app catalog), `/azure` (the ops dashboard),
`/users` (who has signed in, across every Po app), `/settings` (what the app looks like, fixed) and
`/diag` (is the app and everything it relies on working). Everything else is an endpoint.

For years this said *two* pages, and that was load-bearing — the count is a budget, not a
tally. `/users` was added 2026-09-12 because the answer it gives lives nowhere else in the
estate: an Azure Monitor workbook nobody opens. Adding a fourth needs the same standard —
a question the existing pages cannot answer without becoming two pages in a trench coat.

**`/settings` is the fourth page by the owner's explicit call (2026-10-19), not by that standard.**
A first `/settings` (2026-10-04 to 2026-10-19) was deleted: reachable only from Ctrl+K, one of
its four controls saved and read by nothing, two duplicating palette commands, and its heading
icon rendering as the word "PALETTE". The rebuild held four toggles — Theme, Density, Sound,
Details — until **2026-11-17, when the owner fixed all four for everyone: Theme = System (follows
the device), Density = Compact, Sound = Off, Details = Full**, and the controls were removed. The
page now has no controls: it says there is nothing to change and lists, in plain words, how the
app looks and behaves. The route stays, with the Ctrl+K "Go to Settings" row. The header gear was
removed by the owner on 2026-11-18 and the phone bottom nav has no Settings slot either, so Ctrl+K and
a typed URL are the ways in; the page also lists the build stamp. How each fixed value is built in: theme — `po-tokens.css`/`boot.css` key off
`prefers-color-scheme` only and App.razor's Radzen sheets carry matching `media` attributes (no
script, no `data-app-theme`); density — the base `--app-density` is the compact 0.82; sound —
`audio-kit.js` was deleted outright (nothing could ever turn it on), the scan lifecycle event it
also dispatched moved to `appScan` in `helpers.js`; details — the `[data-detail]` hiding rule and
the palette's `detail` filter were deleted, so the chips, card scores/trend and Ctrl+K repo/Actions
rows always show. `theme-kit.js` now only deletes the old stored keys
(`popunkoutersoftware:theme|density|sound|details`, `pops:theme`) so a returning visitor cannot
stay on an old choice. Do not re-add a preference without the owner asking. The
default sign-in window is still `/users` remembering the last window picked (`pops:signin-days`).
Do not cite either page as a precedent for a sixth — that still needs the standard above.

**`/diag` is the fifth page by the owner's explicit call (2026-11-17), reversing the
2026-09-04 removal** (see "Routing" under Cross-cutting decisions for what the old one leaked).
Every Po app carries a friendly `/diag` titled "System status" (Po web standard, see AGENTS.md). This
one is an Interactive WASM page (`Client/Features/Diag/Diag.razor`) whose only fetch is `GET /api/diag`
(`DiagReportDto`), built by `DiagStatusBuilder` as an **allow-list**: each health check's name, group
(api / data / service), label, status and latency, plus fixed plain-English purpose, detail and problem
text written in that builder (a check that threw shows only its exception TYPE name). It never reads a check's `Data` dictionary or `/health`'s config block, so it is safe for
anonymous Production visitors; `ProductionBootTests.DiagStatus_IsFriendlyAndLeaksNothing_InProduction`
asserts that over the whole body. It is reached from Ctrl+K and the 404 page's list, not the
bottom nav. Not a precedent for a sixth page either.

Update this file in the same change whenever you cross an architectural boundary; drift here actively
misleads the next agent.

## Solution layout (vertical slices)

| Project | Role |
|---|---|
| [src/PoPunkouterSoftware.Api/](../src/PoPunkouterSoftware.Api/) | ASP.NET Core host + BFF, and the Blazor WASM host. Slices are `Features/<Area>/<Area>Endpoints.cs` (**Config, Diag, Portfolio, SignIns**), each exposing `Map<Area>Endpoints(this WebApplication)` called from [Program.cs](../src/PoPunkouterSoftware.Api/Program.cs). Host-level plumbing belonging to no slice lives in `Host/`. **The namespace is flat** — every file is `PoPunkouterSoftware.API` regardless of folder — with one exception: `Platform/PoPlatform.cs` (namespace `.Platform`) is this repo's own copy of the Po conventions (log property, cookie names, `/health/live`, `BuildVersion`, commit). Edit it like any other file. |
| [src/PoPunkouterSoftware.Client/](../src/PoPunkouterSoftware.Client/) | Blazor WASM (Radzen, mobile-first). `wwwroot` lives **only** here. The layout is **flat** — every component sits in the project root, named for what it is; there is no `Components/` tree. `wwwroot/js` holds the graphics and audio layers — see [Graphics and audio](#graphics-and-audio) below. |
| [src/PoPunkouterSoftware.Shared/](../src/PoPunkouterSoftware.Shared/) | DTOs + `DomainVocabulary.cs` only. **No PackageReferences at all**, no server- or browser-only deps — it ships in the WASM bundle (`IsTrimmable`). |
| [src/PoPunkouterSoftware.Infrastructure/](../src/PoPunkouterSoftware.Infrastructure/) | Azure adapters (Table Storage, ARM, pinger, incident, AI triage, telemetry) plus cross-slice helpers (`ReportFileCache`, `AttentionItemsBuilder`, `SecretMasking`). **Slices must not reference each other** — shared logic goes here. |

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
  ([PriorityQueueItem.cs](../src/PoPunkouterSoftware.Client/PriorityQueueItem.cs),
  [ResourceExplorerItem.cs](../src/PoPunkouterSoftware.Client/ResourceExplorerItem.cs)) — a private
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
whose whole value is being current: folding it into the stored scan would mean somebody who
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
the stale-report auto-rescan, another tab) show its progress and Cancel, and a Rescan that gets 409
follows that scan. Before, the page showed an idle Rescan button that could only answer "Already
running". Rescan is the only scan button: the home page had a **Sync** button (2026-10-19)
that started the same scan, removed 2026-11-23 (owner's call) so /azure is the one place to
start one. Rescan renders only when `/api/config` reports `managementActionsEnabled`, so in
Production it is available only to authenticated browsers while the feature flag is on.

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

**`/` is a stack matrix, not a card grid (owner's ask, 2026-11-03).** `PortfolioStackTable`
draws one row per app. **Both tables open with the same pinned columns, in this order:
App, Sign-in, Tech stack, AI** (owner's call, 2026-11-03) — pinned means always a column,
never moved to the "Shared by all" line even when every row agrees. Tech stack is ONE cell
holding labelled lines: Frontend / Backend / Real-time / Data for a web app, Engine / Physics /
RL training / Data for a game (Data was its own column until the same day). Then Ships via and rightmost "Unique tech" (`AppStack.Extras`: the
libraries and APIs only that app uses, read from its packages and code) — written for a visitor, not the owner (a Phone/PWA column went 2026-11-03 to save width): plain names, no SKUs or
resource ids. Cells are SHORT labels ("Table + Blob", "Azure OpenAI", "PWA") so a column
scans; the full wording is `AppStack.Details[<camelCase key>]`, shown as an `AppHint` on the
cell plus sr-only text. **The AI column is the exception**: it lists `AppStack.AiServices`
(`AiService`: cloud Azure/Google/In-app, model name, what it is used for, `optional` when
wired but off by default), grouped by cloud, and it is the one column that wraps. That list
was gathered 2026-11-03 from each repo's appsettings, option classes and client code
(tests and vendored JS excluded); `optional` was then checked against production: `kv-poshared2`
secret names and each App Service's app settings (PoSeeReview overrides its image model to
FLUX.1 Kontext pro there; PoRedoMedia's Google key is in the vault, so its Google models are live). A column IDENTICAL on every row of a table (for AI: the same service list) moves into a
"Shared by all N" strip above that table, outside its scroller, worked out per table on every
response. Only an IDENTICAL column moves up: an "all but one, naming the exception" rule
(Sign-in: "Microsoft, except PoRepoLineTracker: GitHub") was tried and removed the same day,
2026-11-03 — sign-in differs between apps (PoRepoLineTracker is GitHub-only on purpose, see
its SPEC.md §9) and some apps will drop login, so Sign-in is a column; an app with a backend and no
`signIn` in the catalog reads "None" (`AppStackBuilder`), never a dash. The RL table has its own columns, empty
for web apps: Physics (MuJoCo vs Unity 2D physics — the three ML-Agents games are all Box2D, none uses PhysX), RL training (Unity ML-Agents vs MuJoCo Warp /
Playground + Brax) and the shared rightmost Unique tech (Cinemachine, glTFast, VContainer,
Unreal Robotics Lab…), read from each game repo's ProjectVersion.txt, Packages/manifest.json,
.uproject and training/pyproject.toml. A Policy runtime column (Unity Inference Engine vs
Unreal NNE) went 2026-11-03 (owner's call); NNE now sits in PoSlapUnreal's Unique tech.
Outward links come from a scan step (`.PublicLinks`): an ANONYMOUS GET, through
`public-api` and never the GitHub PAT, of github.com/<githubRepo> and of the Play Store page
for each catalog `androidPackage`, stored as `AzureReport.PublicLinks`. `PortfolioApp.SourceUrl`
is hidden only when that check saw a 404 (a private repo); `PlayStoreUrl` appears only
once the listing answers, so a game shows "Google Play" the night after it is published. On
2026-11-03 every game's package id 404'd (unpublished), so none is linked yet. The RL training
cell links to the repo folder in `AppStack.TrainingPath`. A Hosting column was removed 2026-11-03 (owner's call). Real-time
(`AppStack.Realtime`: SignalR, WebSockets, server-sent events, Web Push) was read from each
repo's `MapHub`, `text/event-stream` and `PushServiceClient` use. Data names the services
("Azure Table Storage + Azure Blob Storage", one per line), never "Table + Blob", and lists
Blob Storage only for an app whose non-test code really reads or writes blobs (checked
2026-11-03: all but PoRepoLineTracker and PoTraffic). The app cell carries the FULL
`description` sentence under the picture, unclamped — the catalog's descriptions were
rewritten from each README then, and three were simply wrong (PoWatch, PoSeeReview,
PoLocalCompare); per-app add-ons a shared Backend would hide sit in `AppStack.Extras` (its own column).
A column empty on every row of a table is not drawn (the RL table has
no backend). **It stays a table down to 641px**: when the columns outgrow the card, the card
scrolls sideways (tabindex + role=group) with the app column `position: sticky` and opaque.
**Every value, heading and line label carries a hint** (owner's ask, 2026-11-03): a value's
hint is ONLY how THIS app uses it — its `stack.details` note (each item of a "A + B" list gets
the whole note), an AI model's `use`, and a cloud badge lists that app's models there. Never
what the technology is in general: the ~100-entry "what it is" glossary was deleted 2026-11-19
(owner's call); `TechGlossary` now holds only the column/line `Sections` text. Headings and line labels get an ⓘ — except App, Sign-in, Tech stack,
Frontend and Backend, whose names say enough (`NoSectionIcon`, owner's call) — that is
silent to screen readers (`AppHint SpeakText=false`); the table's sr-only caption says what each
column means once instead of with every cell.
A 1200px stacking breakpoint was tried first and took the comparison away in any window
narrower than a desktop, VS Code's browser pane included. At 640px and below each row
becomes a labelled block, and so does any portrait window up to 1024px (2026-11-23): there the
sideways scroll hid the AI column past the card's edge. Landscape windows keep the table.
The data is `PortfolioApp.Stack` (`AppStack`), built by `AppStackBuilder`: Data falls back to
"Azure Storage" when the app's resource group holds a storage account; everything else is the
`stack` block of each `apps.json` entry, because Key Vault, AI and App Insights all live in
the shared PoShared group and the inventory cannot say which app uses which. Those blocks
were filled 2026-11-03 from each repo's `Directory.Packages.props` and CLAUDE.md — when an
app changes its database, AI or sign-in, its `stack` block is the line to change.
**2026-11-13 re-audit against every repo** (owner's ask): every `details` note and AI `use` now
says how THAT app uses the tech — the glossary already says what it is, so a note that only
restates the glossary ("Unity 6000.6, built for WebGL…", "trained by trial and error") is the
failure mode. It found real errors, not just vague wording: PhysX claimed for three Box2D games,
glTFast "at runtime" (editor import only), Tone.js and LibGit2Sharp not used at all, Burst and AI
Navigation unused, PoSlapUnreal's site is a three.js + MuJoCo-WASM build (not Unreal). PoRumble was removed
entirely (repo, Azure resource and URL all gone) and PoRace was delisted (owner's call: the
Unity version is being retired; its Unreal rewrite `PoRaceUnreal` is not deployed). PoTraffic's
deploy had no test step, so it gained the Unit tier the same day and the shared delivery note
("builds, tests and deploys") is true on every row. `optional` was re-checked against
`kv-poshared2` secret names and App Service settings the same day.
PoMemeVideo was deleted from Azure and the catalog on 2026-11-07; its history rows age out of the uptime grid within 30 days.

**The report cache is server-side and must stay off the web root.** `ReportFileCache` keeps two
paths apart on purpose: `GetCatalogDir` is `wwwroot/data` and holds **only** `apps.json`, which is
public by nature; `GetCacheDir` is `ContentRootPath/App_Data` and holds the report. The cache used
to sit beside `apps.json` in `wwwroot/data`, where `UseStaticFiles()` served it to anyone who
requested `/data/azure-full-report.json` — a full Azure inventory including the raw subscription id,
every resource id, cost figures and SSL state, and it was committed to the repo besides. It has
never had a browser consumer. Do not put it back under any served directory, and do not "fix" a
future leak with a static-file exclusion when the file simply does not belong there.
`App_Data/` is gitignored; the Integration fixture seeds its own copy (see below).
Local screenshot captures lived there too (`App_Data/screenshots`, removed 2026-11-07 with in-process capture): they sat
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
a card per resource, and opens on "Needs a look" (rows with a concern; "All" is the other
chip) — 2026-11-07, when "All" was 58 rows around 8 worth reading. Each row prints its finding under the concern (`ResourceExplorerItem.Detail`: downtime cause and fix, storage and config issues), which replaced the drawer's "Evidence" disclosures; scan step timings left the UI (log + Report JSON). The scan no longer flags
Always-On on F1/D1 plans, which cannot enable it: it marked every Po app "Drift". Methodology and owner-facing config text (uptime method, budget key,
management flag, cold starts) lives in an icon-only `AppHint`, not a paragraph.

**One design system, two dials.** Everything visual resolves from the token block at the top of
`modern-ui.css`, and two of those tokens are the only places a global visual decision is made:

- **`--app-density`** scales the whole spacing ladder (`--app-space-*` are all
  `calc(<base> * var(--app-density))`). Compacting the UI for a viewport is one declaration —
  0.78 at 640px — not a per-component `padding` override repeated in every
  breakpoint of every stylesheet, which is what it replaced. Its base is the compact 0.82 for
  everyone since 2026-11-17 (the Density toggle that switched 1 ↔ 0.82 was removed, and the
  0.9 step at 820px with it, which would have been less compact than Compact).
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
  `var(--app-step--2)`. The sweep was finished 2026-11-07 (the command palette held two 0.72rem
  labels and chart axis text was 10px, both below the floor). Every text size is now a step; the
  only literals left are icon glyph sizes, which are not type, and the palette input's 1rem,
  which must stay 16px or iOS zooms the page when it is focused.

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
honest. Do not bring it back. `/` is a ~19-row catalogue and guarantees its first screen is
complete (`Home_FirstScreen_ShowsAWholeRow`). **No route truncates.** Advanced diagnostics is
not a pane but a drawer (see "Drawers are Radzen side dialogs" below).
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
  `.Presentation`, the scan-lifecycle event calls in `.ScanEvents` (`.Audio` until sound was removed, 2026-11-17). The pure projections are NOT a partial-class aspect any
  more: they live in `DashboardDerivations.cs`, a plain `public static` class, because as private
  members of the component ~460 lines of impact scoring, actionability tiering and cleanup
  reasoning were unreachable from a test even though both test projects reference this assembly.
  (`.DerivedViews` and `.Trends` were deleted with the cleanup and history disclosures.)
  Anything pure belongs in `DashboardDerivations`, with a test in
  `DashboardDerivationsTests`. Markup blocks are
  sibling components in the client root (`AzureStatusNarrative`, `AzureWhatChanged`,
  `AzureCostForecast`, `AzureUptimeHeatmap`, `Sparkline`, `AzurePriorityQueue`,
  `AzureResourceExplorer`, `AzureAppDetail`),
  plus the shared `AppDrawer`.

## The GLOBAL snapshot (`/global`)

The old `_CODEMATT/global` folder — a non-repo hub that crawled every sibling app and rendered
static HTML — was moved in on 2026-11-27. It is a **read-only snapshot** feature: nothing here
crawls, spawns processes or writes to another repo at runtime.

```
scripts/global/build_global_data.py        (owner's machine, read-only crawl)
        │  fleet.json, lessons.json, rl_facts.json, unite.json, config/*.json,
        │  history/station_timeline.jsonl, rl/timeline.jsonl
        ▼
src/PoPunkouterSoftware.Api/App_Data/global/     committed, under the CONTENT root
        │  GlobalDataStore (Infrastructure/Global) — timestamp-cached per file, degrades to empty
        ▼
/api/global/*   (Features/Global/GlobalEndpoints.cs, RequireAuthorization)
        ▼
Client/Features/Global/*.razor   (/global, /fleet, /briefing, /vault, /showroom,
                                  /readiness, /lessons, /disk, /rl, /unite, /incubator)
```

Three details that are easy to get wrong:

- **Two naming policies, on purpose.** The Python crawlers write `snake_case`
  (`is_git`, `dirty_count`, `commits_7d`); the app's wire contract is `camelCase`. Two
  source-generated contexts cover the two shapes — `GlobalCrawlerJsonContext` (snake_case, files)
  and `GlobalJsonContext` (camelCase, wire) — so the Release build never needs reflection. A few
  keys fuse a digit to the word (`Commits7d` → `commits7d`), so those carry an explicit
  `[JsonPropertyName]`.
- **`App_Data`, never `wwwroot`.** The snapshot names local paths, uncommitted files and commit
  hashes. It is deployed beside the app, not served — the API is the only entrance. This is the
  same lesson as the Azure report cache (see "Things that will bite you").
- **Nulls are normal.** A folder that is not a repo has `"ci": null`, `"docs": null`,
  `"media": null` and `"recent_commits": null`; a JSON `null` overwrites a property initializer.
  `GlobalDataStore.Normalize` fills every section back in once, so no page or aggregate needs a
  null check. `GlobalDataStoreTests` fails if a crawler change starts writing a shape the app
  cannot read.

The briefing and the ship board are **computed** in the store, not archived: the briefing diffs
the last two `station_timeline.jsonl` entries (commits, brain hashes, disk, CI flips, dirty
counts), and readiness suggests a stage from the crawl unless `config/readiness.json` sets one.
`docs/global-migration.md` is the full record, including what was retired.

## Graphics and audio

Nine files (`audio-kit.js` was deleted 2026-11-17 — there is no sound in the app) in [wwwroot/js/](../src/PoPunkouterSoftware.Client/wwwroot/js/). Read the header
comment in each before changing it; they document the reasoning, this is the map.

| File | Role |
|---|---|
| `motion-kit.js` | **The frame governor.** ONE `requestAnimationFrame` loop for the whole app, one gate (reduced-motion / hidden / blur / no runnable subscriber), and an adaptive quality controller. Must load first. |
| `gpu-backdrop.js` | Orchestrator for `#app-gpu-backdrop`: tier selection, colour tokens, glass rects (the renderer's energy input is a constant 0 since the audio kit went), and the DOM reads that feed the event effects (attractors, origins, the palette lens). Owns no pixels. |
| `gfx-webgl.js` | WebGL2 renderer (curl-noise field, transform-feedback particles, dual-Kawase blur + bloom, glass composite with prism edges) with a WebGL1 field-only fallback. **The top of the ladder.** |
| `ui-physics.js` | Physics on the DOM, not a canvas: KPI values count up on a spring. (The catalogue-card tilt went with the cards, 2026-11-03.) Per-frame work is motionKit subscribers that unsubscribe when settled. |
| `helpers.js` | Topbar, clipboard, download, `appRevealFocus` (the `/azure?focus=` scroll), `appScan` (dispatches the scan lifecycle as `app:refresh`) — plus the Radzen accessibility repairs (`appRepairRadzenA11y`). Unrelated to the animation layers. (The `appMedia` matchMedia bridge went with the resource explorer's data-grid branch — it had exactly one caller.) |
| `theme-kit.js` | Since 2026-11-17 only deletes the stored keys of the removed /settings toggles. Theme, density, sound and details are fixed (see the page budget at the top). |
| `app-boot.js` | Host-page boot: dismisses the loading splash, drives the nav progress bar, filters hot-reload console noise. Not an animation layer — no rAF. Was inline in `App.razor` until the CSP banned inline script. |
| `blazor-hooks.js` | Registers the enhanced-navigation callbacks. Must load **after** `blazor.web.js`, which defines `Blazor`. Also ex-inline. |
| `command-palette.js` | Ctrl+K / Cmd+K overlay: navigation (Settings included — it holds no setting toggles of its own), and a launcher — open / copy link / show uptime / repo / GitHub Actions for every app (from `/api/portfolio`, fetched on first open; repo rows come from the catalog's `githubRepo`. There is deliberately no Azure portal row: a portal deep link carries the subscription id every anonymous endpoint here masks) plus "Rescan Azure" when management actions are on. Fuzzy matching is hand-written: the CSP's `script-src 'self'` rules out a CDN library. |

**There is no service worker and no offline mode** (owner's call, 2026-10-19). One served every
page and script cache-first under a hand-bumped cache name, so a returning visitor saw the
PREVIOUS deploy until someone remembered to bump it — observed live: a header with no Settings
link. A self-removing stub served at that URL for three weeks so old installs could unregister;
it and the PWA `manifest.json` were deleted 2026-11-07.

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

**There is no sound (owner's call, 2026-11-17).** The app had a synthesised Web Audio kit
(`audio-kit.js`: UI clicks, a refresh drone and outcome motif, haptics) behind a /settings Sound
opt-in that defaulted off. Sound is now fixed Off for everyone, so the kit, its `[data-sfx]`
attributes and every `audioKit` call were deleted rather than left unreachable.

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
visuals as an `app:refresh` DOM event that `appScan` in `helpers.js` dispatches from the
dashboard's `appScan.start/progress/indeterminate/end` calls (`AzureDashboard.ScanEvents.cs`). Deferred effects run on a frame-advanced clock, not wall time, so a
scan that ends in a background tab plays its shockwave on return.

**There is one effects set (2026-11-07).** /settings used to offer Effects → Full, adding a
stable-fluids sim, per-service particle flocks, Gray-Scott "scars" around the uptime card, a
metaball health orb in the hero and a hyperspace jump on palette navigation. Few visitors
ever chose it, it was about half of `gfx-webgl.js` and needed float render targets, and it
more than doubled the measured dispatch cost (~6.4ms against ~1.6ms). All of it went, with
the Effects row, `appFx.setFull`, `pops:fx` (cleared on load), `[data-fx-flock]`,
`canvas[data-fx-orb]` and the `is-palette-jump` screen blend. What stays is what everyone saw:
- **Bloom and prism edges** in the composite: bloom reuses the Kawase pyramid plus one
  quarter-res step; glass edges split R/B along the edge normal, strongest near the pointer.
- **Palette** (`app:palette` events from `command-palette.js`): a lens toward the open
  palette, and the header brand morphs into the new h1 through a View Transition.

**Three pieces of polish are plain CSS/platform.** MetricBars spring in on `--app-ease-bounce`
(a damped spring sampled into `linear()`), drawn on the fill's `::before` so the fill's own
box — what `Azure_GlancePanels_DrawTheirMetrics` measures — is final from the first frame.(The theme toggle's circular View Transition reveal went with the toggle, 2026-11-17.) Cards and panes rise in on a scroll
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
  `AzureWhatChanged`, `AzurePriorityQueue`, `MetricBars`, `PortfolioStackTable`) plus any scalar that
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
- **Microsoft sign-in (owner-approved 2026-11-27).** The portfolio, settings and friendly diagnostics
  remain public; Azure, Users, their data/control APIs and refresh hub require authentication.
  `Host/MicrosoftAuthentication.cs` configures OIDC code + PKCE with an HttpOnly session cookie.
  `MicrosoftAuth` is app-specific to avoid picking up another app's `AzureAd` secrets from the vault.
  Server authentication state is serialized to the WASM islands, and protected pages use `[Authorize]`.
  Private APIs return JSON 401 rather than redirects; page requests go to Microsoft sign-in.
  Only explicit `X-Fake-User` requests in Development/Test/Testing use FakeAuth; absent headers
  never create an authenticated guest. All admitted Microsoft users have the same access.
  GLOBAL/RL pages have not yet migrated and must use this boundary when they do.
- **The header is the brand, at every width.** The four page links were
  hidden on 2026-10-20 and removed from `MainLayout` on 2026-10-30. The other pages are reached
  from the brand link home, the 404 page's list, Ctrl+K, the phone bottom nav, or a typed URL.
- **Po web standard (2026-11-18): the Po look.** `wwwroot/css/po-tokens.css`,
  `wwwroot/fonts/inter-latin.woff2` + `space-grotesk-latin.woff2`, `wwwroot/js/po-devwindow.js`,
  `wwwroot/js/po-dev.js` and `wwwroot/po-dev.html` are this repo's own copies of the Po look; edit them
  here like any other file. Radzen's theme sheets load first in App.razor (since 2026-11-23, so app
  classes win equal-specificity ties; E2E `Head_LoadsRadzenCssFirst_AndOnlyOnce`), then po-tokens.css; the token block
  at the top of `modern-ui.css` aliases every `--app-*` colour, radius and font to its `--po-*`
  tokens (one indigo accent, light/dark from the device, Inter text, Space Grotesk titles; the
  Material Symbols subset stays the only other face). The header is `.po-header` (RadzenHeader,
  a full-width bar, no longer the floating pill): the brand is `.po-header__title`, with Microsoft
  session controls on the right (the settings gear remains removed). The build stamp is the one
  `.po-version`. `/diag` draws each dependency as a `.po-diag__card` with a `.po-badge--ok|warn|bad`
  (`--po-text` on the `--po-*-soft` backgrounds), grouped API / Data / Services.
  **Tab with nothing focused opens `/po-dev.html`** in its own window (po-devwindow.js), which
  polls `/health/live` (`X-Commit`) and `/api/diag`; it is static, same-origin and its
  script is a file, so `script-src 'self'` holds. The developer persona dropdown that sat in the
  header went with `AccountMenu`: no endpoint uses the "Management" policy, so it gated nothing;
  `Program.cs` still restores a persona saved under `DevPersonaState.StorageKey`.
- **Session controls use static SSR.** `MainLayout.razor` stays statically rendered. `AuthorizeView`
  reads server authentication state; sign-in is a full-navigation link and sign-out is a POST form
  with an antiforgery token. Neither depends on an inert static `@onclick`.
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
  Every sender is in the app; the GitHub Actions senders (Lighthouse drops in `screenshots.yml`,
  the Monday `weekly-digest.yml`) were dropped with those workflows on 2026-11-20.
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
  (`PortfolioApp.CleanDays`, the flame chip in the row's app cell).
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
  Development/Testing, otherwise opt-in), authenticated access, and `X-Management-Request: 1`.
  Cross-site forms cannot set that header, CORS is disabled, and any supplied Origin must match
  the host. `/api/config` reports controls available only to authenticated users.
  The former `X-Management-Key` gate is no longer used. `/api/diag/ai` also requires sign-in.
  Restart resolves the site from the STORED report, never from the caller, so it can only touch
  a site the scan found; it starts a stopped site and restarts a running one. In Azure it also
  needs Website Contributor, which infra deliberately does not grant — it 403s cleanly there.
  Note a local Development run restarts the REAL site with your `az login`.
- **The one submitting control is "Rewrite", and it validates with FluentValidation (2026-11-17).**
  The app has no data-entry form; the only control that POSTs a body is the Rewrite button on
  `/azure` (`AzureStatusNarrative` → `POST /api/diag/ai`). Its rules live in Shared
  (`AiTriageAdHocRequestValidator`: a list, at most 50 items, none blank or over 500 chars — a
  cost guard on an anonymous paid completion). The button validates before sending and shows the
  first error inline beside itself (`role="alert"`, no browser dialogs); the endpoint runs the same
  validator and answers `Results.ValidationProblem`. The button is disabled while the request is
  in flight and the handler returns early on re-entry. The `/users` filter box also uses a Shared
  validator, but it is a filter, not a form. Any future form follows the estate pattern
  (PoTraffic's `FluentValidationValidator` bridge on a `RadzenTemplateForm`).
- **Build stamp.** Every page shows `vYYMMDD` as the estate's `.po-version`, fixed in the
  lower-right corner (lifted above the bottom nav at ≤768px by `--po-stamp-lift`). YYMMDD is the
  UTC date of the HEAD commit (UTC build date without git), computed by the `PoBuildStamp` target in
  `Directory.Build.props` into `[AssemblyMetadata("BuildVersion")]` and read through
  `Services/Ui/BuildStamp.cs` (also shown on `/settings`; `/api/diag` and `/health/live` report the same
  `BuildVersion` through `PoPlatform.Version`; it is the only version key). Until 2026-11-18 the client
  csproj stamped the local-time BUILD date. It was an in-flow footer showing `v1.0.0+<sha>`, below
  the fold on any long page, until 2026-11-17.
- **Routing.** `/api/diag/*` and `/api/portfolio/*` use `MapGroup`. `/health` (deep probe, one
  `IHealthCheck` per external dependency) and `/health/live` (static liveness) sit off the `/api` group.
  **`/openapi/v1.json` and `/scalar/v1` are mapped only outside Production** — they were
  unconditional, so the live site published its full route table, parameter shapes and response
  schemas (management routes included) to anonymous callers, for an API whose only client is
  compiled from this same solution.
  There is deliberately **no** `/api/health` alias. The **server** maps nothing at a bare `/diag`:
  the first one was an unauthenticated server page rendering ~130 lines of hand-built HTML with
  its own dark-only palette — a second design system — whose only caller was its own smoke test,
  and whose `?format=json` twin returned the server's absolute ContentRoot path, the environment
  name and masked-but-suffixed connection strings to anonymous callers in Production. Removed
  2026-09-04. **Since 2026-11-17 (owner's decision) `/diag` is a WASM page again** — Radzen,
  the app's own design system, fed only by the allow-listed `GET /api/diag` (see the page
  budget at the top). Do not add a server route at `/diag`, and do not widen `/api/diag`
  with check data, config, paths or ids.
  Since 2026-11-21 every outside service the app calls has a row that is probed live by
  `ExternalServiceProbes` (Api `Features/Diag/`), in parallel with a 5 s budget each. Since
  2026-11-23 the probes and the four health checks (each registered with a 10 s timeout, failing
  as Degraded) run as ONE round in `DiagSnapshotCache` (Infrastructure `Diag/`), kept 60 s and served
  to both `/health` and `/api/diag` (`CheckedAt` = when the round ran), so an anonymous visitor or a
  monitor cannot use either route to drive traffic: screenshot blob
  container, ARM, Application Insights (a `print` query), the AI endpoint's free model list,
  GitHub `/rate_limit`, NuGet, OSV, Azure retail prices, RDAP and ntfy `/v1/health`. Rows carry
  fixed sentences only. Before that, four rows (AI, screenshots, ntfy, App Insights) only read
  configuration and showed "Healthy" with 0 ms.
- **An unmatched path gets the app, not JSON.** A browser that mistypes a URL, or follows a link
  to an app that was renamed, must land on the app's own chrome: shell, header, skip link, a way
  back, and a **real 404 status**. That is `NotFoundPage.razor`, rendered by
  `UseStatusCodePagesWithReExecute("/not-found", "?statusCode={0}")`. It is not a page in the
  page budget — nobody navigates to it — but it is a route, so the count of routes is now
  six (five pages since `/diag`, 2026-11-17).
  **Two ways to get this wrong, both tried here, both caught by tests and not by looking:**
  a plain `MapFallback` returns the right status with no page (it swallowed every unmatched path,
  so `<NotFound>` in `Routes.razor` was dead code for a browser navigation); a **catch-all route**
  (`@page "/{*path}"`) returns the page but a **200**, because a catch-all matches by definition
  and a component cannot set `Response.StatusCode` — the response has already begun by the time
  `OnInitialized` runs. Only the re-executing status page gives a real 404 with a real body.
  Do not "simplify" it back into a fallback or a catch-all. Unknown **`/api/*`** paths keep
  answering JSON (`app.Map("/api/{**rest}")` in [Program.cs](../src/PoPunkouterSoftware.Api/Program.cs)),
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
- **`/api/diag/report` masks the subscription id.** It requires sign-in and returns every resource id in the estate.
  `SecretMasking.MaskSubscriptionIds` rewrites `/subscriptions/<guid>` to `/subscriptions/****` on
  the serialized JSON — on the JSON, not the object graph, because the id appears inside free-text
  strings across a dozen nested record types and a per-record rewrite would miss whichever one is
  added next. Resource groups and names stay: they are already public in the hostnames the
  portfolio links to. Use `MaskedJson`, never `Results.Json`, for a report in that slice.
- **`/api/signins` masks every email address, and the mask keeps the last character on
  purpose.** The `/users` roster is the app's only payload containing personal data about
  *other people* — the live data includes an address at a customer domain, not just the
  owner's. The endpoint now requires sign-in, but masking is retained and
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
- **The theme follows the device, and only the device (2026-11-17).** It was a choice: a
  /settings Theme row wrote a `data-app-theme` override, which forced a second copy of the light
  palette (attribute-scoped beside the media-query one) and a script that rewrote the two Radzen
  sheets' `media` attributes. The row, the override, the second palette copies (`po-tokens.css`,
  `boot.css`, `.azure-ops-page`'s `--tone-*`) and the script went together; each palette now has
  one dark default and one `@media (prefers-color-scheme: light)` block. Rejected alternatives,
  still: Radzen's `RadzenTheme`/`RadzenAppearanceToggle` (a second theme authority) and
  `light-dark()` (fails silently and totally on a browser that lacks it). The `--tone-*` strokes
  are data-viz graded against a 3:1 non-text floor — keep their light values in that one block.
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
- **Drawers are Radzen side dialogs** (owner decision 2026-11-21, "Radzen wins"; they were native
  `<dialog>`s from 2026-10-19). `AppDrawer.razor` calls `DialogService.OpenSideAsync` (right edge,
  `min(46rem, 100vw)` wide, mask, Escape and overlay click close it). The side dialog renders in the
  `<RadzenComponents>` outlet in `MainLayout`, outside the page, which cost two things the `<dialog>`
  had for free, both restored by hand: (1) page state — `AppDrawer` stays in the page and calls
  `DialogService.Refresh()` from `OnParametersSet` while open, so a filter click inside the drawer
  re-renders it; (2) scoped CSS — the page wraps the drawer content in
  `<div class="app-page azure-ops-page app-drawer__body">` written in `AzureDashboard.razor`, so the
  wrapper carries the page's scope attribute and every `.azure-ops-page ::deep …` rule still applies.
  `/azure` has two: Advanced diagnostics (it was an inline `.app-pane--tall` pane) and one per app,
  opened from an uptime row name (`AzureAppDetail`: the row's sentence, response time, attention
  lines and changes naming it, and its reach/cost).
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
  - The pinger stops when the app does (F1 has no Always-On). The nightly `uptime-scan.yml` that
    filled quiet days was dropped on 2026-11-20 (owner's choice), so a day on which nobody opened
    this site has no data on the grid; the grid's hint says so.
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
  ([docker-compose.yml](../docker-compose.yml), `UseDevelopmentStorage=true`). History rows are pruned
  after `Retention:HistoryDays` (default 30) on each save; blobs age out via the lifecycle policy in
  [infra/main.bicep](../infra/main.bicep). There is no incident log: `IncidentService` wrote a row per
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
  `[JsonSerializable]` entry in [AppJsonContext.cs](../src/PoPunkouterSoftware.Client/AppJsonContext.cs).
  `PublishTrimmed` + `EnableTrimAnalyzer` are on for `.Client` with no `WarningsNotAsErrors` escape
  hatch, so reflection-based JSON fails the build.
- **The whole app has one rAF loop.** It lives in `js/motion-kit.js`. Do not add another —
  see [Graphics and audio](#graphics-and-audio).
- **The icon font is a SUBSET, and adding an icon means rebuilding it.** Radzen.Blazor ships the
  Material Symbols variable font (~3,600 glyphs, 1,068,920 bytes) and self-hosts it as family
  `"Material Symbols"`. This app draws about thirty of those glyphs, so
  [scripts/Build-IconFontSubset.py](../scripts/Build-IconFontSubset.py) builds
  `wwwroot/fonts/material-symbols-subset.woff2` (16,744 bytes, from the Radzen version pinned in
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
- **The Integration fixture must hand back a developer's local report.** `TestWebApp` sets
  `App_Data/azure-full-report.json` aside, seeds its own, and restores on dispose. Its
  `Dispose(true)` runs TWICE (`WebApplicationFactory` reaches it from `DisposeAsync` and
  `Dispose`), and until 2026-11-03 the second pass deleted the real report the first had just
  put back — every test run wiped the local cache. It clears its fields before touching a
  file, and it never moves aside a seed it finds in place.
- **`Testing` is the hermetic switch.** It skips Key Vault entirely. Integration tests boot the real
  entry point via `WebApplicationFactory`, and collections run sequentially (`AssemblyInfo.cs`)
  because concurrent entry-point boots race.
- **UI tests must cover both viewports.** `PortfolioUiTests` drives every test at mobile-portrait
  (390×844) and desktop-landscape (1440×1000) through the shared `Viewports` theory data. Visual
  parity is a hard rule, not a preference.
- **Screenshots are no longer captured; the app shows the last ones.** `screenshots.yml` was the
  only writer of the `app-screenshots` blob container (one PNG per host, plus PageSpeed scores) and
  was dropped on 2026-11-20 by the owner's choice, together with the PageSpeed score chips it fed
  (production never held a score: anonymous PageSpeed calls answered 429). `AppScreenshotStore`
  still reads the stored PNGs, so cards show the last captured picture and the home page says the
  pictures are no longer updated; a new app shows its placeholder. In-process capture (Playwright)
  was removed on 2026-11-07 and must not come back: the F1 sandbox has no Chromium (an install once
  filled the worker disk and broke every deploy, 2026-07-10), and it put a headless browser behind
  an anonymous URL. Serving the blobs needs `AzureBlobStorage:Endpoint` in Azure, without which
  `GetContainerAsync` returns null and stored images never appear.
