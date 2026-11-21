# AGENTS.md — PoPunkouterSoftware

## 1. Standard

This repo owns every file in it. Nothing here is generated from, synced with, or required to stay identical to
another repository. Headers like "SAME FILE IN EVERY PO …", "Generated from po-shared … DO NOT EDIT", and scripts
that copy files into or check other repos are forbidden. Only Azure resources in the `PoShared` resource group
may be shared.

The Po Web App Standard follows (repo-specific exceptions are under "Repo-specific notes").

This repo is one of several independent Po apps. It follows the conventions below, but it owns every file:
nothing is generated from, synced with, or required to stay identical to another repository. Change anything
here freely; only the *conventions* are common, never the code. Azure resources may be shared through the
`PoShared` resource group; code may not.

### Repo skeleton
```
README.md          what the app is, live URL, how to run locally, how it deploys
AGENTS.md          this standard + repo-specific notes (CLAUDE.md just points here)
CLAUDE.md          one line: "Read AGENTS.md."  (plus repo-specific notes only if unavoidable)
<App>.slnx  global.json  Directory.Build.props  Directory.Packages.props  .editorconfig
src/<App>.Api  src/<App>.Client  src/<App>.Shared  [src/<App>.Mobile]
tests/<App>.Unit  tests/<App>.Integration  [tests/<App>.E2E...]
infra/main.bicep  infra/main.bicepparam  [infra/modules/*.bicep]
scripts/           setup / helper scripts (no infra/*.ps1)
docs/              lowercase; everything else that is prose
.github/workflows/deploy.yml   the ONLY workflow
```
No `.cursorrules`, `.copilotignore`, `SPEC.md`, `tasks/`, `DOCS/` at the root (fold useful content into docs/).
`SCREENSHOTS/` is allowed only if README references it.

### Page shell (every page, signed in or out, including the sign-in page)
- Header `.po-header`, height 56px, sticky: app name top-left (`.po-header__title`, links to `/`).
- Top-right `.po-header__actions`: signed-in user's **email** (`.po-header__email`, full email, ellipsis at 16ch on
  phones) — or, signed out, one **Sign in** button `class="po-link-btn filled small"` (same style on every page) —
  then the settings gear ONLY if the app has a settings page (the owner removed settings from some apps — don't
  re-add it). Apps without OAuth show nothing in place of the email (no fake identity chips).
- Version badge `.po-version` fixed bottom-right showing `vYYMMDD`. It must never cover content: `main` gets
  `padding-bottom: calc(var(--po-space-6) + env(safe-area-inset-bottom))`, and when a mobile bottom nav exists the
  badge sits above it (`--po-stamp-lift: <nav height>`).
- Colours only from `wwwroot/css/po-tokens.css` (`--po-*`). All text ≥ 4.5:1 contrast in light AND dark mode
  (status banners: use `--po-text` on `--po-*-soft` backgrounds, never warning-on-peach).
- Login-only data must not be requested for signed-out visitors (no 401s in the console on public pages), and
  actions a signed-out visitor cannot perform are hidden.

### Version
- `Directory.Build.props` stamps ONE assembly metadata key: `BuildVersion` = UTC `yyMMdd`. No `BuildStamp`/`BuildDate`
  aliases. Every reader (header badge component, `/api/diag`, `/health` headers) reads `BuildVersion`.

### Health + /diag contract
- `GET /health/live` (liveness, returns header `X-Commit: <sha>`), `GET /health` (readiness), `GET /api/diag` →
  ```
  DiagReportDto(string Status, string Environment, string Version, DateTimeOffset CheckedAt, double CheckedMs,
                DiagCheckDto[] Checks)
  DiagCheckDto(string Name, string Group /* "api" | "data" | "service" */, string Label, string Status
               /* Healthy|Degraded|Unhealthy|Off */, string Purpose, string Detail, double LatencyMs, string? Problem)
  ```
- UI route `/diag` ONLY (delete other diagnostics pages/routes such as `/system`, `/diagnostics`, `/status`). Page
  `<title>` and `h1`: **System status**. Layout: overall banner in plain words ("Everything is working" /
  "Working, with N things degraded" / "Not working: …"), then three sections in this order: **API**, **Data**,
  **Services**; each row = label, purpose, status pill, latency ms, problem/detail text. Every external dependency
  the app really calls appears as a check. Anonymous, nothing secret in the payload.

### Workflow (`.github/workflows/deploy.yml`, the only one)
push to master → format check → build (Release, warnings as errors) → Unit tests → Integration tests (Testcontainers /
Azurite; Docker is available on ubuntu-latest) → publish → deploy → confirm `/health/live` X-Commit = this SHA.
If the repo has a MAUI `src/<App>.Mobile`, a second job in the SAME file (`mobile`, `needs: []`, not blocking deploy)
installs the `maui-android` workload and builds the Android target, so the client is always compiled.

