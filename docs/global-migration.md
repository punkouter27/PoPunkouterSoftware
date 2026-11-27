# GLOBAL → PoPunkouterSoftware

The `_CODEMATT/global` folder was a non-repo hub beside every Po app: it crawled the fleet,
generated static HTML pages, held the UNITE creature prefabs, and ran a local Blazor command
centre. This app now owns that functionality. The owner approved the move and the sign-in
boundary on 2026-11-27.

## What lives where now

| GLOBAL thing | Now |
| --- | --- |
| `UNITE/` (rules, contract schema, 6 prefabs, tools, runs) | [`docs/unite/`](../docs/unite) — data and prose, prefabs referenced by path |
| `station/scripts/` crawlers (fleet, lessons) | [`scripts/global/`](../scripts/global) — data only, no HTML |
| `rl_training/scripts/` crawler + history recorder | [`scripts/global/`](../scripts/global) |
| `station/*.html` (fleet health, briefing, vault, showroom, ship board, lessons, disk, incubator) | Blazor pages under `/global` |
| `rl_training/*.html` (overview, progress over time) | `/global/rl` |
| `index.html` hub, `RUN_ME.bat` | `/global` and `scripts/global/refresh_and_push.ps1` (crawl, commit, push) |
| `station/automation/station_server.py` (the :5050 launcher) | `scripts/global/local_helper.py` — same port, no file serving, token + origin checked |
| `rl_training/gpu_queue/run_gpu_queue.ps1` + `gpu_queue.html` | `scripts/global/run_gpu_queue.ps1`, launched from `/global/queue` |
| `BENCHMARK.bat` / `BENCHMARK.ps1` | `scripts/global/run_benchmarks.ps1`, launched from `/global/pc` |
| `rl_training/standards/vault_import.py` | `scripts/global/vault_import.py`, run from `/global/pc` (check first, then copy) |
| `blazor_apps/` and `rl_training/` standards + prompts | `docs/global-standards/`, shown at `/global/standards` |
| the 07:00 task "CODEMATT Morning Briefing" | same task, now `refresh_and_push.ps1` (`scripts/global/register_daily_refresh.ps1`) |
| `station/config/*.json` | `src/PoPunkouterSoftware.Api/App_Data/global/config/` |

The `_CODEMATT/GLOBAL` folder itself was archived to `_CODEMATT/GLOBAL-archive-20261127.zip` and removed.

## The fourteen pages

| Route | What it shows |
| --- | --- |
| `/global` | Hub: fleet counts, CI failures, disk, and a tile per tool |
| `/global/fleet` | Per repo: branch, dirty/ahead/behind, last commit, CI conclusion, brains, models, disk, live URL |
| `/global/briefing` | What changed between the last two crawls: commits, new brains, disk movement, CI flips, working-tree churn |
| `/global/vault` | Every `.onnx` grouped by socket (obs → action), starred brains, every MJCF body |
| `/global/showroom` | Newest captures per repo (linked on GitHub, never copied) and live URLs |
| `/global/readiness` | Ship board: Prototype → Shipped, six-point checklist, suggested stage with a reason |
| `/global/lessons` | Traps and rules harvested from every repo's docs, with search, repo and kind filters |
| `/global/disk` | Space per repo and folder type, heaviest folders, how safe each is to delete |
| `/global/rl` | Shipped brains, benchmark facts, and the recorded history chart |
| `/global/queue` | Training queue: every job in `config/train_jobs.json`, whether the crawl found what it needs, and (with the helper connected) dry run / start on the PC's GPU or send to the Google Cloud GPU |
| `/global/unite` | The 6 prefabs, the document list, and the rules |
| `/global/pc` | This PC: refresh and push the snapshot, run benchmarks, copy a brain, drive the cloud GPU VM |
| `/global/standards` | The Blazor and RL standards, the portability contract and the saved prompts |
| `/global/incubator` | Five questions → which repo to start from, brains to bring, clone command |

## How the data gets there

```
scripts/global/build_global_data.py
        │  crawls ../Po* (read-only) and docs/unite
        ▼
src/PoPunkouterSoftware.Api/App_Data/global/*.json     (committed)
        │  GlobalDataStore (timestamp-cached, degrades to empty)
        ▼
/api/global/*        (owner sign-in required, like /azure and /users)
        ▼
/global/*            (Blazor WASM pages)
```

`App_Data` sits under the content root, so it is **never** served as a static file — the API is the
only way in. That matters: the snapshot names local paths, uncommitted files and commit hashes.

The crawler writes `snake_case`; the API answers `camelCase`. Two source-generated
`JsonSerializerContext`s cover the two shapes (`GlobalCrawlerJsonContext` for the files,
`GlobalJsonContext` for the wire), so nothing depends on reflection in the trimmed Release build.

## Refreshing the snapshot

```powershell
python scripts/global/build_global_data.py            # ~2 min: fleet crawl + disk sizes + gh
python scripts/global/build_global_data.py --only rl  # just the training facts
```

Then commit `App_Data/global` (the RLS and fleet data are the product) and reload `/global`.

`App_Data/global/history/snapshots/` and `App_Data/global/rl/snapshots/` are **not** committed —
they are the full per-run archive and only the compact timelines are needed for the briefing and
the RL chart. See `.gitignore`.

