"""Offline checks for gcp_queue.py: the parts that decide what leaves this PC and what
runs on the VM. Nothing here talks to Google Cloud.

    python -m unittest discover -s scripts/gcp -p "test_*.py"
"""

from __future__ import annotations

import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

import gcp_queue  # noqa: E402  (path set above so the module is importable from the scripts folder)


class CatalogTest(unittest.TestCase):
    def setUp(self):
        self.cfg = json.loads(gcp_queue.JOBS_FILE.read_text(encoding="utf-8"))

    def test_every_job_names_a_real_sibling_repo(self):
        for job in self.cfg["jobs"]:
            with self.subTest(job=job["id"]):
                self.assertTrue((gcp_queue.ROOT / job["source"]).is_dir(), job["source"])
                self.assertTrue((gcp_queue.ROOT / job["source"] / job["cwd"]).is_dir(), job["cwd"])
                self.assertNotIn("\\", job["cwd"], "cwd is used on Linux")
                self.assertGreater(job["hours"], 0)
                for rel in job["artifacts"]:
                    self.assertNotIn("\\", rel)

    def test_paths_match_disk_casing(self):
        """Windows hides `training` vs `Training`; Linux does not, and cwd is used there."""

        def exact(path, base):
            current = base
            for part in pathlib.PurePosixPath(path).parts:
                names = os.listdir(current) if current.is_dir() else []
                if part in names:
                    current = current / part
                    continue
                if any(name.lower() == part.lower() for name in names):
                    raise AssertionError(f"{path}: {part!r} is the wrong case for {current}")
                return  # not created yet: the job makes it on the VM

        for job in self.cfg["jobs"]:
            with self.subTest(job=job["id"]):
                exact(job["cwd"], gcp_queue.ROOT / job["source"])
                for rel in job["artifacts"]:
                    exact(rel, gcp_queue.ROOT / job["source"])

    def test_commands_are_not_the_windows_ones(self):
        for job in self.cfg["jobs"]:
            with self.subTest(job=job["id"]):
                self.assertNotIn("Scripts/python.exe", job["command"])
                self.assertNotIn("powershell", job["command"].lower())


class ExcludeTest(unittest.TestCase):
    def setUp(self):
        self.cfg = json.loads(gcp_queue.JOBS_FILE.read_text(encoding="utf-8"))
        self.job = next(j for j in self.cfg["jobs"] if j["id"] == "box_queue")

    def test_plain_names_are_pruned_at_any_depth(self):
        patterns = gcp_queue.exclude_patterns(self.cfg, self.job)
        self.assertIn("PoBox/Library", patterns)
        self.assertIn("*/Library", patterns)

    def test_globs_stay_globs(self):
        patterns = gcp_queue.exclude_patterns(self.cfg, self.job)
        self.assertIn("*.mp4", patterns)
        self.assertIn("PoBox/training/checkpoints/v4*", patterns)

    def test_patterns_are_deduplicated(self):
        patterns = gcp_queue.exclude_patterns(self.cfg, self.job)
        self.assertEqual(len(patterns), len(set(patterns)))


@unittest.skipUnless(shutil.which("tar"), "tar is not on PATH")
class TarTest(unittest.TestCase):
    """Windows bsdtar must really drop the heavy folders, not just list them as excludes."""

    def setUp(self):
        self.tmp = pathlib.Path(tempfile.mkdtemp(prefix="po-gcp-tar-"))
        for rel in (
            "P/Library/unity.bin",
            "P/sub/Library/nested.bin",
            "P/.git/config",
            "P/sub/.git/config",
            "P/build/clip.mp4",
            "P/Training/checkpoints/v4r_x/latest.pt",
            "P/Training/checkpoints/shipped/match.pt",
            "P/Training/queue.json",
        ):
            path = self.tmp / rel
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("x", encoding="utf-8")

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def test_tar_drops_excluded_subtrees(self):
        job = {
            "source": "P",
            "excludes": ["Library", "Training/checkpoints/v4*"],
        }
        cfg = {"common_excludes": [".git", "*.mp4"]}
        tarball = self.tmp / "out.tar.gz"
        argv = gcp_queue.tar_argv(job, tarball, gcp_queue.exclude_patterns(cfg, job))
        argv[argv.index("-C") + 1] = str(self.tmp)
        subprocess.run(argv, check=True)
        listing = subprocess.run(["tar", "-tzf", str(tarball)], check=True, capture_output=True, text=True).stdout
        self.assertIn("P/Training/checkpoints/shipped/match.pt", listing)
        self.assertIn("P/Training/queue.json", listing)
        for dropped in ("Library/unity.bin", "nested.bin", ".git/config", "clip.mp4", "latest.pt"):
            self.assertNotIn(dropped, listing, f"{dropped} should not ship")


class EnvFileTest(unittest.TestCase):
    def test_job_env_replaces_hours_and_quotes(self):
        cfg = json.loads(gcp_queue.JOBS_FILE.read_text(encoding="utf-8"))
        settings = gcp_queue.Settings(_args(), cfg)
        job = settings.job("bball_queue")
        env = gcp_queue.job_env(settings, job, 6, "bball_queue_test", self_stop=True)
        self.assertEqual(env["COMMAND"], "uv run python -m streetball.queue --hours 6")
        self.assertEqual(env["SELF_STOP"], "1")
        self.assertEqual(env["TIMEOUT_SECONDS"], str(6 * 3600 + 30 * 60))
        text = gcp_queue.env_file_text({"SETUP": "uv sync --group train"})
        self.assertEqual(text, "SETUP='uv sync --group train'\n")
        self.assertEqual(gcp_queue.env_file_text({"X": "it's"}), "X='it'\\''s'\n")


def _args(**overrides):
    base = {
        "project": "demo-project",
        "zone": "us-east4-a",
        "instance": None,
        "machine_type": None,
        "bucket": None,
        "sync_seconds": None,
        "dry_run": True,
    }
    base.update(overrides)
    return type("Args", (), base)()


class DryRunTest(unittest.TestCase):
    """--dry-run has to work with no login at all."""

    def test_dry_run_prints_and_touches_nothing(self):
        gcp_queue.DRY_RUN = True
        try:
            self.assertEqual(
                gcp_queue.main(["--project", "demo-project", "--dry-run", "run", "--job", "bball_queue"]),
                0,
            )
        finally:
            gcp_queue.DRY_RUN = False


if __name__ == "__main__":
    unittest.main()
