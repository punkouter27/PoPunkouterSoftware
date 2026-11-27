"""Tests for build_rl_reports.rewrite: every local link in GLOBAL's reports becomes a GitHub URL,
a site page, or plain text — nothing relative is left to break or to point at the PC.

Run:  python -m unittest scripts/global/test_build_rl_reports.py   (from the repo root)
"""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import build_rl_reports as br  # noqa: E402

GITHUB = {"PoBox": ("punkouter27/PoBox", "master"), "PoSlapUnreal": ("punkouter27/PoSlapUnreal", "main")}


class RewriteTests(unittest.TestCase):
    def test_images_come_from_raw_github(self):
        page, _ = br.rewrite('<img src="../../../PoBox/SCREENSHOTS/a b.gif" alt="x">', GITHUB)
        self.assertIn('src="https://raw.githubusercontent.com/punkouter27/PoBox/master/SCREENSHOTS/a b.gif"', page)

    def test_repo_file_links_open_on_github_in_a_new_tab(self):
        page, dropped = br.rewrite('<a class="file-link" href="../../../PoSlapUnreal/training/ppo.py">ppo</a>', GITHUB)
        self.assertIn('href="https://github.com/punkouter27/PoSlapUnreal/blob/main/training/ppo.py"', page)
        self.assertIn('target="_blank"', page)
        self.assertIn('class="file-link"', page)
        self.assertEqual(dropped, 0)

    def test_links_between_reports_go_to_the_site_in_the_top_window(self):
        page, _ = br.rewrite('<a href="rl_progress_over_time.html">p</a> <a href="../../index.html">hub</a>', GITHUB)
        self.assertIn('href="/global/rl?view=progress" target="_top"', page)
        self.assertIn('href="/global" target="_top"', page)

    def test_in_page_anchors_are_untouched(self):
        page, dropped = br.rewrite('<a href="#trainNextSection">t</a>', GITHUB)
        self.assertEqual(page, '<a href="#trainNextSection">t</a>')
        self.assertEqual(dropped, 0)

    def test_local_only_links_become_plain_text(self):
        source = ('<a href="../history/snapshots/x.json"><code>x</code></a>'
                  '<a href="../../../NotAGitRepo/file.txt">y</a><img src="../../../NotAGitRepo/a.png">')
        page, dropped = br.rewrite(source, GITHUB)
        self.assertEqual(dropped, 2)
        self.assertNotIn("../", page)
        self.assertIn('src=""', page)


if __name__ == "__main__":
    unittest.main()
