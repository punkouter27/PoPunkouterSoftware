"""
Copy one .onnx brain from the vault into another repo - safely.

  python scripts/global/vault_import.py <brain path relative to the workspace> <TargetRepo> [--dest <dir>] [--dry-run]

* Never overwrites: refuses if the destination file already exists.
* Default destination: <TargetRepo>/vault_imports/<SourceRepo>/<file>.onnx
* Checks the brain's socket (obs size -> action size) against the target repo's own
  brains (from App_Data/global/fleet.json) and prints FITS / ADAPTER NEEDED.
* Writes <file>.vault.json next to the copy: source path, sha1, socket, date.
The copy leaves the target repo with an uncommitted file - review and commit it there.

This is the one GLOBAL tool that writes into another repo. It only adds a file under
vault_imports/ (or --dest), never overwrites, and never commits.
"""
from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import shutil
import sys
from pathlib import Path

import station_common as sc

ROOT = sc.WORKSPACE
FLEET = sc.OUT / "fleet.json"


def sha1(p: Path) -> str:
    h = hashlib.sha1()
    with open(p, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("brain", help="path relative to _CODEMATT, e.g. PoOlympic/Assets/.../sprint.onnx")
    ap.add_argument("target", help="target repo folder name, e.g. PoHillKing")
    ap.add_argument("--dest", help="destination folder inside the target repo (default vault_imports/<SourceRepo>)")
    ap.add_argument("--dry-run", action="store_true")
    a = ap.parse_args()

    src = (ROOT / a.brain.replace("\\", "/")).resolve()
    if not src.is_file() or src.suffix.lower() != ".onnx" or ROOT not in src.parents:
        print(f"ERROR: {a.brain} is not an .onnx file under {ROOT}")
        return 2
    src_repo = src.relative_to(ROOT).parts[0]
    tgt = (ROOT / a.target).resolve()
    if not (tgt / ".git").exists() or tgt.parent != ROOT:
        print(f"ERROR: {a.target} is not a repo folder under {ROOT}")
        return 2
    dest_dir = (tgt / (a.dest or f"vault_imports/{src_repo}")).resolve()
    if tgt not in dest_dir.parents and dest_dir != tgt:
        print("ERROR: --dest must stay inside the target repo")
        return 2
    dest = dest_dir / src.name
    if dest.exists():
        print(f"ERROR: {dest} already exists - refusing to overwrite. Pick another --dest.")
        return 3

    fleet = json.loads(FLEET.read_text(encoding="utf-8")) if FLEET.exists() else {"repos": {}}
    rel = src.relative_to(ROOT).as_posix()
    info = next((o for o in fleet["repos"].get(src_repo, {}).get("onnx", []) if o["rel"] == rel), None)
    socket = f'{info["obs"]}->{info["act"]}' if info and info.get("obs") else "unknown (refresh the vault)"
    tgt_brains = fleet["repos"].get(a.target, {}).get("onnx", [])
    tgt_sockets = sorted({f'{o["obs"]}->{o["act"]}' for o in tgt_brains if o.get("obs")})

    print(f"Brain   : {rel}")
    print(f"Socket  : {socket}   (obs -> action)")
    print(f"Target  : {a.target}  sockets in use: {', '.join(tgt_sockets) or 'none (no brains yet)'}")
    if socket in tgt_sockets:
        print("Verdict : FITS - the target already runs brains with this exact socket.")
    elif tgt_sockets:
        print("Verdict : ADAPTER NEEDED - the target's controllers expect a different obs/action size.\n"
              "          Map joints / senses, or retrain in the target using this brain as a teacher.")
    else:
        print("Verdict : target has no brains to compare with.")
    print(f"Copy to : {dest.relative_to(ROOT).as_posix()}")
    if a.dry_run:
        print("Dry run - nothing copied.")
        return 0
    dest_dir.mkdir(parents=True, exist_ok=True)
    shutil.copy2(src, dest)
    meta = {"source": rel, "source_repo": src_repo, "sha1": sha1(src), "socket": socket,
            "imported": dt.datetime.now().astimezone().isoformat(timespec="seconds"),
            "inputs": (info or {}).get("inputs"), "outputs": (info or {}).get("outputs")}
    dest.with_suffix(".vault.json").write_text(json.dumps(meta, indent=2), encoding="utf-8")
    print("Copied. Review and commit it in the target repo.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
