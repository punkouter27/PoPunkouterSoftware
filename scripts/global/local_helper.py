"""Local helper for the /global pages: http://127.0.0.1:5050

The site runs in Azure and cannot reach this PC. This small server (standard library only)
runs on the PC and lets the signed-in pages do the things that have to happen here: open a
repo in VS Code, start the engine, run the overnight GPU queue, send a job to the cloud GPU,
refresh the snapshot, run the benchmarks, copy a brain between repos.

It replaces GLOBAL/station/automation/station_server.py. It serves no files.

Safety:
  * binds 127.0.0.1 only and rejects any request whose Host is not 127.0.0.1/localhost:<port>
    (so a DNS-rebinding page cannot reach it);
  * a browser request must come from an allowed Origin (the live site or the local dev site);
  * every action needs the token in X-Po-Helper-Token. The token is made on first start,
    kept in scripts/global/.local/helper_token.txt (git-ignored) and pasted into the site once;
  * actions are a fixed list; repo names must be git folders in the workspace; job ids must be
    in the catalogs, and the command always comes from the catalog, never from the page;
  * nothing here deletes files.

Run:  scripts\\global\\start_local_helper.bat   (or python local_helper.py [--port 5050])
"""

from __future__ import annotations

import argparse
import hmac
import json
import os
import re
import secrets
import subprocess
import sys
import time
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlparse

import station_common as sc

HERE = sc.HERE
REPO = sc.REPO
WORKSPACE = sc.WORKSPACE
LOCAL = HERE / ".local"                 # git-ignored: token, logs, saved queues, queue runs
LOGS = LOCAL / "logs"
QUEUES = LOCAL / "queues"
RUNS = LOCAL / "runs"
TOKEN_FILE = LOCAL / "helper_token.txt"
TRAIN_JOBS = sc.CONFIG / "train_jobs.json"
CLOUD_JOBS = REPO / "scripts" / "gcp" / "cloud_jobs.json"
GCP_QUEUE = REPO / "scripts" / "gcp" / "gcp_queue.py"

DEFAULT_ORIGINS = (
    "https://app-popunkoutersoftware2.azurewebsites.net",
    "http://localhost:8000",
    "http://127.0.0.1:8000",
)
TOKEN_HEADER = "X-Po-Helper-Token"
CLOUD_READ = ("doctor", "status")       # output is captured and returned
CLOUD_CONSOLE = ("up", "down")          # run in their own window
JOBS: dict[str, dict] = {}
PORT = 5050
ORIGINS: set[str] = set(DEFAULT_ORIGINS)
TOKEN = ""

NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)
NEW_CONSOLE = getattr(subprocess, "CREATE_NEW_CONSOLE", 0)


def load_token() -> str:
    LOCAL.mkdir(parents=True, exist_ok=True)
    if TOKEN_FILE.exists():
        token = TOKEN_FILE.read_text(encoding="utf-8").strip()
        if len(token) >= 24:
            return token
    token = secrets.token_urlsafe(24)
    TOKEN_FILE.write_text(token + "\n", encoding="utf-8")
    return token


def repo_names() -> set[str]:
    return {p.name for p in WORKSPACE.iterdir() if p.is_dir() and (p / ".git").exists()}


def find_unity_project(repo: Path) -> Path | None:
    for cand in [repo, *[p for p in repo.iterdir() if p.is_dir()]]:
        if (cand / "ProjectSettings" / "ProjectVersion.txt").exists():
            return cand
    return None


def start_job(name: str, cmd: list[str], cwd: Path) -> str:
    LOGS.mkdir(parents=True, exist_ok=True)
    jid = f"{name}_{time.strftime('%Y%m%d_%H%M%S')}"
    log = LOGS / f"{jid}.log"
    fh = open(log, "w", encoding="utf-8", errors="replace")  # noqa: SIM115 - closed in job_status
    proc = subprocess.Popen(cmd, cwd=cwd, stdout=fh, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL,
                            creationflags=NO_WINDOW)
    JOBS[jid] = {"proc": proc, "log": log, "fh": fh}
    return jid


def run_capture(cmd: list[str], cwd: Path | None = None, timeout: int = 30) -> tuple[bool, str]:
    try:
        r = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True, timeout=timeout,
                           encoding="utf-8", errors="replace", stdin=subprocess.DEVNULL)
        return r.returncode == 0, (r.stdout + r.stderr).strip()
    except Exception as ex:  # noqa: BLE001
        return False, str(ex)


