"""
Central Station fleet crawler.

Walks every repo folder under _CODEMATT and records, per repo:
  * kind (unreal / unity / blazor / dotnet / python / web) and RL membership
  * git state (branch, dirty files, ahead/behind, last commit, commit activity)
  * CI (workflow files + latest GitHub Actions run via `gh`, optional)
  * disk usage split into categories (engine caches, venvs, build output,
    training output, media, .git, everything else) + the heavy folders found
  * every .onnx brain (size, mtime, short sha1, input/output shapes)
  * newest media in SCREENSHOTS/ (for the showroom)
  * docs / tests presence

Writes (under src/PoPunkouterSoftware.Api/App_Data/global):
  fleet.json                        (latest; served by /api/global/fleet)
  history/snapshots/fleet_<stamp>.json   (full archive per run; git-ignored)
  history/station_timeline.jsonl         (compact append-only log; served by /api/global/briefing)

Usage:  python collect_fleet.py [--no-gh] [--no-sizes]
Read-only with respect to every repo.
"""
from __future__ import annotations

import datetime as dt
import hashlib
import json
import os
import re
import subprocess
import sys
from pathlib import Path

from station_common import DATA, HIST
from station_common import WORKSPACE as ROOT

RL_REPOS = {
    "PoBasketball", "PoBox", "PoFootball", "PoHillKing", "PoOlympic",
    "PoRaceUnreal", "PoRobotTrain", "PoSlapUnreal", "PoSoccer", "PoSumo",
}
RL_STACK = {
    "PoBasketball": "Unreal + MuJoCo", "PoRaceUnreal": "Unreal + MuJoCo",
    "PoRobotTrain": "Unreal + MuJoCo", "PoSlapUnreal": "Unreal + MuJoCo",
    "PoBox": "Unity + MuJoCo", "PoHillKing": "Unity + MuJoCo", "PoOlympic": "Unity + MuJoCo",
    "PoFootball": "Unity ML-Agents", "PoSoccer": "Unity ML-Agents", "PoSumo": "Unity ML-Agents",
}
IGNORE_TOP = {"global", "_rl_overview_media"}  # lower-case: the hub folder is `global/`

MEDIA_EXT = {".gif", ".png", ".jpg", ".jpeg", ".webp"}
TRAIN_OUT_NAMES = {"runs", "checkpoints", "ckpt", "results", "wandb", "outputs", "_benchmark"}
WALK_SKIP_FOR_FILES = {".git", ".venv", "venv", "Library", "Intermediate", "Saved", "Temp", "obj", "bin",
                       "node_modules", "DerivedDataCache", "Logs", "__pycache__", "Builds", "Binaries"}

NOW = dt.datetime.now().astimezone()


def run(cmd: list[str], cwd: Path | None = None, timeout: int = 30) -> str:
    try:
        r = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True, timeout=timeout,
                           encoding="utf-8", errors="replace")
        return r.stdout.strip() if r.returncode == 0 else ""
    except Exception:
        return ""


# --------------------------------------------------------------------------- kind
def detect_kind(repo: Path) -> dict:
    top = {p.name for p in repo.iterdir()} if repo.exists() else set()
    sub = []
    for p in repo.iterdir():
        if p.is_dir() and p.name not in WALK_SKIP_FOR_FILES and not p.name.startswith("."):
            try:
                sub.extend(q for q in p.iterdir())
            except OSError:
                pass
    names = list(top) + [q.name for q in sub]
    uproject = [h for pat in ("*.uproject", "*/*.uproject", "*/*/*.uproject") for h in repo.glob(pat)
                if "Intermediate" not in h.parts]
    unity = any((repo / n / "ProjectSettings" / "ProjectVersion.txt").exists() for n in [""] + [p.name for p in repo.iterdir() if p.is_dir()])
    sln = [n for n in names if n.endswith((".sln", ".slnx"))]
    csproj_paths = list(repo.glob("*.csproj")) + list(repo.glob("*/*.csproj")) + list(repo.glob("src/*/*.csproj"))
    blazor = False
    for cp in csproj_paths[:30]:
        try:
            t = cp.read_text(encoding="utf-8", errors="ignore")
            if "Microsoft.NET.Sdk.BlazorWebAssembly" in t or "Components.WebAssembly" in t or "Microsoft.NET.Sdk.Web" in t:
                blazor = blazor or any(cp.parent.rglob("*.razor"))
        except OSError:
            pass
    kind = ("unreal" if uproject else "unity" if unity else "blazor" if blazor else
            "dotnet" if (sln or csproj_paths) else "python" if ("pyproject.toml" in top or "requirements.txt" in top) else
            "web" if "package.json" in top else "other")
    unity_version = ""
    if unity:
        for pv in [repo / "ProjectSettings" / "ProjectVersion.txt", *repo.glob("*/ProjectSettings/ProjectVersion.txt")]:
            if pv.exists():
                m = re.search(r"m_EditorVersion:\s*(\S+)", pv.read_text(errors="ignore"))
                if m:
                    unity_version = m.group(1)
                    break
    uproject_path = ""
    if uproject:
        hits = [h for pat in ("*.uproject", "*/*.uproject", "*/*/*.uproject") for h in repo.glob(pat)]
        if hits:
            uproject_path = hits[0].relative_to(ROOT).as_posix()
    return {"kind": kind, "unity_version": unity_version, "uproject": uproject_path,
            "has_csproj": bool(csproj_paths), "has_sln": bool(sln)}


