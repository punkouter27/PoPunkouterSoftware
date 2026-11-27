"""Tests for local_helper.py: who may call it, and that a page can never supply a command.

Run:  python -m unittest scripts/global/test_local_helper.py   (from the repo root)
Nothing here starts a process on the PC: only refusals and the queue builder are exercised.
"""

from __future__ import annotations

import json
import sys
import threading
import unittest
import urllib.error
import urllib.request
from http.server import ThreadingHTTPServer
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import local_helper as lh  # noqa: E402

SITE = "https://app-popunkoutersoftware2.azurewebsites.net"


class QueueBuilderTests(unittest.TestCase):
    def setUp(self):
        self.catalog = lh.sc.load_json(lh.TRAIN_JOBS, {}).get("jobs", [])
        if not self.catalog:
            self.skipTest("no train_jobs.json in the snapshot")

    def test_command_comes_from_the_catalog_not_the_page(self):
        job = self.catalog[0]
        queue, problem = lh.build_queue({"jobs": [{"id": job["id"], "hours": 2, "command": "del /s /q C:\\"}]})
        self.assertEqual(problem, "")
        self.assertEqual(queue["jobs"][0]["command"], job["command"])
        self.assertEqual(queue["jobs"][0]["cwd"], job["cwd"])

    def test_unknown_job_is_refused(self):
        queue, problem = lh.build_queue({"jobs": [{"id": "not-a-job; calc.exe", "hours": 1}]})
        self.assertIsNone(queue)
        self.assertIn("not in config/train_jobs.json", problem)

    def test_empty_selection_is_refused(self):
        self.assertIsNone(lh.build_queue({"jobs": []})[0])
        self.assertIsNone(lh.build_queue({})[0])

    def test_hours_are_clamped_and_the_name_is_made_safe(self):
        job = self.catalog[0]
        queue, _ = lh.build_queue({"name": "..\\..\\evil name", "jobs": [{"id": job["id"], "hours": 999}]})
        self.assertEqual(queue["jobs"][0]["hours"], 24.0)
        self.assertRegex(queue["name"], r"^[A-Za-z0-9_-]+$")
        self.assertEqual(lh.clamp_hours("nonsense", 3), 3.0)
        self.assertEqual(lh.clamp_hours(-5, 3), 0.1)


class ActionRefusalTests(unittest.TestCase):
    def test_repo_must_be_a_git_folder_in_the_workspace(self):
        for repo in ("..", "C:\\Windows", "no-such-repo"):
            reply = lh.do_action({"action": "open_folder", "repo": repo})
            self.assertFalse(reply["ok"], repo)
            self.assertIn("unknown repo", reply["output"])

    def test_unknown_actions_and_cloud_commands_are_refused(self):
        self.assertFalse(lh.do_action({"action": "run_anything", "arg": "calc.exe"})["ok"])
        self.assertFalse(lh.do_action({"action": "cloud", "command": "ssh"})["ok"])
        self.assertFalse(lh.do_action({"action": "cloud", "command": "run", "id": "not-a-cloud-job"})["ok"])

    def test_vault_import_needs_a_real_target_repo(self):
        self.assertFalse(lh.do_action({"action": "vault_import", "brain": "x.onnx", "target": "nope"})["ok"])


class ConfigTests(unittest.TestCase):
    def test_readiness_keeps_known_keys_and_drops_empty_apps(self):
        repo = sorted(lh.repo_names())[0]
        cleaned, problem = lh.clean_config("readiness", {"apps": {repo: {
            "stage": "polishing", "notes": "  ship it  ",
            "checklist": {"core_loop": True, "perf": False, "evil": True, "audio_fx": None},
            "extra": "dropped"}}})
        self.assertEqual(problem, "")
        self.assertEqual(cleaned, {"apps": {repo: {"stage": "polishing", "notes": "ship it",
                                                   "checklist": {"core_loop": True, "perf": False}}}})
        self.assertEqual(lh.clean_config("readiness", {"apps": {repo: {"checklist": {}}}})[0], {"apps": {}})

    def test_readiness_refuses_unknown_repos_and_stages(self):
        self.assertIsNone(lh.clean_config("readiness", {"apps": {"..": {}}})[0])
        repo = sorted(lh.repo_names())[0]
        self.assertIsNone(lh.clean_config("readiness", {"apps": {repo: {"stage": "launched"}}})[0])

    def test_vault_gold_accepts_only_brains_inside_workspace_repos(self):
        repo = sorted(lh.repo_names())[0]
        ok, _ = lh.clean_config("vault_gold", {"gold": {f"{repo}/a/b.onnx": "best"}})
        self.assertEqual(ok, {"gold": {f"{repo}/a/b.onnx": "best"}})
        for bad in (f"{repo}/../x.onnx", "NotARepo/a.onnx", f"{repo}/a.txt"):
            self.assertIsNone(lh.clean_config("vault_gold", {"gold": {bad: ""}})[0], bad)

    def test_unknown_config_names_are_refused(self):
        self.assertIsNone(lh.clean_config("appsettings", {})[0])
        self.assertFalse(lh.do_action({"action": "save_config", "arg": "../../x", "data": {}})["ok"])


class HttpGateTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        lh.TOKEN = "test-token-that-is-long-enough-123456"
        cls.server = ThreadingHTTPServer(("127.0.0.1", 0), lh.Handler)
        lh.PORT = cls.server.server_address[1]
        cls.url = f"http://127.0.0.1:{lh.PORT}"
        threading.Thread(target=cls.server.serve_forever, daemon=True).start()

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()

    def call(self, path, *, method="GET", headers=None, body=None):
        data = json.dumps(body).encode() if body is not None else None
        request = urllib.request.Request(self.url + path, data=data, method=method, headers=headers or {})
        try:
            with urllib.request.urlopen(request, timeout=10) as response:
                return response.status, dict(response.headers), response.read().decode()
        except urllib.error.HTTPError as error:
            return error.code, dict(error.headers), error.read().decode()

    def test_ping_without_a_token_reveals_nothing(self):
        status, _, text = self.call("/api/ping", headers={"Origin": SITE})
        self.assertEqual(status, 200)
        self.assertEqual(json.loads(text), {"ok": True, "authorized": False})

    def test_ping_with_the_token_is_authorized(self):
        status, headers, text = self.call("/api/ping", headers={"Origin": SITE, lh.TOKEN_HEADER: lh.TOKEN})
        self.assertEqual(status, 200)
        self.assertTrue(json.loads(text)["authorized"])
        self.assertEqual(headers.get("Access-Control-Allow-Origin"), SITE)

    def test_other_origins_are_refused(self):
        for origin in ("https://evil.example", "http://localhost:9999", "null"):
            status, headers, _ = self.call("/api/ping", headers={"Origin": origin, lh.TOKEN_HEADER: lh.TOKEN})
            self.assertEqual(status, 403, origin)
            self.assertIsNone(headers.get("Access-Control-Allow-Origin"))

    def test_a_rebound_host_name_is_refused(self):
        status, _, _ = self.call("/api/ping", headers={"Host": "evil.example", lh.TOKEN_HEADER: lh.TOKEN})
        self.assertEqual(status, 403)

    def test_actions_need_the_token(self):
        body = {"action": "git_status", "repo": "PoPunkouterSoftware"}
        for headers in ({"Origin": SITE}, {"Origin": SITE, lh.TOKEN_HEADER: "wrong"}):
            status, _, text = self.call("/api/action", method="POST", headers=headers, body=body)
            self.assertEqual(status, 401)
            self.assertFalse(json.loads(text)["authorized"])

    def test_preflight_allows_only_the_site_and_grants_private_network_access(self):
        status, headers, _ = self.call("/api/action", method="OPTIONS", headers={"Origin": SITE})
        self.assertEqual(status, 204)
        self.assertEqual(headers.get("Access-Control-Allow-Private-Network"), "true")
        self.assertIn(lh.TOKEN_HEADER, headers.get("Access-Control-Allow-Headers", ""))
        status, _, _ = self.call("/api/action", method="OPTIONS", headers={"Origin": "https://evil.example"})
        self.assertEqual(status, 403)

    def test_an_authorized_refusal_still_answers_as_json(self):
        status, _, text = self.call("/api/action", method="POST",
                                    headers={"Origin": SITE, lh.TOKEN_HEADER: lh.TOKEN},
                                    body={"action": "open_vscode", "repo": "no-such-repo"})
        self.assertEqual(status, 200)
        self.assertEqual(json.loads(text)["ok"], False)

    def test_no_files_are_served(self):
        for path in ("/", "/scripts/global/.local/helper_token.txt", "/api/token"):
            status, _, _ = self.call(path, headers={lh.TOKEN_HEADER: lh.TOKEN})
            self.assertEqual(status, 404, path)


if __name__ == "__main__":
    unittest.main()