def powershell_console(script: Path, *args: str) -> None:
    """Run a script in its own PowerShell window that stays open, so the owner can watch it."""
    subprocess.Popen(["powershell", "-NoExit", "-ExecutionPolicy", "Bypass", "-File", str(script), *args],
                     cwd=WORKSPACE, creationflags=NEW_CONSOLE)


def clamp_hours(value, default: float) -> float:
    try:
        return max(0.1, min(24.0, float(value)))
    except (TypeError, ValueError):
        return max(0.1, min(24.0, float(default)))


def safe_name(value, fallback: str) -> str:
    return re.sub(r"[^A-Za-z0-9_-]", "_", str(value or fallback))[:40] or fallback


def build_queue(body: dict) -> tuple[dict | None, str]:
    """Turn the page's selection into a queue file. Commands come from the catalog only."""
    picked = body.get("jobs")
    if not isinstance(picked, list) or not picked:
        return None, "pick at least one job"
    known = {j["id"]: j for j in sc.load_json(TRAIN_JOBS, {}).get("jobs", [])}
    clean = []
    for item in picked:
        if not isinstance(item, dict) or item.get("id") not in known:
            return None, f"job {item.get('id') if isinstance(item, dict) else item!r} is not in config/train_jobs.json"
        job = known[item["id"]]
        entry = {key: job.get(key) for key in
                 ("id", "repo", "cwd", "command", "self_limiting", "stop_file", "device", "requires")}
        entry["hours"] = clamp_hours(item.get("hours"), job.get("default_hours", 1))
        clean.append(entry)
    queue = {
        "name": safe_name(body.get("name"), "tonight"),
        "created": time.strftime("%Y-%m-%dT%H:%M:%S"),
        "cool_temp": int(body.get("coolTemp") or 70),
        "cool_max_wait_min": int(body.get("coolMaxWaitMin") or 20),
        "gap_min": int(body.get("gapMin") if body.get("gapMin") is not None else 5),
        "refresh_after": bool(body.get("refreshAfter", True)),
        "jobs": clean,
    }
    return queue, ""


STAGES = ("prototype", "playable", "polishing", "ready", "shipped")
CHECKS = ("core_loop", "controls", "ui_polish", "audio_fx", "perf", "no_critical_bugs")
CONFIG_FILES = {"readiness": "readiness.json", "vault_gold": "vault_gold.json"}


def lh_config_path(name: str) -> Path:
    return sc.CONFIG / CONFIG_FILES[name]


def clean_config(name: str, data) -> tuple[dict | None, str]:
    """Validate a page's edit of readiness.json or vault_gold.json; keep only known keys and sizes."""
    if name not in CONFIG_FILES:
        return None, f"unknown config {name!r}"
    if not isinstance(data, dict):
        return None, "config data must be an object"
    if name == "readiness":
        apps = data.get("apps")
        if not isinstance(apps, dict):
            return None, "readiness needs an 'apps' object"
        known = repo_names()
        clean = {}
        for repo, entry in apps.items():
            if repo not in known or not isinstance(entry, dict):
                return None, f"unknown repo {repo!r}"
            stage = entry.get("stage")
            if stage is not None and stage not in STAGES:
                return None, f"unknown stage {stage!r}"
            checklist = entry.get("checklist") or {}
            if not isinstance(checklist, dict):
                return None, "checklist must be an object"
            item = {"checklist": {k: v for k, v in checklist.items() if k in CHECKS and isinstance(v, bool)}}
            if stage:
                item["stage"] = stage
            notes = entry.get("notes")
            if isinstance(notes, str) and notes.strip():
                item["notes"] = notes.strip()[:2000]
            if item.get("stage") or item["checklist"] or item.get("notes"):
                clean[repo] = item
        return {"apps": clean}, ""
    gold = data.get("gold")
    if not isinstance(gold, dict):
        return None, "vault_gold needs a 'gold' object"
    known = repo_names()
    clean = {}
    for rel, note in gold.items():
        if not isinstance(rel, str) or len(rel) > 400 or ".." in rel or rel.split("/")[0] not in known \
                or not rel.lower().endswith(".onnx"):
            return None, f"not a brain path in a workspace repo: {rel!r}"
        clean[rel] = str(note or "")[:200]
    return {"gold": clean}, ""