# --------------------------------------------------------------------------- git
def git_info(repo: Path) -> dict:
    if not (repo / ".git").exists():
        return {"is_git": False}
    g = lambda *a: run(["git", "-C", str(repo), *a])
    porcelain = g("status", "--porcelain")
    dirty = [ln for ln in porcelain.splitlines() if ln.strip()]
    ab = g("rev-list", "--left-right", "--count", "@{u}...HEAD")
    behind, ahead = (int(x) for x in ab.split()) if ab and len(ab.split()) == 2 else (None, None)
    last = g("log", "-1", "--format=%cI%x1f%s%x1f%h")
    last_iso, last_msg, last_hash = (last.split("\x1f") + ["", "", ""])[:3]
    remote = g("remote", "get-url", "origin")
    slug = ""
    m = re.search(r"github\.com[:/]([^/]+/[^/.]+)", remote)
    if m:
        slug = m.group(1)
    days = None
    if last_iso:
        try:
            days = round((NOW - dt.datetime.fromisoformat(last_iso)).total_seconds() / 86400, 1)
        except ValueError:
            pass
    def count_since(d):
        out = g("rev-list", "--count", f"--since={d} days ago", "HEAD")
        return int(out) if out.isdigit() else 0
    total = g("rev-list", "--count", "HEAD")
    recent = g("log", "-8", "--format=%cI%x1f%h%x1f%s")
    return {
        "is_git": True, "branch": g("branch", "--show-current"), "dirty_count": len(dirty),
        "dirty_files": dirty[:15], "ahead": ahead, "behind": behind,
        "last_commit_iso": last_iso, "last_commit_msg": last_msg, "last_commit_hash": last_hash,
        "days_since_commit": days, "commits_7d": count_since(7), "commits_30d": count_since(30),
        "commits_total": int(total) if total.isdigit() else 0, "remote": remote, "slug": slug,
        "recent_commits": [dict(zip(("iso", "hash", "msg"), ln.split("\x1f"))) for ln in recent.splitlines() if ln],
    }


def ci_info(repo: Path, slug: str, use_gh: bool) -> dict:
    wf_dir = repo / ".github" / "workflows"
    workflows = sorted(p.name for p in wf_dir.glob("*.y*ml")) if wf_dir.exists() else []
    latest = None
    if use_gh and slug and workflows:
        out = run(["gh", "run", "list", "-R", slug, "-L", "5", "--json",
                   "conclusion,status,workflowName,createdAt,url,headBranch,event"], timeout=25)
        try:
            runs = json.loads(out) if out else []
        except json.JSONDecodeError:
            runs = []
        if runs:
            latest = runs[0]
            latest["recent"] = [r.get("conclusion") or r.get("status") for r in runs]
    return {"workflows": workflows, "latest_run": latest}


# --------------------------------------------------------------------------- disk
def tree_size(path: Path) -> tuple[int, int, int]:
    total = files = errors = 0
    stack = [str(path)]
    while stack:
        cur = stack.pop()
        try:
            with os.scandir(cur) as it:
                for e in it:
                    try:
                        if e.is_dir(follow_symlinks=False):
                            stack.append(e.path)
                        elif e.is_file(follow_symlinks=False):
                            total += e.stat(follow_symlinks=False).st_size
                            files += 1
                    except OSError:
                        errors += 1
        except OSError:
            errors += 1
    return total, files, errors


