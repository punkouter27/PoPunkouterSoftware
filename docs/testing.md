# Testing — PoPunkouterSoftware

Four projects, one per tier. Counts are real (2026-11-21), not caps: add a test where it buys coverage, but
prefer widening an existing test's assertions to adding a near-duplicate method (the tiers that had to be
cut back were full of one-assertion Facts that each re-fetched the same document).

| Tier | Project | Tests | Runs in CI | Needs | Without it |
| --- | --- | --- | --- | --- | --- |
| Unit | `tests/PoPunkouterSoftware.Unit` | 113 | yes | nothing | — |
| Integration | `tests/PoPunkouterSoftware.Integration` | 82 (6 use Docker) | yes | Docker for the Testcontainers Azurite tests | those 6 skip with "Docker is not running…"; the other 76 run |
| E2E API | `tests/PoPunkouterSoftware.E2EAPI` | 22 | no | an app at `BASE_URL` (default http://localhost:8000) | every test skips with "No app is answering at …" |
| E2E UI | `tests/PoPunkouterSoftware.E2EUI` | 32 | no | an app at `BASE_URL` with a stored Azure scan + Playwright Chromium | every test skips with the reason |

```powershell
dotnet build PoPunkouterSoftware.slnx -c Release
dotnet test tests/PoPunkouterSoftware.Unit -c Release --no-build
dotnet test tests/PoPunkouterSoftware.Integration -c Release --no-build
pwsh ./scripts/run-dev.ps1 -NoWatch                       # the app on :8000, for the E2E tiers
curl -s https://app-popunkoutersoftware2.azurewebsites.net/api/diag/report -o src/PoPunkouterSoftware.Api/App_Data/azure-full-report.json   # E2E UI needs a scan; git-ignored
$env:BASE_URL='http://localhost:8000'; dotnet test tests/PoPunkouterSoftware.E2EAPI -c Release --no-build
pwsh tests/PoPunkouterSoftware.E2EUI/bin/Release/net10.0/playwright.ps1 install chromium   # once
$env:BASE_URL='http://localhost:8000'; dotnet test tests/PoPunkouterSoftware.E2EUI -c Release --no-build
$env:HEADED=1; $env:BROWSER_CHANNEL='chrome'; dotnet test tests/PoPunkouterSoftware.E2EUI -c Release --no-build
```

A tier that "passes" in a second skipped: read the skip reason.

- **Unit** — strictly no I/O (HTTP stubs, in-memory). DTO-mapping tests live in Integration, never here.
- **Integration** — `WebApplicationFactory` boots the real entry point; collections run sequentially
  (`AssemblyInfo.cs`) because concurrent boots race. `TestWebApp` seeds its own report cache into `App_Data`
  and **displaces** any developer report already there (restored on dispose), so the tier never runs against a
  local report. The Azurite tests (`AzureReportStoreAzuriteTests`, `AzureReportStoreHistoryTests`,
  `AzuriteWebAppTests`) start a throwaway Azurite through Testcontainers and are `[DockerFact]`s
  (`DockerFactAttribute.cs`). `ProductionBootTests` boots under `Production` with every external dependency
  blanked — the only coverage of that environment's pipeline — and asserts `/api/diag` leaks nothing.
- **E2E API** — plain `HttpClient` against a live host; every assertion holds against both a fresh localhost
  instance and production, so `BASE_URL=https://app-popunkoutersoftware2.azurewebsites.net` is a post-deploy
  smoke. Gated by `[LiveAppFact]` / `[LiveAppTheory]` (`LiveAppFactAttribute.cs`).
- **E2E UI** — Playwright, every test at mobile portrait (390×844) and desktop (1440×1000). Runs **axe-core**
  (WCAG 2.1 A/AA) over all five pages at both viewports and fails on serious/critical only; the same pass
  asserts the `vYYMMDD` stamp sits in the lower-right corner. It waits for `appRepairRadzenA11y` first, or it
  reports icon ligatures in names. Measure rendered boxes, not `scrollWidth`, for overflow checks (see
  "Things that will bite you" in [architecture.md](architecture.md)). Full runs on the shared machine hold the
  engine lock.
