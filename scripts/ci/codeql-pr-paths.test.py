#!/usr/bin/env python3
"""The CodeQL pull_request path filter has one source."""

from __future__ import annotations

import unittest
from pathlib import Path

import yaml


ROOT = Path(__file__).resolve().parents[2]
PATHS = ROOT / ".github" / "codeql" / "pr-paths.txt"
WORKFLOW = ROOT / ".github" / "workflows" / "codeql.yml"


def canonical_paths() -> list[str]:
    paths = []
    for line in PATHS.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if line and not line.startswith("#"):
            paths.append(line)
    return paths


class CodeQlPrPathsTests(unittest.TestCase):
    def test_pull_request_paths_match_the_canonical_list(self) -> None:
        document = yaml.safe_load(WORKFLOW.read_text(encoding="utf-8"))
        triggers = document.get("on", document.get(True))
        self.assertEqual(triggers["pull_request"]["paths"], canonical_paths())

    def test_trunk_push_and_weekly_schedule_stay_broad(self) -> None:
        document = yaml.safe_load(WORKFLOW.read_text(encoding="utf-8"))
        triggers = document.get("on", document.get(True))
        self.assertIn("src/**", triggers["push"]["paths"])
        self.assertEqual(triggers["schedule"], [{"cron": "0 0 * * 1"}])

    def test_every_glob_names_a_real_path(self) -> None:
        for pattern in canonical_paths():
            with self.subTest(pattern=pattern):
                if pattern.endswith("/**"):
                    self.assertTrue((ROOT / pattern[:-3]).is_dir(), pattern)
                else:
                    self.assertTrue(any(ROOT.glob(pattern)), pattern)

    def test_the_list_covers_each_required_class(self) -> None:
        text = "\n".join(canonical_paths())
        for needle in (
            "Authentication",
            "Features/Admin",
            "Features/Identity",
            "Features/Security",
            "Features/Import",
            "Server/Features/Operations/**",
            "Features/ControlPlane",
            "Protocols/Mcp",
            "StudioAiProxy",
            "Infrastructure/Http",
            "PMTilesProxy",
            "Sharing",
            "Infrastructure/Session",
            "Features/Export",
            "FileStorage",
            "Infrastructure/Sql",
            ".github/workflows/**",
        ):
            with self.subTest(needle=needle):
                self.assertIn(needle, text)


if __name__ == "__main__":
    unittest.main()