def recent_runs(limit: int = 5) -> str:
    if not RUNS.is_dir():
        return "No queue has run from this PC yet."
    lines = []
    for run in sorted((p for p in RUNS.iterdir() if p.is_dir()), reverse=True)[:limit]:
        summary = sc.load_json(run / "summary.json", None)
        if not summary:
            lines.append(f"{run.name}: no summary yet (still running, or stopped early)")
            continue
        lines.append(f"{run.name}  ({summary.get('queue')})")
        for job in summary.get("jobs", []):
            lines.append(f"  {job.get('id')}: {job.get('outcome')} after {job.get('minutes')} min, "
                         f"{len(job.get('new_onnx') or [])} new brains")
    return "\n".join(lines) if lines else "No queue has run from this PC yet."


def do_action(body: dict) -> dict:
    act = body.get("action")
    repo = body.get("repo") or ""
    if repo and repo not in repo_names():
        return {"ok": False, "output": f"unknown repo {repo!r}"}
    rpath = WORKSPACE / repo if repo else None

    if act == "open_vscode" and rpath:
        subprocess.Popen(["cmd", "/c", "code", str(rpath)], creationflags=NO_WINDOW)
        return {"ok": True, "output": f"Opening {rpath} in VS Code"}

    if act == "open_folder" and rpath:
        os.startfile(str(rpath))  # type: ignore[attr-defined]
        return {"ok": True, "output": f"Opened {rpath}"}

    if act == "git_status" and rpath:
        ok, out = run_capture(["git", "-C", str(rpath), "status", "-sb"])
        _, log = run_capture(["git", "-C", str(rpath), "log", "-8", "--format=%h %cr  %s"])
        return {"ok": ok, "output": out + "\n\nRecent commits:\n" + log}

    if act == "open_engine" and rpath:
        projects = [h for pat in ("*.uproject", "*/*.uproject", "*/*/*.uproject")
                    for h in rpath.glob(pat) if "Intermediate" not in h.parts]
        if projects:
            os.startfile(str(projects[0]))  # type: ignore[attr-defined]
            return {"ok": True, "output": f"Opening {projects[0].name} with the Unreal launcher association"}
        proj = find_unity_project(rpath)
        if proj:
            text = (proj / "ProjectSettings" / "ProjectVersion.txt").read_text(errors="ignore")
            ver = re.search(r"m_EditorVersion:\s*(\S+)", text)
            exe = Path(rf"C:\Program Files\Unity\Hub\Editor\{ver.group(1) if ver else ''}\Editor\Unity.exe")
            if exe.exists():
                subprocess.Popen([str(exe), "-projectPath", str(proj)])
                return {"ok": True, "output": f"Starting Unity {ver.group(1)} on {proj}"}
            return {"ok": False, "output": f"Unity editor not found at {exe}"}
        return {"ok": False, "output": "No .uproject or Unity project found"}

    if act == "refresh":
        args = [] if body.get("push") else ["-NoPush"]
        if body.get("push") and body.get("noCrawl"):
            args = ["-NoCrawl"]
        jid = start_job("refresh", ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                                    str(HERE / "refresh_and_push.ps1"), *args], WORKSPACE)
        what = "crawl, commit and push (the site redeploys in about 5 minutes)" if body.get("push") else "crawl only"
        return {"ok": True, "output": f"Started the refresh: {what}.", "job": jid}

    if act == "save_config":
        name = str(body.get("arg") or "")
        data = body.get("data")
        cleaned, problem = clean_config(name, data)
        if cleaned is None:
            return {"ok": False, "output": problem}
        target = lh_config_path(name)
        target.write_text(json.dumps(cleaned, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        saved = f"Saved App_Data/global/config/{target.name}. Push it to show it on the live site."
        return {"ok": True, "output": saved}

    if act == "job_status":
        job = JOBS.get(str(body.get("job")))
        if not job:
            return {"ok": False, "output": "unknown job", "done": True}
        done = job["proc"].poll() is not None
        if done:
            job["fh"].close()
        text = job["log"].read_text(encoding="utf-8", errors="replace")[-6000:]
        if done:
            text += f"\n[exit {job['proc'].returncode}]"
        return {"ok": True, "output": text, "done": done}

    if act in ("save_queue", "launch_queue"):
        queue, problem = build_queue(body)
        if queue is None:
            return {"ok": False, "output": problem}
        QUEUES.mkdir(parents=True, exist_ok=True)
        qfile = QUEUES / f"{queue['name']}.json"
        qfile.write_text(json.dumps(queue, indent=2), encoding="utf-8")
        hours = sum(j["hours"] for j in queue["jobs"])
        if act == "save_queue":
            return {"ok": True, "output": f"Saved {qfile.name}: {len(queue['jobs'])} job(s), {hours:g} h"}
        args = ["-QueueFile", str(qfile)] + (["-DryRun"] if body.get("dry") else [])
        powershell_console(HERE / "run_gpu_queue.ps1", *args)
        kind = "Dry run" if body.get("dry") else "Queue"
        return {"ok": True, "output": f"{kind} started in a new PowerShell window: {len(queue['jobs'])} job(s), "
                                      f"{hours:g} h.\nReport: scripts/global/.local/runs/<stamp>/summary.md"}

    if act == "queue_runs":
        return {"ok": True, "output": recent_runs()}

    if act == "benchmark":
        wanted = [r for r in (body.get("repos") or []) if isinstance(r, str)]
        unknown = [r for r in wanted if r not in repo_names()]
        if unknown:
            return {"ok": False, "output": f"unknown repo(s): {', '.join(unknown)}"}
        args = (["-Repos", ",".join(wanted)] if wanted else []) + (["-NoRefresh"] if body.get("dry") else [])
        powershell_console(HERE / "run_benchmarks.ps1", *args)
        return {"ok": True, "output": f"Benchmark started in a new PowerShell window for "
                                      f"{', '.join(wanted) if wanted else 'every RL repo'}."}

    if act == "vault_import":
        brain, target = str(body.get("brain") or ""), str(body.get("target") or "")
        if not brain or target not in repo_names():
            return {"ok": False, "output": "give a brain path and a target repo"}
        cmd = [sys.executable, str(HERE / "vault_import.py"), brain, target]
        if body.get("dest"):
            cmd += ["--dest", str(body["dest"])]
        if body.get("dry"):
            cmd.append("--dry-run")
        ok, out = run_capture(cmd, cwd=HERE, timeout=120)
        return {"ok": ok, "output": out}

    if act == "cloud":
        command = str(body.get("command") or "")
        base = [sys.executable, str(GCP_QUEUE)] + (["--dry-run"] if body.get("dry") else [])
        if command in CLOUD_READ:
            ok, out = run_capture([*base, command], cwd=REPO, timeout=180)
            return {"ok": ok, "output": out}
        if command in CLOUD_CONSOLE:
            subprocess.Popen(["cmd", "/k", *base, command], cwd=REPO, creationflags=NEW_CONSOLE)
            return {"ok": True, "output": f"gcp_queue.py {command} started in a new window."}
        if command == "run":
            known = {j["id"]: j for j in sc.load_json(CLOUD_JOBS, {}).get("jobs", [])}
            job = known.get(body.get("id"))
            if not job:
                return {"ok": False, "output": f"job {body.get('id')!r} is not in scripts/gcp/cloud_jobs.json"}
            hours = clamp_hours(body.get("hours"), job.get("hours", 1))
            run_args = [*base, "run", "--job", job["id"], "--hours", f"{hours:g}"]
            if body.get("dry"):
                ok, out = run_capture(run_args, cwd=REPO, timeout=180)
                return {"ok": ok, "output": out}
            subprocess.Popen(["cmd", "/k", *run_args], cwd=REPO, creationflags=NEW_CONSOLE)
            return {"ok": True, "output": f"Sending {job['id']} to the cloud GPU for {hours:g} h in a new window. "
                                          "This costs money while the VM runs; stop it with 'down' when finished."}
        return {"ok": False, "output": f"unknown cloud command {command!r}"}

    if act == "cloud_jobs":
        jobs = sc.load_json(CLOUD_JOBS, {}).get("jobs", [])
        lines = [f"{j.get('id')}  ({j.get('source')}, {j.get('hours', '?')} h default)" for j in jobs]
        return {"ok": True, "output": "\n".join(lines) or "No cloud jobs defined.",
                "ids": [j.get("id") for j in jobs]}

    return {"ok": False, "output": f"unknown or incomplete action {act!r}"}


class Handler(BaseHTTPRequestHandler):
    server_version = "PoLocalHelper/1"

    def log_message(self, fmt, *args):  # one short line per API call
        sys.stderr.write(f"{time.strftime('%H:%M:%S')} {fmt % args}\n")

    def _host_ok(self) -> bool:
        return self.headers.get("Host", "") in (f"127.0.0.1:{PORT}", f"localhost:{PORT}")

    def _origin(self) -> tuple[bool, str | None]:
        """(allowed, origin). No Origin header means a non-browser caller, which the token covers."""
        origin = self.headers.get("Origin")
        return (origin is None or origin in ORIGINS), origin

    def _authorized(self) -> bool:
        return hmac.compare_digest(self.headers.get(TOKEN_HEADER, ""), TOKEN)

    def _cors(self, origin: str | None) -> None:
        if origin:
            self.send_header("Access-Control-Allow-Origin", origin)
            self.send_header("Vary", "Origin")
            self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
            self.send_header("Access-Control-Allow-Headers", f"Content-Type, {TOKEN_HEADER}")
            self.send_header("Access-Control-Allow-Private-Network", "true")
            self.send_header("Access-Control-Max-Age", "600")

    def _json(self, code: int, obj: dict, origin: str | None) -> None:
        data = json.dumps(obj).encode()
        self.send_response(code)
        self._cors(origin)
        self.send_header("Content-Type", "application/json")
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def _gate(self) -> tuple[bool, str | None]:
        allowed, origin = self._origin()
        if not self._host_ok() or not allowed:
            self.send_error(HTTPStatus.FORBIDDEN)
            return False, origin
        return True, origin

    def do_OPTIONS(self):  # noqa: N802 - http.server naming
        ok, origin = self._gate()
        if not ok:
            return
        self.send_response(HTTPStatus.NO_CONTENT)
        self._cors(origin)
        self.send_header("Content-Length", "0")
        self.end_headers()

    def do_GET(self):  # noqa: N802
        ok, origin = self._gate()
        if not ok:
            return
        if urlparse(self.path).path != "/api/ping":
            return self.send_error(HTTPStatus.NOT_FOUND)
        authorized = self._authorized()
        reply = {"ok": True, "authorized": authorized}
        if authorized:
            reply["output"] = str(WORKSPACE)
        return self._json(200, reply, origin)

    def do_POST(self):  # noqa: N802
        ok, origin = self._gate()
        if not ok:
            return
        if urlparse(self.path).path != "/api/action":
            return self.send_error(HTTPStatus.NOT_FOUND)
        if not self._authorized():
            refused = {"ok": False, "authorized": False, "output": "The helper token is missing or wrong."}
            return self._json(401, refused, origin)
        length = int(self.headers.get("Content-Length") or 0)
        if length > 200_000:
            return self.send_error(HTTPStatus.REQUEST_ENTITY_TOO_LARGE)
        try:
            body = json.loads(self.rfile.read(length) or b"{}")
            if not isinstance(body, dict):
                raise ValueError("body must be an object")
        except (json.JSONDecodeError, ValueError):
            return self._json(400, {"ok": False, "authorized": True, "output": "bad JSON"}, origin)
        try:
            reply = do_action(body)
        except Exception as ex:  # noqa: BLE001
            reply = {"ok": False, "output": f"{type(ex).__name__}: {ex}"}
        reply["authorized"] = True
        return self._json(200, reply, origin)


def main() -> int:
    global PORT, TOKEN
    ap = argparse.ArgumentParser(description="Local helper for the PoPunkouterSoftware /global pages.")
    ap.add_argument("--port", type=int, default=5050)
    ap.add_argument("--origin", action="append", default=[], help="extra allowed site origin, repeatable")
    args = ap.parse_args()
    PORT = args.port
    ORIGINS.update(args.origin)
    TOKEN = load_token()
    server = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    print(f"Local helper on http://127.0.0.1:{PORT}  (Ctrl+C to stop)")
    print(f"Workspace: {WORKSPACE}")
    print(f"Token (paste it once on the site's 'This PC' page): {TOKEN}")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