def classify_dir(d: Path, parent_names: set[str]) -> tuple[str, str] | None:
    """Return (category, cleanup_safety) for a heavy folder, else None."""
    n = d.name
    if n == ".git":
        return "git", "never"
    if n in (".venv", "venv"):
        return "python_env", "recreate"
    if n == "node_modules":
        return "node", "regenerable"
    if n in ("Library", "Temp", "Logs") and "ProjectSettings" in parent_names:
        return "unity_cache", "regenerable" if n != "Library" else "slow_rebuild"
    if n in ("Intermediate", "DerivedDataCache") and any(p.endswith(".uproject") for p in parent_names):
        return "unreal_cache", "regenerable"
    if n == "Saved" and any(p.endswith(".uproject") for p in parent_names):
        return "unreal_saved", "review"
    if n == "Binaries" and any(p.endswith(".uproject") for p in parent_names):
        return "unreal_binaries", "slow_rebuild"
    if n in ("bin", "obj") and any(p.endswith(".csproj") for p in parent_names):
        return "dotnet_build", "regenerable"
    if n in TRAIN_OUT_NAMES:
        return "training_output", "review"
    if n == "SCREENSHOTS":
        return "media", "review"
    if n in ("Builds", "Build", "Packaged", "dist") :
        return "builds", "review"
    return None


def disk_info(repo: Path) -> dict:
    cats: dict[str, int] = {}
    heavy: list[dict] = []
    other = scan_errors = 0
    stack = [repo]
    while stack:
        cur = stack.pop()
        try:
            entries = list(os.scandir(cur))
        except OSError:
            scan_errors += 1
            continue
        names = {e.name for e in entries}
        for e in entries:
            try:
                if e.is_dir(follow_symlinks=False):
                    p = Path(e.path)
                    c = classify_dir(p, names)
                    if c:
                        size, nfiles, errors = tree_size(p)
                        scan_errors += errors
                        cats[c[0]] = cats.get(c[0], 0) + size
                        if size > 20 * 1024 * 1024 or c[0] in ("training_output",):
                            heavy.append({"rel": p.relative_to(ROOT).as_posix(), "category": c[0],
                                          "safety": c[1], "bytes": size, "files": nfiles})
                    else:
                        stack.append(p)
                elif e.is_file(follow_symlinks=False):
                    other += e.stat(follow_symlinks=False).st_size
            except OSError:
                scan_errors += 1
    cats["source_other"] = other
    heavy.sort(key=lambda h: -h["bytes"])
    return {"total_bytes": sum(cats.values()), "categories": cats, "heavy_dirs": heavy[:25],
            "scan_errors": scan_errors, "complete": scan_errors == 0}


# --------------------------------------------------------------------------- files
def walk_files(repo: Path, exts: set[str], max_depth: int = 8):
    base_depth = len(repo.parts)
    for dirpath, dirnames, filenames in os.walk(repo):
        dp = Path(dirpath)
        if len(dp.parts) - base_depth >= max_depth:
            dirnames[:] = []
        dirnames[:] = [d for d in dirnames if d not in WALK_SKIP_FOR_FILES and not d.startswith(".")]
        for f in filenames:
            if Path(f).suffix.lower() in exts:
                yield dp / f


def onnx_io(path: Path):
    try:
        import onnx  # type: ignore
        m = onnx.load(str(path), load_external_data=False)
        init = {i.name for i in m.graph.initializer}
        def shp(v):
            dims = []
            for d in v.type.tensor_type.shape.dim:
                dims.append(d.dim_value if d.dim_value else (d.dim_param or "?"))
            return [v.name, dims]
        ins = [shp(i) for i in m.graph.input if i.name not in init]
        outs = [shp(o) for o in m.graph.output]
        opset = max((o.version for o in m.opset_import), default=None)
        return ins, outs, opset
    except Exception as ex:  # noqa: BLE001
        return None, None, f"unreadable: {type(ex).__name__}"


def onnx_inventory(repo: Path) -> list[dict]:
    out = []
    for p in walk_files(repo, {".onnx"}):
        try:
            st = p.stat()
        except OSError:
            continue
        rel = p.relative_to(ROOT).as_posix()
        parts_l = [x.lower() for x in p.relative_to(repo).parts]
        is_shipped = not any(x in TRAIN_OUT_NAMES or x in ("archive", "dist") for x in parts_l)
        h = hashlib.sha1()
        try:
            with open(p, "rb") as fh:
                for chunk in iter(lambda: fh.read(1 << 20), b""):
                    h.update(chunk)
        except OSError:
            pass
        ins, outs, opset = onnx_io(p)
        obs = act = None
        if ins:
            d = ins[0][1]
            obs = d[-1] if d and isinstance(d[-1], int) else None
        if outs:
            for o in outs:
                d = o[1]
                if d and isinstance(d[-1], int):
                    act = d[-1]
                    break
        out.append({"rel": rel, "name": p.name, "kb": round(st.st_size / 1024, 1), "mtime": int(st.st_mtime),
                    "sha1": h.hexdigest()[:12], "shipped": is_shipped, "inputs": ins, "outputs": outs,
                    "opset": opset, "obs": obs, "act": act})
    out.sort(key=lambda o: -o["mtime"])
    return out


