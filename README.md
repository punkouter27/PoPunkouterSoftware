# PoPunkouterSoftware

PoPunkouterSoftware is the Po apps portfolio and Azure ops dashboard: a .NET 10 Blazor Web App (ASP.NET Core host +
WebAssembly client) showing every Po app, how each is built, and the health, uptime, cost and security of the
Azure estate behind them.

- Live: https://app-popunkoutersoftware2.azurewebsites.net (System status: `/diag`)
- Deploys: every push to `master` runs `.github/workflows/deploy.yml` (format, ruff, build, Unit + Integration
  tests, publish, deploy to App Service, confirm `/health/live` reports the pushed commit in `X-Commit`).
- Agents and contributors: read [AGENTS.md](AGENTS.md); the architecture map is [docs/architecture.md](docs/architecture.md),
  the test tiers [docs/testing.md](docs/testing.md). Before/after pages of UI changes are kept in [SCREENSHOTS/](SCREENSHOTS/); `pwsh ./scripts/screenshots.ps1 -Feature <name>` makes one (local Release builds of before and after, never the live site).

## Solution Layout

- `src/PoPunkouterSoftware.Api/`: ASP.NET Core host + BFF. Feature slices under `Features/<Area>/` (Config, Diag, Portfolio, SignIns); host-level plumbing that belongs to no slice lives in `Host/`. Hosts the Blazor WASM shell.
- `src/PoPunkouterSoftware.Client/`: WASM UI, layouts, pages, and static assets.
- `src/PoPunkouterSoftware.Shared/`: contracts and models shared between the host and client.
- `src/PoPunkouterSoftware.Infrastructure/`: Azure and integration adapters.
- `tests/PoPunkouterSoftware.Unit/`: xUnit, strictly no-I/O.
- `tests/PoPunkouterSoftware.Integration/`: `WebApplicationFactory` + Azurite Testcontainers.
- `tests/PoPunkouterSoftware.E2EAPI/`: pure-HTTP contract tests against a live instance.
- `tests/PoPunkouterSoftware.E2EUI/`: C# Playwright browser tests (run on demand against a live instance).
- `scripts/`: setup and helper scripts (`run-dev.ps1`, `ensure-azure-access.ps1`, `post-deploy-smoke.ps1`, the icon-font subset builder).
- `infra/`: Bicep (`main.bicep`, `main.bicepparam`, `modules/`), applied by hand.
- `docs/`: architecture and testing notes, plus the GitHub Pages site (privacy pages linked from app stores).

## Local Development

Prerequisites:

