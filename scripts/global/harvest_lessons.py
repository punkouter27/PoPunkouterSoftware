"""
Harvest "lessons learned / gotchas / traps / rules" bullets from every repo's docs.

Sources per repo: AGENTS.md, CLAUDE.md, README.md, docs/**/*.md (depth 2),
Training|training/*.md. A heading counts when it matches LESSON_RX (trap / landmine /
known issue / lessons / things that bite / pins / patches...) or RULE_RX (house rules,
hard rules, coding standard...). Every bullet under it (until the next heading of the
same or higher level) becomes one lesson. Identical bullets in several repos are merged
("shared by N repos"). Manual extras come from config/lessons_manual.json.

Writes App_Data/global/lessons.json (served by /api/global/lessons)
"""
from __future__ import annotations

import hashlib
import json
import re
from pathlib import Path

from station_common import CONFIG, DATA, load_json
from station_common import WORKSPACE as ROOT

LESSON_RX = re.compile(r"(gotcha|trap|pitfall|lesson|learned|known (issue|quirk|gap|open)|landmine|bite|caveat|"
                       r"don'?t|do not|never|warning|contradict|disprov|what not|sim-to-sim|deviation|"
                       r"version pin|local patch|why .* (stopped|failed)|neither kept|not there yet|failed)", re.I)
RULE_RX = re.compile(r"(house rules|hard rules|method rules|agent rules|standing rules|coding standard|"
                     r"rules while working|project contracts|uu_agent|unity_rules|repo notes|^rules$)", re.I)
HEAD_RX = re.compile(r"^(#{1,6})\s+(.*)$")
BULLET_RX = re.compile(r"^(\s*)([-*+]|\d+[.)])\s+(.*)$")

TAGS = {
    "unity": r"\bunity\b|playmode|editor|prefab|\.asset\b|sentis|inferenceengine",
    "unreal": r"\bunreal\b|\bue5?\b|uproject|blueprint|chaos|\bnne\b|-nullrhi|urlab|pie\b",
    "mujoco": r"mujoco|mjcf|\bwarp\b|mjx|actuator|\bkp\b|\bkv\b|timestep|qpos|qvel",
    "onnx": r"\bonnx\b|opset|export",
    "ml-agents": r"ml-?agents|mlagents|ma-poca|self-play|\belo\b",
    "training": r"\bppo\b|reward|curriculum|checkpoint|resume|entropy|learning rate|\blr\b|normaliz|rung|gate",
    "python/env": r"\bvenv\b|\buv\b|\bpip\b|torch|cuda|python",
    "ci/deploy": r"\bci\b|workflow|github actions|deploy|azure|webgl",
    "git": r"\bgit\b|commit|branch|\bpr\b|merge|push",
    "windows": r"windows|powershell|\.ps1|msys|path|crlf|line ending",
    "testing": r"\btests?\b|pytest|xunit|assert",
    "perf": r"\bfps\b|perf|latency|slow|fast|thermal|gpu",
}
TAG_RX = {k: re.compile(v, re.I) for k, v in TAGS.items()}


def md_sources(repo: Path) -> list[Path]:
    out = []
    for n in ("AGENTS.md", "CLAUDE.md", "README.md"):
        if (repo / n).exists():
            out.append(repo / n)
    for d in ("docs", "Training", "training"):
        base = repo / d
        if base.exists():
            out += sorted(base.glob("*.md"))
            if d == "docs":
                out += sorted(base.glob("*/*.md"))[:60]
    seen, uniq = set(), []
    for p in out:
        k = str(p).lower()
        if k not in seen:
            seen.add(k)
            uniq.append(p)
    return uniq


def clean(t: str) -> str:
    t = re.sub(r"\s+", " ", t).strip()
    return t