def mjcf_inventory(repo: Path) -> list[dict]:
    """Every MuJoCo MJCF model (xml whose root is <mujoco>): joints, actuators, timestep."""
    out = []
    for p in walk_files(repo, {".xml"}, max_depth=7):
        try:
            if p.stat().st_size > 3_000_000:
                continue
            head = p.read_text(encoding="utf-8", errors="ignore")
        except OSError:
            continue
        if "<mujoco" not in head[:4000]:
            continue
        try:
            import xml.etree.ElementTree as ET
            xr = ET.fromstring(head)
        except Exception:
            continue
        wb = xr.find("worldbody")
        alljoints = list(wb.iter("joint")) if wb is not None else []
        free = sum(1 for x in alljoints if x.get("type") == "free") + (len(list(wb.iter("freejoint"))) if wb is not None else 0)
        joints = [x for x in alljoints if x.get("type") != "free"]
        act_el = xr.find("actuator")
        acts = len(list(act_el)) if act_el is not None else 0
        bodies = len(list(wb.iter("body"))) if wb is not None else 0
        ts = re.search(r'timestep="([^"]+)"', head)
        model = re.search(r'<mujoco[^>]*model="([^"]+)"', head)
        out.append({"rel": p.relative_to(ROOT).as_posix(), "name": p.name,
                    "model": model.group(1) if model else p.stem, "bodies": bodies,
                    "joints": len(joints), "free_joints": free, "actuators": acts,
                    "timestep": ts.group(1) if ts else "", "includes": len(xr.findall(".//include")),
                    "mtime": int(p.stat().st_mtime)})
    out.sort(key=lambda m: m["rel"])
    return out


def media_info(repo: Path) -> dict:
    shots = repo / "SCREENSHOTS"
    items = []
    if shots.exists():
        for p in walk_files(shots, MEDIA_EXT, max_depth=5):
            try:
                st = p.stat()
            except OSError:
                continue
            items.append({"rel": p.relative_to(ROOT).as_posix(), "ext": p.suffix.lower(),
                          "kb": round(st.st_size / 1024, 1), "mtime": int(st.st_mtime)})
    items.sort(key=lambda m: -m["mtime"])
    gifs = [m for m in items if m["ext"] == ".gif"]
    stills = [m for m in items if m["ext"] != ".gif"]
    # Prefer big, recent stills (tiny diag thumbnails are less showy)
    stills_show = sorted(stills[:40], key=lambda m: (-(m["kb"] > 60), -m["mtime"]))
    return {"count": len(items), "gif_count": len(gifs), "newest": items[:1],
            "showcase": (gifs[:6] + stills_show[:6])}


def docs_info(repo: Path) -> dict:
    found = {n: (repo / n).exists() for n in ("README.md", "CLAUDE.md", "AGENTS.md", "tasks.md")}
    tests = []
    for cand in ("tests", "Tests", "test", "Assets/Tests", "Source/Tests", "sim/tests", "Training/tests",
                 "training/tests", "RobotControl/tests", "Assets/_Project/Core/Tests"):
        if (repo / cand).exists():
            tests.append(cand)
    tests += [p.relative_to(repo).as_posix() for p in list(repo.glob("*Tests*/*.csproj"))[:4]]
    tests += [p.relative_to(repo).as_posix() for p in list(repo.glob("tests/*Tests*/*.csproj"))[:4]]
    benchmark = (repo / "benchmark.ps1").exists()
    live_url = ""
    try:
        readme = (repo / "README.md").read_text(encoding="utf-8", errors="ignore")
        m = re.search(r"https://[A-Za-z0-9.-]+\.(?:azurewebsites\.net|azurestaticapps\.net|github\.io)(?:/[A-Za-z0-9._~-]*)?", readme)
        if m:
            live_url = re.sub(r"/(diag|signin-github)?$", "", m.group(0).rstrip(".,`)/"))
    except OSError:
        pass
    bench_hist = repo / "benchmark" / "history.json"
    seen, uniq = set(), []
    for t in tests:
        if t.lower() not in seen:
            seen.add(t.lower()); uniq.append(t)
    return {"files": found, "tests": uniq, "live_url": live_url, "benchmark_ps1": benchmark,
            "benchmark_history": bench_hist.exists()}