- .NET SDK from `global.json`
- Docker Desktop (Azurite for the local run and the Integration tier's Testcontainers)
- PowerShell 7 (`scripts/run-dev.ps1`)

Common commands (the full, tested list is "3. Verification" in [AGENTS.md](AGENTS.md)):

```powershell
pwsh ./scripts/run-dev.ps1 -NoWatch        # http://localhost:8000 (starts Azurite if needed)
dotnet build PoPunkouterSoftware.slnx -c Release
dotnet test tests/PoPunkouterSoftware.Unit -c Release --no-build
dotnet test tests/PoPunkouterSoftware.Integration -c Release --no-build
```

Available VS Code tasks in this workspace include:

- `build`
- `test-unit`
- `start-azurite`
- `start-api-server`
- `deep-clean` (removes every `bin/` and `obj/`; `dotnet clean` leaves `obj/` behind)

The app runs locally on HTTP port `8000`.

## Runtime Notes

- The portfolio (`/`), `/settings`, and friendly `/diag` remain public. `/azure`, `/users`, their
  report/summary/sign-in APIs, Azure mutation APIs, and `/hubs/refresh` require Microsoft sign-in.
  The same authenticated boundary will apply to GLOBAL/RL pages when they are migrated.
- Microsoft OIDC uses a server-owned HttpOnly session cookie; access tokens are not sent to the
  Blazor client. The header provides Microsoft sign-in and antiforgery-protected sign-out.
  Missing Microsoft configuration leaves private routes inaccessible and sign-in reports 503.

- `/health` is the deep probe (Key Vault, Table Storage); `/health/live` is a static liveness ping carrying `X-Commit`.
- `GET /api/diag` returns the System status report (`DiagReportDto`: status, environment, version, checks grouped api / data / service), the only source of the `/diag` page — statuses, latencies and fixed text, safe for anonymous Production visitors (no config block, URIs, paths or ids). `/api/diag/*` (summary, report, refresh, cancel-refresh, restart/{name}, ai) are the dashboard's own endpoints. `POST /api/diag/ai` validates its body with the shared FluentValidation `AiTriageAdHocRequestValidator` (400 validation problem on a bad list). `restart/{name}` restarts (or starts) one scanned App Service and sits behind the same management gate as `refresh`; in Azure it also needs Website Contributor, which infra does not grant.
- Each scan also reads Azure Resource Health (for down apps), Azure Advisor, 24h exception types per app from the shared Application Insights component (`SignIns:ResourceId`), and open Dependabot alerts per matched repo (needs Dependabot enabled on the repo and a PAT with "Dependabot alerts: read").
- Four keyless public APIs feed the scan through the `public-api` HttpClient: each matched repo's `Directory.Packages.props` (read with the GitHub PAT) is checked against nuget.org for newer versions and OSV.dev for known advisories; the Azure Retail Prices API prices B1/S1/P0v3 in each app's region; RDAP reads the registration expiry of any custom domain. Each catalog entry in `apps.json` may set `expect`, text the live page must contain; a page that answers 2xx without it is flagged. Each entry may also carry a `stack` block of short labels (`frontend`, `backend`, `realtime`, `data`, `extras` = the app's unique tech, `ai`, `signIn`, `delivery`) plus a `details` map of the same keys holding the full wording, and an `aiServices` list (`cloud`, `name`, `use`, `optional`) for the AI column. An entry may also set `androidPackage`; each scan checks anonymously whether its Google Play page and its `githubRepo` are public, and the home page links each only once it is — the home page's matrix of how each app is built.
- Push alerts go through ntfy (`Alerts:NtfyTopic`): app down/up, failed scan, spend spike, an SSL certificate crossing 14 days, the month-end forecast crossing the budget, a page losing its `expect` text, a custom domain crossing 30 days to expiry.
- `deploy.yml` is the only workflow (no infra job, no rollback, no pull-request runs). E2E tiers run locally; `scripts/post-deploy-smoke.ps1 -BaseUrl <url>` is a manual check.
- **Dropped on 2026-11-20 (owner's choice, instead of upgrading the F1 plan):** the scheduled workflows `ci-watch.yml`, `conventions.yml`, `uptime-scan.yml`, `weekly-digest.yml` and `screenshots.yml`. There is no nightly scan (a scan runs when a visitor loads a report older than 12h, so a day nobody visits has no uptime data), no Monday digest, no estate CI watcher, and no new card screenshots: cards keep the last captured picture and the home page says so. The PageSpeed score chips went with the capture.
- Shell: this repo's own `po-tokens.css` look (indigo accent, light/dark from the device, Inter and Space Grotesk), a header with the app name upper-left and Microsoft session controls upper-right (the settings gear stays removed), one `vYYMMDD` stamp lower-right (UTC date of HEAD), `/diag` cards, and Tab with nothing focused opens `/po-dev.html`, a live developer window reading `/health/live` and `/api/diag`.
- Azure behavior prefers Key Vault and managed identity when available, with local fallback configuration for development.
- The client dashboard consumes the shared Azure report models generated by the server and infrastructure layers.
- Every page shows the build date (YYMMDD) in the lower-right corner, from the client assembly's `BuildVersion` metadata.
- Theme, density, sound and details are fixed for everyone (since 2026-11-17): the theme follows the device, the layout is compact, there is no sound, and every detail shows. `/settings` has no controls; it explains this, and old stored choices are cleared on load.

## Configuration

`appsettings.json` holds values only; what each section means lives here. Secrets come from Key
Vault `kv-poshared2` under the prefix `PoPunkouterSoftware--` and are never committed.

| Key | What it does |
|---|---|
| `AzureBlobStorage:Endpoint` | Public endpoint of the account holding the `app-screenshots` container (the last captured card pictures; nothing writes them since 2026-11-20), used on the managed-identity path. Without it stored screenshots never serve. Azurite wins in Development. |
| `ApplicationInsights:ConnectionString` | Left blank; supplied by the vault secret `ApplicationInsights--ConnectionString` or `APPLICATIONINSIGHTS_CONNECTION_STRING`. |
| `FeatureFlags:EnableManagementActions` | Enables Azure controls for authenticated users. Mutations also require the same-origin `X-Management-Request: 1` header, sent by the client. `/api/config` reports whether this browser can use the controls. |
| `FeatureFlags:EnableAutoInventoryRefresh` | A portfolio load with a report older than 12h starts a background rescan (30-min cooldown). Defaults on outside Development/Testing. |
| `FeatureFlags:EnableAiSummary` | The AI status paragraph on `/azure`; `false` falls back to the rule-based narrative. |
| `StatusNarrator:*` | Azure AI Foundry chat completions on `po-aiservices-shared2` (`gpt-5.4-nano`). Deliberately NOT named `AzureAi`: the vault's estate-wide `AzureAI--*` secrets would be picked up case-insensitively and 401 every call. Blank `ApiKey` means Entra ID (needs Cognitive Services OpenAI User), falling back to the vault's `AzureOpenAI--ApiKey`. |
| `SignIns:*` | The `/users` roster, read live from the shared Application Insights component's `traces` (`UserSignedIn`). Needs Monitoring Reader; anything missing degrades to "unavailable", never a 500. `WindowDays` is capped at 90 (the component's retention). |
| `Budget:MonthlyUsd` | Monthly ceiling for the `/azure` forecast. 0 or absent shows the projection with no comparison. |
| `Alerts:*` | ntfy push alerts. Off while `NtfyTopic` is blank; on ntfy.sh the topic IS the credential, so set it only through the vault secret `PoPunkouterSoftware--Alerts--NtfyTopic`. `SiteUrl` is where a tapped alert opens. |
| `RateLimits:*` | Global (not per-IP) ceilings on `/api/signins` and `POST /api/diag/ai`, the two anonymous endpoints that cost money per call. |
| `Retention:HistoryDays` | Days before history rows are pruned (0 disables). Blob lifecycle is in `infra/main.bicep`. |
| `Pinger:*` | Background sweep that warms cold starts and records daily reachability for the uptime grid. Off means the grid uses scan observations alone. |
| `MicrosoftAuth:ClientId` | This app's Microsoft Entra application ID. Use an app registration supporting the Microsoft account types you want to admit. |
| `MicrosoftAuth:ClientSecret` | Server-only credential from `PoPunkouterSoftware--MicrosoftAuth--ClientSecret` in Key Vault (never put it in client assets or source control). |
| `MicrosoftAuth:TenantId` | Defaults to `common` for work/school and personal Microsoft accounts. A tenant GUID restricts sign-in to that tenant. All admitted authenticated users can access Azure and Users; there is no separate administrator role. |

### Microsoft sign-in setup

Register **Web** redirect URIs `http://localhost:8000/signin-oidc` and
`https://app-popunkoutersoftware2.azurewebsites.net/signin-oidc` on this app's Entra registration.
Set `MicrosoftAuth:ClientId`, `MicrosoftAuth:ClientSecret`, and optionally `MicrosoftAuth:TenantId`
through app-prefixed Key Vault secrets or server environment variables. No browser-side secret or
Graph permission is required: only `openid`, `profile`, and `email` scopes are requested.
The former management API-key gate has been replaced by authenticated sessions.
Local tests may explicitly send `X-Fake-User`; those headers never authenticate Production or Staging.

## Testing Strategy

- Unit tests (`PoPunkouterSoftware.Unit`) cover pure logic and API helpers, no I/O.
- Integration tests (`PoPunkouterSoftware.Integration`) cover host configuration, diagnostics endpoints, and Azure Table Storage behavior via `WebApplicationFactory` + Testcontainers Azurite.
- E2E tests (`PoPunkouterSoftware.E2EAPI`, `PoPunkouterSoftware.E2EUI`) cover the HTTP contract and browser flows against a live instance; on demand, not in CI, and they skip with a clear message when no app answers at `BASE_URL`. E2EUI runs axe-core's WCAG 2.1 A/AA rules over every page (`Deque.AxeCore.Playwright`) and fails on serious or critical violations.
- Integration runs in CI; locally its 6 Azurite tests skip with a clear message when Docker is not running.
- Current counts: Unit 94, Integration 45, E2E API 22, E2E UI 32 (see [docs/testing.md](docs/testing.md)).

## Developer Guidance

- Read [AGENTS.md](AGENTS.md) before changing anything (CLAUDE.md just points there); [docs/architecture.md](docs/architecture.md) is the context map.
- Keep `/api/diag/*` available but do not link them from the UI — diagnostics surface only in the `/azure` Advanced diagnostics drawer.
- Preserve Po-prefixed naming and strict build quality defaults from `Directory.Build.props`.
