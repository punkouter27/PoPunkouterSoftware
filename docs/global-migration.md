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
| `index.html` hub, `RUN_ME.bat`, `BENCHMARK.*` | `/global` and `scripts/global/build_global_data.py` |
| `station/config/*.json` | `src/PoPunkouterSoftware.Api/App_Data/global/config/` |
| `PoStation` (the local command centre at :5050) | unchanged — it is its own repo, not part of this move |

The one thing deliberately **not** carried over is `station/scripts/vault_import.py`: it is the
only tool that wrote into a repo, and copying a brain stays a manual step.

## The twelve pages

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
| `/global/queue` | Training queue (read-only): every job in `config/train_jobs.json`, its command, and whether the crawl found everything it needs on the PC |
| `/global/unite` | The 6 prefabs, the document list, and the rules |
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

## Not carried over

- **Starting or stopping training from the page.** `run_gpu_queue.ps1` and the hub buttons of
  `station_server.py` (open VS Code / folder / engine, git status, update all, save and launch
  a queue) run programs on the owner's PC. This site runs in Azure, so `/global/queue` is a
  read-only list; the old scripts stay in `_CODEMATT/GLOBAL`.
- **The 07:00 scheduled refresh** and `BENCHMARK.ps1` (each RL repo keeps its own benchmark kit).
- **`blazor_apps/` standards prose** (each repo's AGENTS.md carries its own standard).

The recorded history did come across: `history/station_timeline.jsonl` and
`rl/rl_timeline.jsonl` start with GLOBAL's 16 fleet crawls and 15 RL records.

## Tests

- Unit: `tests/PoPunkouterSoftware.Unit/GlobalDataStoreTests.cs` — parses the real committed
  snapshot (so a crawler/API drift fails the build), plus missing-file, corrupt-file, briefing-delta,
  socket grouping, gold stars and readiness-suggestion cases.
- Integration: `tests/PoPunkouterSoftware.Integration/GlobalEndpointsTests.cs` — every route 401s
  anonymously, answers signed in, and the snapshot is deployed beside the app.

## Deliberate deviations

| Deviation | Why |
| --- | --- |
| `snake_case` file naming on the API's raw-read context | the crawlers are unchanged Python tools; rewriting every key would risk the crawl for cosmetics |
| Showroom links to GitHub instead of rendering the images | no second copy of 900 captures, and the biggest GIFs are tens of MB |
| No scheduled crawl | F1 has no Always On; the owner runs the crawl and commits, as with every other snapshot in this repo |
| `docs/unite/tools/` sits beside the prefabs | the tools resolve `creatures/` relative to themselves; moving them to `scripts/` would need path surgery in ten files |