# --------------------------------------------------------------------------- main
def main() -> int:
    use_gh = "--no-gh" not in sys.argv
    do_sizes = "--no-sizes" not in sys.argv
    DATA.mkdir(parents=True, exist_ok=True)
    (HIST / "snapshots").mkdir(parents=True, exist_ok=True)

    prev = None
    if (DATA / "fleet.json").exists():
        try:
            prev = json.loads((DATA / "fleet.json").read_text(encoding="utf-8"))
        except Exception:
            prev = None

    repos = {}
    for d in sorted(ROOT.iterdir()):
        if not d.is_dir() or d.name.lower() in IGNORE_TOP or d.name.startswith((".", "_")):
            continue
        is_git = (d / ".git").exists()
        if not is_git and d.name not in RL_REPOS:
            # e.g. quarantine / scratch folders: record size only
            rec = {"name": d.name, "kind": "folder", "is_rl": False, "git": {"is_git": False}}
            if do_sizes:
                rec["disk"] = disk_info(d)
            repos[d.name] = rec
            print(f"  {d.name:28s} (not a git repo)")
            continue
        t0 = dt.datetime.now()
        kind = detect_kind(d)
        gi = git_info(d)
        rec = {
            "name": d.name, **kind, "is_rl": d.name in RL_REPOS, "rl_stack": RL_STACK.get(d.name, ""),
            "git": gi, "ci": ci_info(d, gi.get("slug", ""), use_gh),
            "docs": docs_info(d), "media": media_info(d),
        }
        if d.name in RL_REPOS:
            rec["onnx"] = onnx_inventory(d)
            rec["mjcf"] = mjcf_inventory(d)
        if do_sizes:
            rec["disk"] = disk_info(d)
        elif prev and d.name in prev.get("repos", {}) and "disk" in prev["repos"][d.name]:
            rec["disk"] = prev["repos"][d.name]["disk"]
            rec["disk"]["stale"] = True
        repos[d.name] = rec
        secs = (dt.datetime.now() - t0).total_seconds()
        gb = rec.get("disk", {}).get("total_bytes", 0) / 1e9
        print(f"  {d.name:28s} {rec['kind']:7s} dirty={gi.get('dirty_count', '-'):<3} "
              f"onnx={len(rec.get('onnx', [])):<4} {gb:6.1f} GB  ({secs:.0f}s)")

    stamp = NOW.strftime("%Y%m%d_%H%M%S")
    fleet = {"generated": NOW.isoformat(timespec="seconds"), "stamp": stamp, "root": str(ROOT),
             "used_gh": use_gh, "repos": repos}
    (DATA / "fleet.json").write_text(json.dumps(fleet, indent=1), encoding="utf-8")
    (HIST / "snapshots" / f"fleet_{stamp}.json").write_text(json.dumps(fleet), encoding="utf-8")

    compact = {"when": fleet["generated"], "stamp": stamp, "repos": {}}
    for n, r in repos.items():
        g = r.get("git", {})
        compact["repos"][n] = {
            "bytes": r.get("disk", {}).get("total_bytes"),
            "cats": r.get("disk", {}).get("categories"),
            "disk_complete": r.get("disk", {}).get("complete"),
            "disk_scan_errors": r.get("disk", {}).get("scan_errors"),
            "disk_stale": r.get("disk", {}).get("stale", False),
            "dirty": g.get("dirty_count"), "commits_total": g.get("commits_total"),
            "last_hash": g.get("last_commit_hash"),
            "onnx": len(r.get("onnx", [])), "onnx_shas": sorted({o["sha1"] for o in r.get("onnx", [])}),
            "media": r.get("media", {}).get("count"),
            "ci": (r.get("ci", {}).get("latest_run") or {}).get("conclusion"),
        }
    with open(HIST / "station_timeline.jsonl", "a", encoding="utf-8") as fh:
        fh.write(json.dumps(compact) + "\n")
    print(f"\nWrote {DATA / 'fleet.json'} and snapshot fleet_{stamp}.json")
    return 0


if __name__ == "__main__":
    sys.exit(main())