### Tests
- Unit tier: fast, no I/O, runs in CI.
- Integration tier: runs in CI; locally it SKIPS with a clear message when Docker is not running (never a wall of
  `DockerUnavailableException` failures).
- E2E/UI tiers: run locally against a running app; when no app URL / browser is available they SKIP with a clear
  message. They are not part of CI.
- Docs must state real counts, not caps that are out of date.

### Azure naming
App Service `app-<repo lowercase>`; per-app resources named `<type>-<repo lowercase>`; shared things only in `PoShared`.

## 2. Concurrent work

Several sessions (people and agents) commit to this repo, sometimes at the same time.
- Before every commit and every push: `git fetch origin && git rebase --autostash origin/master`.
- Stage only the files you changed (`git add <paths>`); never `git add -A` / `git add .`; check `git diff --cached`.
- Never revert, reformat or overwrite changes you didn't make; keep the owner's recent decisions (e.g. features
  removed on purpose stay removed).
- Don't commit or sync from the IDE while an agent is working in this folder.
- Pushing `master` deploys production; changes to `.github/workflows/*` need a token with the `workflow` scope.

## 3. Verification

Tested 2026-11-21 (.NET SDK 10.0.4xx per `global.json`, `rollForward: latestFeature`; Python 3.10+ and `uvx`
for ruff; Docker Desktop for Azurite).

| Step | Command | Needs / without it |
| --- | --- | --- |
| Format | `dotnet format PoPunkouterSoftware.slnx --verify-no-changes` | — |
| Python lint | `uvx ruff check .` (config: `ruff.toml`) | — |
| Build | `dotnet build PoPunkouterSoftware.slnx -c Release` (warnings are errors) | — |
| Unit (91) | `dotnet test tests/PoPunkouterSoftware.Unit -c Release --no-build` | nothing |
| Integration (45) | `dotnet test tests/PoPunkouterSoftware.Integration -c Release --no-build` | Docker for the 6 Testcontainers Azurite tests; without it they skip with "Docker is not running…" |
| E2E API (22) | `BASE_URL=http://localhost:8000 dotnet test tests/PoPunkouterSoftware.E2EAPI -c Release --no-build` | a running app; without one every test skips ("No app is answering at …") |
| E2E UI (28) | `BASE_URL=http://localhost:8000 dotnet test tests/PoPunkouterSoftware.E2EUI -c Release --no-build` | a running app with a stored Azure scan (on a fresh machine: `curl -s https://app-popunkoutersoftware2.azurewebsites.net/api/diag/report -o src/PoPunkouterSoftware.Api/App_Data/azure-full-report.json`) + Playwright Chromium (`pwsh tests/PoPunkouterSoftware.E2EUI/bin/Release/net10.0/playwright.ps1 install chromium` once); without either every test skips with the reason |
| E2E on the trimmed Release build | `pwsh ./scripts/run-e2eui.ps1 -Tiers E2EAPI,E2EUI` (publishes Release like deploy.yml, seeds the scan, starts Azurite + the published app on :8000, runs both tiers, stops everything) | Docker + Playwright Chromium |
| Run locally | `pwsh ./scripts/run-dev.ps1 -NoWatch` (drop `-NoWatch` for hot reload) | Docker (starts Azurite via `docker compose` if :10002 is closed) → http://localhost:8000 |
| Deploy | push to `master` → `.github/workflows/deploy.yml` (format, ruff, build, Unit, Integration, publish, deploy); `*.md` and `SCREENSHOTS/**` are paths-ignored | repo variables for OIDC |
| Deploy check | `curl -sI https://app-popunkoutersoftware2.azurewebsites.net/health/live` → `X-Commit: <pushed sha>` | — |
| Live | https://app-popunkoutersoftware2.azurewebsites.net and https://app-popunkoutersoftware2.azurewebsites.net/diag | — |

## Repo-specific notes

### Exceptions to the standard (deliberate)

- **Header right side is empty.** The app has no visitor sign-in (no email, no Sign in button), and the owner
  removed the settings gear from the top bar on 2026-11-18 although `/settings` still exists (reached from
  Ctrl+K). Don't re-add the gear without the owner asking.
- **Azure names predate the naming rule.** App Service `app-popunkoutersoftware2` on plan
  `asp-PoPunkouterSoftware-f1` (F1 Free, no Always On) in resource group `PoPunkouterSoftware`; Key Vault
  `kv-poshared2` and the shared App Insights live in `PoShared`. Renaming means recreating the site.
- **An extra `src/PoPunkouterSoftware.Infrastructure` project** holds the Azure adapters (Table Storage, ARM
  scan, pinger, alerts, AI triage); the skeleton's Api/Client/Shared stay as they are.