def harvest_file(path: Path, repo: str) -> list[dict]:
    try:
        lines = path.read_text(encoding="utf-8", errors="ignore").splitlines()
    except OSError:
        return []
    items: list[dict] = []
    stack: list[tuple[int, str, str | None]] = []  # (level, heading, kind)
    active_kind = None
    active_heading = ""
    cur = None
    in_code = False

    def flush():
        nonlocal cur
        if cur and len(cur["text"]) >= 25:
            cur["text"] = clean(cur["text"])[:700]
            items.append(cur)
        cur = None

    for i, raw in enumerate(lines, 1):
        if raw.strip().startswith("```"):
            in_code = not in_code
            continue
        if in_code:
            continue
        h = HEAD_RX.match(raw)
        if h:
            flush()
            level, title = len(h.group(1)), h.group(2).strip()
            while stack and stack[-1][0] >= level:
                stack.pop()
            kind = "trap" if LESSON_RX.search(title) else "rule" if RULE_RX.search(title) else None
            stack.append((level, title, kind))
            active = next(((lv, t, k) for lv, t, k in reversed(stack) if k), None)
            active_kind = active[2] if active else None
            active_heading = " › ".join(t for _, t, _ in stack[-3:])
            continue
        if not active_kind:
            continue
        b = BULLET_RX.match(raw)
        if b and len(b.group(1)) <= 1:
            flush()
            cur = {"repo": repo, "file": path.relative_to(ROOT).as_posix(), "line": i,
                   "heading": active_heading, "kind": active_kind, "text": b.group(3)}
        elif cur and raw.strip() and (raw.startswith("  ") or BULLET_RX.match(raw)):
            cur["text"] += " " + raw.strip().lstrip("-*+ ")
        elif not raw.strip():
            flush()
    flush()
    return items


def main() -> dict:
    fleet = load_json(DATA / "fleet.json", {"repos": {}})
    merged: dict[str, dict] = {}
    for name, rec in fleet["repos"].items():
        if not rec.get("git", {}).get("is_git"):
            continue
        for src in md_sources(ROOT / name):
            for it in harvest_file(src, name):
                key = hashlib.sha1(re.sub(r"[^a-z0-9]+", "", it["text"].lower())[:240].encode()).hexdigest()[:14]
                if key in merged:
                    m = merged[key]
                    if name not in m["repos"]:
                        m["repos"].append(name)
                        m["sources"].append({"repo": name, "file": it["file"], "line": it["line"]})
                    continue
                it["id"] = key
                it["repos"] = [name]
                it["sources"] = [{"repo": name, "file": it["file"], "line": it["line"]}]
                it["tags"] = [k for k, rx in TAG_RX.items() if rx.search(it["text"] + " " + it["heading"])]
                merged[key] = it
    manual = load_json(CONFIG / "lessons_manual.json", {"lessons": []}).get("lessons", [])
    for i, m in enumerate(manual):
        key = f"manual{i}"
        repo = m.get("repo", "fleet")
        merged[key] = {
            "id": key,
            "repo": repo,
            "repos": [repo],
            "file": m.get("file", ""),
            "line": m.get("line", 0),
            "heading": m.get("heading", "Manual entry"),
            "kind": m.get("kind", "trap"),
            "text": m["text"],
            "tags": m.get("tags") or [k for k, rx in TAG_RX.items() if rx.search(m["text"])],
            "sources": [{"repo": repo, "file": m.get("file", ""), "line": m.get("line", 0)}],
            "manual": True,
        }
    lessons = sorted(merged.values(), key=lambda x: (x["kind"] != "trap", -len(x["repos"]), x["repo"]))
    out = {"count": len(lessons), "lessons": lessons}
    (DATA / "lessons.json").write_text(json.dumps(out, indent=0), encoding="utf-8")
    return out


if __name__ == "__main__":
    o = main()
    from collections import Counter
    kinds = Counter(x["kind"] for x in o["lessons"])
    repos = Counter(x["repo"] for x in o["lessons"]).most_common()
    print(o["count"], "lessons;", kinds, repos)
