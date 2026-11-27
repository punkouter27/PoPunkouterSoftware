"""Reusable crawler that extracts real ground-truth RL training facts from all 10 RL repos
in the _CODEMATT workspace into App_Data/global/rl_facts.json (served by /api/global/rl).
"""

from __future__ import annotations

import json
import os
import re
import xml.etree.ElementTree as ET
from pathlib import Path

from station_common import RL_OUT as OUT_DIR
from station_common import WORKSPACE as ROOT

OUT_DIR.mkdir(parents=True, exist_ok=True)

REPOS = [
    "PoBasketball",
    "PoBox",
    "PoFootball",
    "PoHillKing",
    "PoOlympic",
    "PoRaceUnreal",
    "PoRobotTrain",
    "PoSlapUnreal",
    "PoSoccer",
    "PoSumo",
]

SKIP_DIRS = {".git", ".venv", "Library", "Intermediate", "Saved", "Temp", "obj", "bin", "node_modules", "Packages", "Builds", "Logs", "__pycache__"}


def find_files(repo: Path, exts: tuple[str, ...], max_depth: int = 5) -> list[Path]:
    found = []
    base_depth = len(repo.parts)
    for dirpath, dirnames, filenames in os.walk(repo):
        p = Path(dirpath)
        depth = len(p.parts) - base_depth
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS and not d.startswith(".")]
        if depth >= max_depth:
            dirnames[:] = []
        for fn in filenames:
            if fn.lower().endswith(exts):
                found.append(p / fn)
    return found


def inspect_onnx(path: Path) -> dict:
    info = {"rel": str(path.relative_to(ROOT)).replace("\\", "/"), "name": path.name, "kb": round(path.stat().st_size / 1024, 1)}
    try:
        import onnxruntime as ort
        sess = ort.InferenceSession(str(path), providers=["CPUExecutionProvider"])
        info["inputs"] = [(i.name, list(i.shape)) for i in sess.get_inputs()]
        info["outputs"] = [(o.name, list(o.shape)) for o in sess.get_outputs()]
    except Exception as e:
        info["err"] = str(e)[:120]
    return info


def inspect_mjcf(path: Path) -> dict | None:
    try:
        tree = ET.parse(path)
        root = tree.getroot()
        if root.tag != "mujoco":
            return None
        joints = root.findall(".//joint")
        free_joints = [j for j in joints if j.attrib.get("type") == "free"]
        actuated_joints = [j for j in joints if j.attrib.get("type") != "free"]
        acts = root.findall(".//actuator/*")
        bodies = root.findall(".//body")
        opt = root.find(".//option")
        timestep = opt.attrib.get("timestep") if opt is not None else None
        return {
            "rel": str(path.relative_to(ROOT)).replace("\\", "/"),
            "bodies": len(bodies),
            "joints_total": len(joints),
            "free_joints": len(free_joints),
            "actuated_joints": len(actuated_joints),
            "actuators": len(acts),
            "timestep": timestep,
        }
    except Exception:
        return None


def sum_by_machine(runs: list[dict]) -> list[dict]:
    """Aggregate runs per hostname so benchmarks from different computers can be compared."""
    machines: dict[str, dict] = {}
    for r in runs:
        m = r.get("machine") or {}
        host = (m.get("hostname") or "unknown").strip() or "unknown"
        e = machines.setdefault(host, {
            "hostname": host,
            "gpu": m.get("gpu"),
            "cpu": m.get("cpu"),
            "runs": 0,
            "failed_runs": 0,
            "best_env_steps_per_s": 0,
            "best_num_envs": None,
            "best_when": None,
            "last_env_steps_per_s": None,
            "last_when": None,
        })
        e["gpu"] = e["gpu"] or m.get("gpu")
        e["cpu"] = e["cpu"] or m.get("cpu")
        sps = (r.get("result") or {}).get("env_steps_per_s")
        if not r.get("ok") or not isinstance(sps, (int, float)) or sps <= 0:
            e["failed_runs"] += 1
            continue
        e["runs"] += 1
        if sps > e["best_env_steps_per_s"]:
            e["best_env_steps_per_s"] = sps
            e["best_num_envs"] = (r.get("workload") or {}).get("num_envs")
            e["best_when"] = r.get("when")
        when = r.get("when") or ""
        if e["last_when"] is None or when >= e["last_when"]:
            e["last_when"] = when
            e["last_env_steps_per_s"] = sps
    return sorted(machines.values(), key=lambda x: -x["best_env_steps_per_s"])