## Who can see it

Only the owner. `MicrosoftAuth:OwnerEmail` names the one account that can complete Microsoft
sign-in (`Host/OwnerAccess.cs`); any other account is refused before a session exists, so the
header links to Azure, Users and Fleet & training never render for anyone else. With no owner
configured, Production refuses everybody. One-time setup:
`pwsh ./scripts/setup-microsoft-signin.ps1 -OwnerEmail <owner address>`.

## Actions on the PC (the local helper)

The site runs in Azure and cannot reach the owner's PC, so everything that has to happen there
goes from the browser straight to `scripts/global/local_helper.py` on `http://127.0.0.1:5050`:

```
signed-in /global page (browser on the PC)
        │  fetch, with the token from this browser's localStorage
        ▼
local_helper.py on 127.0.0.1:5050     (start: scripts\global\start_local_helper.bat)
        │  fixed list of actions; commands come from the catalogs, never from the page
        ▼
VS Code / Explorer / Unity / Unreal · run_gpu_queue.ps1 · run_benchmarks.ps1 ·
vault_import.py · refresh_and_push.ps1 · scripts/gcp/gcp_queue.py
```

| Where | Buttons |
| --- | --- |
| `/global/fleet` | per repo: Code, Folder, Status (git), Engine (Unity / Unreal) |
| `/global/queue` | tick jobs + hours → Dry run / Start on this PC; Recent runs; per cloud job: Dry run / Send to the cloud GPU |
| `/global/pc` | Crawl only / Crawl, commit and push; Benchmarks; Copy a brain (check fit, then copy); cloud VM Status / Check setup / Start / Stop |

Rules the helper enforces (tested in `scripts/global/test_local_helper.py`):

- listens on 127.0.0.1 only, and refuses a request whose `Host` is not that address;
- a browser request must carry this site's `Origin` (the live site or `localhost:8000`);
- every action needs the token it prints on start (`scripts/global/.local/helper_token.txt`,
  git-ignored). The owner pastes it once on the site; it stays in that browser;
- the page sends names only — a repo, job ids, hours. Repos must be git folders in the
  workspace, job ids must be in `train_jobs.json` / `cloud_jobs.json`, and the command is
  always the catalog's;
- it serves no files and deletes nothing.

Away from the PC, or with the helper stopped, the pages still read and the buttons are not
rendered. Anything that starts real work (training, a cloud run, the cloud VM, a brain copy)
takes a second, explicit click that says what will happen; cloud actions say they cost money.
The site's CSP allows exactly `http://127.0.0.1:5050` in `connect-src` for this.

Local state — the token, saved queues, queue runs, benchmark logs, refresh log — lives in
`scripts/global/.local/` and is never committed.

### The daily refresh

`scripts/global/refresh_and_push.ps1` crawls, commits **only** `App_Data/global` when it
changed, rebases onto `origin/master` and pushes (which deploys). The Windows task
"CODEMATT Morning Briefing" runs it at 07:00; point the task at this repo with
`powershell -ExecutionPolicy Bypass -File scripts\global\register_daily_refresh.ps1`.

## Not carried over

- Editing the ship-board stage and the vault's gold stars from a page (`save_config`). Both
  files were empty in GLOBAL; edit `App_Data/global/config/readiness.json` and
  `vault_gold.json` by hand.
- `quick_transfer_actions.py`, `train_next_explorer.py` and the HTML generators: they only
  produced static pages the app now renders itself.

The recorded history did come across: `history/station_timeline.jsonl` and
`rl/rl_timeline.jsonl` start with GLOBAL's 16 fleet crawls and 15 RL records.

## Tests

- Unit: `tests/PoPunkouterSoftware.Unit/GlobalDataStoreTests.cs` — parses the real committed
  snapshot (so a crawler/API drift fails the build), plus missing-file, corrupt-file, briefing-delta,
  socket grouping, gold stars and readiness-suggestion cases.
- Helper: `python -m unittest scripts/global/test_local_helper.py` — who may call it, and that a page
  can never supply a command (15 tests; nothing is started on the PC).
- Browser: `tests/PoPunkouterSoftware.E2EUI/GlobalUiTests.cs` — the PC buttons appear only when the
  helper is connected, a start needs a confirmation, and only ids and hours are sent (the helper
  is a Playwright route, so nothing runs).
- Integration: `tests/PoPunkouterSoftware.Integration/GlobalEndpointsTests.cs` — every route 401s
  anonymously, answers signed in, and the snapshot is deployed beside the app.

## Deliberate deviations

| Deviation | Why |
| --- | --- |
| `snake_case` file naming on the API's raw-read context | the crawlers are unchanged Python tools; rewriting every key would risk the crawl for cosmetics |
| Showroom links to GitHub instead of rendering the images | no second copy of 900 captures, and the biggest GIFs are tens of MB |
| No scheduled crawl | F1 has no Always On; the owner runs the crawl and commits, as with every other snapshot in this repo |
| `docs/unite/tools/` sits beside the prefabs | the tools resolve `creatures/` relative to themselves; moving them to `scripts/` would need path surgery in ten files |
