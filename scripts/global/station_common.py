"""Paths and helpers shared by the GLOBAL crawlers.

The crawlers used to live in a standalone `global/` folder beside the repos and generated
static HTML. They now live in this repo and write ONE thing: the JSON snapshot under
`src/PoPunkouterSoftware.Api/App_Data/global`, which the app serves behind Microsoft sign-in.

The crawlers still read the sibling Po repos; nothing here writes to them.
"""
from __future__ import annotations

import json
import os
from pathlib import Path

HERE = Path(__file__).resolve().parent            # <repo>/scripts/global
REPO = HERE.parents[1]                            # PoPunkouterSoftware
# The workspace that holds every Po repo. Override with CODEMATT_ROOT when the repos live
# somewhere other than the parent of this clone.
WORKSPACE = Path(os.environ.get("CODEMATT_ROOT", REPO.parent)).resolve()

OUT = REPO / "src" / "PoPunkouterSoftware.Api" / "App_Data" / "global"
DATA = OUT                                        # crawler output, served by /api/global/*
CONFIG = OUT / "config"                           # hand-edited inputs
HIST = OUT / "history"
RL_OUT = OUT / "rl"


def load_json(path: Path, default):
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return default


def write_json(path: Path, obj, indent: int | None = 1) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(obj, indent=indent, ensure_ascii=False), encoding="utf-8")
    return path


def fmt_bytes(n) -> str:
    if n is None:
        return "–"
    n = float(n)
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if abs(n) < 1024 or unit == "TB":
            return f"{n:.0f} {unit}" if unit in ("B", "KB") else f"{n:.1f} {unit}"
        n /= 1024
    return f"{n:.1f} TB"