def read_benchmark(repo: Path) -> dict | None:
    p = repo / "benchmark" / "history.json"
    if not p.exists():
        return None
    try:
        data = json.loads(p.read_text(encoding="utf-8"))
        runs = [r for r in data.get("runs", []) if r.get("ok")]
        if not runs:
            runs = data.get("runs", [])
        if not runs:
            return None
        latest = runs[-1]
        return {
            "when": latest.get("when"),
            "ok": latest.get("ok"),
            "machine_gpu": (latest.get("machine") or {}).get("gpu"),
            "trainer": (latest.get("workload") or {}).get("trainer"),
            "script": (latest.get("workload") or {}).get("script"),
            "num_envs": (latest.get("workload") or {}).get("num_envs"),
            "horizon": (latest.get("workload") or {}).get("horizon"),
            "net": (latest.get("workload") or {}).get("net"),
            "step_time_ms": (latest.get("result") or {}).get("step_time_ms"),
            "sim_ms": (latest.get("result") or {}).get("sim_ms"),
            "learn_ms": (latest.get("result") or {}).get("learn_ms"),
            "env_steps_per_s": (latest.get("result") or {}).get("env_steps_per_s"),
            "versions": latest.get("versions"),
            "total_benchmark_runs": len(data.get("runs", [])),
            "by_machine": sum_by_machine(data.get("runs", [])),
        }
    except Exception as e:
        return {"error": str(e)}


def find_visuals(repo: Path) -> list[dict]:
    imgs = []
    for sub in ("SCREENSHOTS", "docs", "parity"):
        sp = repo / sub
        if not sp.exists():
            continue
        for p in find_files(sp, (".gif", ".png", ".jpg", ".jpeg", ".webp"), max_depth=4):
            sz = p.stat().st_size
            if sz < 15_000:
                continue
            imgs.append({
                "rel": str(p.relative_to(ROOT)).replace("\\", "/"),
                "ext": p.suffix.lower(),
                "kb": round(sz / 1024, 1),
                "mtime": int(p.stat().st_mtime),
            })
    imgs.sort(key=lambda x: (0 if x["ext"] == ".gif" else 1, -x["mtime"]))
    return imgs[:12]


def main() -> None:
    results = {}
    for name in REPOS:
        repo = ROOT / name
        if not repo.exists():
            continue
        # Shipped ONNX models (prefer Assets/Content/shared/models/RobotControl, skip runs/checkpoints)
        all_onnx = find_files(repo, (".onnx",), max_depth=6)
        shipped_onnx = [
            p for p in all_onnx
            if not any(part.lower() in ("runs", "checkpoints", "archive", "results", "dist", "_benchmark") for part in p.parts)
        ]
        onnx_infos = [inspect_onnx(p) for p in shipped_onnx[:35]]

        # MJCF XMLs
        xmls = find_files(repo, (".xml",), max_depth=5)
        mjcf_infos = []
        for x in xmls:
            if any(part.lower() in ("spritesheet", "kenney", "archive") for part in x.parts):
                continue
            info = inspect_mjcf(x)
            if info and (info["actuators"] > 0 or info["joints_total"] > 0):
                mjcf_infos.append(info)

        # YAML configs (ML-Agents / deploy)
        yamls = [
            str(p.relative_to(ROOT)).replace("\\", "/")
            for p in find_files(repo, (".yaml", ".yml"), max_depth=4)
            if ".github" not in p.parts and "archive" not in [x.lower() for x in p.parts]
        ]

        # Reward config JSONs
        rew_cfgs = [
            str(p.relative_to(ROOT)).replace("\\", "/")
            for p in find_files(repo, (".json",), max_depth=4)
            if "reward" in p.name.lower()
        ]

        results[name] = {
            "benchmark": read_benchmark(repo),
            "onnx_total_found": len(all_onnx),
            "onnx_shipped_count": len(shipped_onnx),
            "onnx_samples": onnx_infos[:15],
            "mjcf_count": len(mjcf_infos),
            "mjcf_samples": mjcf_infos[:10],
            "yaml_configs": yamls[:12],
            "reward_configs": rew_cfgs[:5],
            "visuals": find_visuals(repo),
        }
        b = results[name]["benchmark"] or {}
        print(
            f"{name:14s} | onnx={len(shipped_onnx):2d} ({len(all_onnx):2d} tot) | mjcf={len(mjcf_infos):2d} | "
            f"envs={str(b.get('num_envs','-')):>5s} | sps={str(b.get('env_steps_per_s','-')):>7s} | "
            f"step_ms={str(b.get('step_time_ms','-')):>7s} | visuals={len(results[name]['visuals']):2d}"
        )

    out_json = OUT_DIR / "rl_facts.json"
    out_json.write_text(json.dumps(results, indent=2), encoding="utf-8")
    print(f"\nSaved {out_json}")


if __name__ == "__main__":
    main()

