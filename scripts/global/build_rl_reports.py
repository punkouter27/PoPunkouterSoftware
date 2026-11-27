"""Build the two RL reports the /global/rl page shows: the RL comparison & triage overview and
RL progress over time.

They are GLOBAL's own generators (scripts/global/rl_reports, vendored unchanged apart from
paths), so the hand-written analysis — what to train next, portability matrix, character
passports, skill library, starter recipes, per-repo deep dives — comes across intact. This
script runs them and makes the output safe to serve from the site:

  * screenshots and GIFs  -> raw.githubusercontent.com (every Po repo is public)
  * links to repo files   -> github.com/<owner>/<repo>/blob/<branch>/<path>, new tab
  * links between reports -> /global/rl?view=..., and the hub / standards pages, top window
  * anything else relative (snapshot files, local folders) -> plain text

Output: App_Data/global/reports/rl-overview.html and rl-progress.html, served (sign-in only,
in a sandboxed frame) by /api/global/reports/{name}.

  python scripts/global/build_rl_reports.py
"""

from __future__ import annotations

import html
import json
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE / "rl_reports"))

import station_common as sc  # noqa: E402

REPORTS = sc.OUT / "reports"

TOP_LINKS = {
    "../../index.html": "/global",
    "rl_progress_over_time.html": "/global/rl?view=progress",
    "rl_training_overview.html": "/global/rl?view=overview",
    "../standards/README.md": "/global/standards",
}


def _github_map() -> dict[str, tuple[str, str]]:
    """repo -> (owner/name, branch) from the fleet crawl's git remotes."""
    fleet = sc.load_json(sc.OUT / "fleet.json", {"repos": {}})
    out = {}
    for name, repo in fleet.get("repos", {}).items():
        git = repo.get("git") or {}
        match = re.search(r"github\.com[:/]([^/]+/[^/.]+?)(?:\.git)?$", git.get("remote") or "")
        if match:
            out[name] = (match.group(1), git.get("branch") or "master")
    return out


def rewrite(page: str, github: dict[str, tuple[str, str]]) -> tuple[str, int]:
    """Point every relative link at GitHub or the site; returns (html, links that became plain text)."""
    dropped = 0

    def repo_url(rel: str, raw: bool) -> str | None:
        repo, _, path = rel.partition("/")
        if repo not in github or not path:
            return None
        slug, branch = github[repo]
        if raw:
            return f"https://raw.githubusercontent.com/{slug}/{branch}/{path}"
        return f"https://github.com/{slug}/blob/{branch}/{path}"

    def src(match: re.Match) -> str:
        url = repo_url(html.unescape(match.group(2)), raw=True)
        return f'{match.group(1)}="{html.escape(url) if url else ""}"'

    page = re.sub(r'(src)="\.\./\.\./\.\./([^"]+)"', src, page)

    def anchor(match: re.Match) -> str:
        nonlocal dropped
        attrs, target = match.group(1), html.unescape(match.group(2))
        if target in TOP_LINKS:
            return f'<a{attrs}href="{TOP_LINKS[target]}" target="_top"'
        if target.startswith("../../../"):
            url = repo_url(target[len("../../../"):], raw=False)
            if url:
                return f'<a{attrs}href="{html.escape(url)}" target="_blank" rel="noopener noreferrer"'
        if target.startswith(("#", "http://", "https://", "mailto:")):
            extra = ' target="_blank" rel="noopener noreferrer"' if target.startswith("http") else ""
            return f'<a{attrs}href="{html.escape(target)}"{extra}'
        dropped += 1
        return f"<a{attrs}"

    page = re.sub(r'<a(\s[^>]*?)href="([^"]*)"', anchor, page)
    return page, dropped


def build_overview() -> str:
    import build_rl_dashboard as dashboard

    return dashboard.build_html()


def build_progress() -> str:
    import record_and_build_history as history

    facts = sc.load_json(sc.OUT / "rl_facts.json", {})
    onnx_records, _ = history.scan_onnx_timeline()
    bench = history.load_benchmark_histories()
    entries = [json.loads(line) for line in history.TIMELINE_PATH.read_text(encoding="utf-8").splitlines()
               if line.strip()] if history.TIMELINE_PATH.exists() else []
    snaps = sorted(history.SNAP_DIR.glob("*.json")) if history.SNAP_DIR.is_dir() else []
    snap = sc.load_json(snaps[-1], None) if snaps else None
    if not snap or not entries:
        raise SystemExit("no RL snapshot yet: run build_global_data.py --only rl first")
    snap.setdefault("facts", facts)
    return history.build_history_html(snap, entries, onnx_records, bench)


def main() -> int:
    REPORTS.mkdir(parents=True, exist_ok=True)
    github = _github_map()
    for name, build in (("rl-overview", build_overview), ("rl-progress", build_progress)):
        page, dropped = rewrite(build(), github)
        out = REPORTS / f"{name}.html"
        out.write_text(page, encoding="utf-8")
        images = len(re.findall(r'src="https://raw\.githubusercontent\.com/', page))
        print(f"  wrote {out.relative_to(sc.REPO)} ({out.stat().st_size // 1024} KB, {images} images from GitHub, "
              f"{dropped} local-only links made plain text)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
