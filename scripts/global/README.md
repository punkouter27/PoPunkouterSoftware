# scripts/global — the GLOBAL crawlers

The `global/` folder beside the repos used to hold these crawlers **and** the static HTML they
generated. The pages now live in this app under `/global` (Microsoft sign-in required), so the
crawlers here produce **data only**: one JSON snapshot the app reads.

```
python scripts/global/build_global_data.py              # everything
python scripts/global/build_global_data.py --no-gh      # skip the GitHub CI lookups
python scripts/global/build_global_data.py --only fleet # one step: fleet | lessons | rl | unite
```

Then reload `/global` — the API re-reads a file when its timestamp changes, so no restart is needed.

## What each step writes

| Step | Script | Output (under `src/PoPunkouterSoftware.Api/App_Data/global/`) | Served by |
| --- | --- | --- | --- |
| `fleet` | `collect_fleet.py` | `fleet.json`, `history/station_timeline.jsonl` | `/api/global/fleet`, `/api/global/briefing`, `/api/global/vault`, `/api/global/readiness`, `/api/global/showroom`, `/api/global/disk` |
| `lessons` | `harvest_lessons.py` | `lessons.json` | `/api/global/lessons` |
| `rl` | `collect_rl_facts.py`, `record_rl_history.py` | `rl_facts.json`, `rl/rl_timeline.jsonl` | `/api/global/rl` |
| `unite` | `build_global_data.py` (inline) | `unite.json` | `/api/global/unite` |
| `queue` | `build_global_data.py` (inline) | `queue.json` (jobs, missing requirements, cloud jobs) | `/api/global/queue` |
| `standards` | `build_global_data.py` (inline) | `standards.json` (from `docs/global-standards`) | `/api/global/standards` |

`config/` holds the hand-edited inputs the previous station kept: `train_jobs.json`,
`readiness.json`, `vault_gold.json`, `lessons_manual.json`. Edit those directly.

## Read-only crawlers, and the one tool that writes

Every crawler only reads the sibling repos. `vault_import.py` is the exception: it copies one
`.onnx` into another repo's `vault_imports/` folder. It never overwrites, never commits, and
`--dry-run` only reports whether the brain's socket fits. Nothing here deletes anything.

## Things that run on the PC

| Script | What it does |
| --- | --- |
| `start_local_helper.bat` → `local_helper.py` | The helper on `127.0.0.1:5050` that the signed-in `/global` pages call. Prints a token to paste once on the site. See [docs/global-migration.md](../../docs/global-migration.md#actions-on-the-pc-the-local-helper) |
| `refresh_and_push.ps1` | Crawl, commit only `App_Data/global`, rebase, push. `-NoPush` crawls only |
| `register_daily_refresh.ps1` | Points the 07:00 task "CODEMATT Morning Briefing" at `refresh_and_push.ps1` |
| `run_gpu_queue.ps1` | Runs a saved queue on this PC's GPU (cool-down, time limits, summary per run) |
| `run_benchmarks.ps1` | Runs each RL repo's own `benchmark.ps1 -Run`, then refreshes |
| `vault_import.py` | Copies a brain between repos (above) |
| `test_local_helper.py` | `python -m unittest scripts/global/test_local_helper.py` |

`.local/` (git-ignored) holds the helper token, saved queues, queue runs, benchmark logs and
the refresh log.

## Where the workspace is

`station_common.py` treats the parent of this clone as the workspace (`_CODEMATT`). Override it
when the repos live elsewhere:

```powershell
$env:CODEMATT_ROOT = 'D:\work\_CODEMATT'
python scripts/global/build_global_data.py --only fleet
```

## What was retired

`build_station.py`, `pages_a/b/c.py`, `hub_extras.py`, `build_rl_dashboard.py`,
`plain_english_sections.py`, `quick_transfer_actions.py`, `train_next_explorer.py` and the
`.bat`/`.ps1` launchers generated HTML pages. The app renders those pages now (see
[docs/global-migration.md](../../docs/global-migration.md)), so they were not carried over —
keeping them would mean two renderers drifting apart.

The UNITE tools and prefabs moved to [`docs/unite`](../../docs/unite/README.md), beside the data
they operate on.