- **Reading other Po apps is the product.** `wwwroot/data/apps.json` lists the portfolio, and the app pings each
  app's public `/health/live` and reads its public pages and repos. That is data, not shared code: nothing here
  writes to, copies into or checks the conventions of another repo.
- `SCREENSHOTS/` holds before/after UI pages (owner's rule below) and is referenced from README.md.
- **Radzen wins (owner decision 2026-11-21).** Every interactive control is a Radzen component: buttons are
  `RadzenButton`, the /users filter is a `RadzenTextBox`. A button that keeps a bespoke look is
  `ButtonStyle.Base` + `Variant.Flat` + class `ppk-plain` (reset in `modern-ui.css`); its own class styles it
  from a scoped sheet as `.<page> ::deep .<class>` so it outranks the theme. The drawers are Radzen side
  dialogs (`AppDrawer.razor`, a native `<dialog>` until 2026-11-21; see docs/architecture.md for the refresh and scoped-CSS plumbing). Allowed exceptions
  (owner list: resumable-upload file inputs, native audio/video, read-only data tables): only the six
  read-only data tables, which stay plain `<table>`s (row headers, captions and the phone card layout;
  `RadzenTable` wraps them in a scrolling grid with sticky, ellipsised headers). No other component library.
- **Version stamp on phones is in the page flow.** At 768px and below `.po-version` is the last thing on the
  page (lower-right, above the bottom nav); wider, it is fixed lower-right and `.app-shell-body` leaves it an
  88px gutter. A fixed stamp on a phone sat on /diag cards as they scrolled past.
- **/diag probes every outside service live** (`Features/Diag/ExternalServiceProbes.cs`, cached 1 minute):
  screenshot blob container, Azure Resource Manager, Application Insights (a `print` query on the Users-page
  resource), the AI endpoint (model list, never a paid completion), GitHub (`/rate_limit`), NuGet, OSV, Azure
  retail prices, RDAP and ntfy (`/v1/health`, never a push; shown as a neutral **Off** row, not counted in the banner, while
  `Alerts:NtfyTopic` is unset, which it is in production) — plus the Key Vault, Table Storage and scan-data
  health checks. Off in the `Testing` environment and when `Diag:ProbeServices=false`.

### Dropped on 2026-11-20 (owner's choice)

The F1 plan has no Always On, so in-app timers stop whenever the site sleeps; instead of upgrading the plan the
owner dropped the scheduled jobs. `deploy.yml` is now the only workflow. Gone:
- `ci-watch.yml` (ntfy push when any Po repo's default-branch run went red) and `conventions.yml` (weekly check of
  every Po repo against shared conventions, with `scripts/check-conventions.sh` / `scripts/sync-ci.ps1`).
- `uptime-scan.yml` (nightly `POST /api/diag/refresh` + healthchecks.io ping). Scans now run when a visitor loads a
  report older than 12 h, or when a key holder calls `/api/diag/refresh`; a day nobody visits has no uptime data,
  and the grid's hint says so.
- `weekly-digest.yml` (Monday ntfy summary). The app's own outage/scan/cost alerts are unchanged.
- `screenshots.yml` (nightly card screenshots + PageSpeed scores). Cards keep the last captured picture with a
  "no longer updated" note on the home page; the PageSpeed chips were removed (production never held a score).
- GitHub secrets only those jobs used (`MANAGEMENT_API_KEY`, `NTFY_TOPIC`, `HEALTHCHECKS_PING_URL`,
  `PAGESPEED_API_KEY`) are no longer read by any workflow.

### Documents

- `README.md`: what the app is, live URL, local run, deploy, configuration keys.
- `docs/architecture.md`: the architecture map and the decisions behind it. Read it before changing behaviour.
- `docs/testing.md`: the four test tiers, counts, and how each skips.

### Commands and footguns

```powershell
pwsh ./scripts/run-dev.ps1                 # kills a stale instance, starts Azurite, watch-run on :8000
dotnet test tests/PoPunkouterSoftware.Unit --filter "FullyQualifiedName~SecretMaskingTests"    # one class
python scripts/Build-IconFontSubset.py     # rebuild the icon-font subset (pip install "fonttools[woff]" brotli)
Get-ChildItem -Recurse -Directory -Include bin,obj | Remove-Item -Recurse -Force   # when a build misbehaves
```

- **Never `dotnet run` this project directly.** A stale `PoPunkouterSoftware.Api.exe` locks port 8000 and the
  build DLLs (MSB3026 stalls, then a half-replaced `wwwroot` gives WASM 404s and SRI errors);
  `--no-launch-profile` defaults to `Production`, silently attaching a local run to the production Key Vault,
  Table Storage and App Insights; and without the profile it binds :5000. `scripts/run-dev.ps1` handles all three.
- `dotnet test` takes ONE project per invocation (MSB1008).
- VS Code tasks wrap most of this: `build`, `test-unit`, `test-unit-watch`, `start-azurite`, `start-api-server`,
  `deep-clean`. F5 runs `f5-prep` (kill stale dotnet → start Azurite → build).

### CI/CD and Azure

- `.github/workflows/deploy.yml` is the only workflow. A push to `master` checks formatting, lints Python with
  ruff, builds Release (warnings are errors), runs the Unit and Integration tiers (Testcontainers Azurite on the
  runner's Docker), publishes, deploys and waits for `/health/live` to report the pushed commit in `X-Commit`. A
  red check stops the deploy and the live site keeps its last good build. E2E tiers and Bicep run locally; Bicep
  (`infra/main.bicep` + `main.bicepparam`, modules in `infra/modules/`) is applied by hand (`what-if` first).
- Azure sign-in is GitHub OIDC (the `gh-<repo>-deploy` app). Its IDs are repo **variables** `AZURE_CLIENT_ID`,
  `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID`; the App Service name is `AZURE_WEBAPP_NAME`. Never move them to
  secrets or write resource names into workflows.
- Every permission the app and CI need lives in `scripts/ensure-azure-access.ps1`. A role granted by hand and not
  added there is a bug: the next re-provision loses it.
- The app answers `/health/live` (process up, no dependencies, `X-Commit`), `/health` (dependencies) and
  `/api/diag` (the System status report). Tests pass on any machine: no drive-letter, temp-folder or
  working-directory assumptions, and no live config value hard-coded as an expected result.

### Owner's working rules

Standing instructions from the owner. They outrank default habits and apply to every task here.

- **`master` only.** Commit straight to `master`; use another branch only when explicitly asked for
  one. See "Git workflow" below.
- **Never push without being asked.** Not "asked once, months ago" — asked in the turn you are
  working on. A push deploys straight to production. Commit, then hand back and say it is ready.
  The one standing exception is the phrase **`git sync`**: typing it *is* the ask.
- **`git sync` means: stage everything, commit, push.** In that order, no questions asked in
  between. Commit **all** outstanding changes first — a sync that leaves a dirty tree behind ships
  half the work and hides the other half.
- **Git-sync commit messages are short and use American slang, so it seems a human wrote it.**
  One line, casual — "fixed the busted app cards", "cleaned up the dead scan code". No formal
  subject/body essays, no bullet lists, no changelog voice.
- **Restart the app after any code change and confirm it came back up.** `./scripts/run-dev.ps1`,
  then check it actually serves (`/health/live` returns 200) before claiming the change works. A build
  that compiles is not a running app — and this project's stale-process footgun means a "successful"
  build can leave the old binary serving on :8000.
- **No `dotnet user-secrets`.** Local config goes in `appsettings*.json`; real secrets go in the
  `kv-poshared2` Key Vault. Rationale: user secrets live outside the repo in a per-machine folder, so
  a value only one machine has looks like a bug on every other machine and in production.
  Resolved 2026-09-05: `<UserSecretsId>` removed from `PoPunkouterSoftware.Api.csproj`; ASP.NET no
  longer auto-loads `%APPDATA%\Microsoft\UserSecrets\popunkouter-software-api\secrets.json` in
  Development. Re-add only with the owner's explicit go-ahead.
- **Run only the tests that cover what you changed.** Not the whole tier, and never all four tiers,
  after an ordinary code change — `--filter "FullyQualifiedName~<TypeUnderTest>Tests"` against the
  one project that holds them. The full fast tier is a **pre-push gate**, not a per-edit habit; see
  "Git workflow" below. A minor edit (copy, comment, docs, a CSS value) can
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
- **Keep [README.md](README.md) in step with this file and [docs/architecture.md](docs/architecture.md)** whenever a dependency, configuration key
  or major endpoint changes — in the same change, like the rule above for this file.
- **Treat compile warnings as errors and make sure they are fixed.** The build command above
  already runs with `TreatWarningsAsErrors=true` solution-wide (see `Directory.Build.props`), so
  the rule is enforced by the compiler — a warning is a build failure. Don't ship a commit
  that builds with warnings; either fix the warning or, if it is intentional, suppress it at the
  smallest possible scope with a documented reason.
- **Where the prose lives.** [docs/architecture.md](docs/architecture.md) is the architecture map and the
  decisions behind it; [docs/testing.md](docs/testing.md) the test tiers. The rest of [docs/](docs/) is the
  GitHub Pages site (landing page, per-app privacy policies and store art for PoBox, PoCross, PoDance, PoFlag,
  PoFootball, PoRacer, PoSoccer and PoSumo, none of which are in this portfolio): leave those paths alone, app
  stores link to them.

### Git workflow

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
