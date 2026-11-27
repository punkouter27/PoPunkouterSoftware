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

`config/` holds the hand-edited inputs the previous station kept: `train_jobs.json`,
`readiness.json`, `vault_gold.json`, `lessons_manual.json`. Edit those directly.

## Read-only

Every crawler only reads the sibling repos. The one that writes into a repo was `vault_import.py`,
which is **not** vendored: copying a brain between repos stays a manual step. Nothing here deletes
anything.

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
