#!/usr/bin/env python3
"""Run the hygiene command against older bases and tampered policy heads."""

import os
from pathlib import Path
import subprocess
import tempfile
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = yaml.safe_load((ROOT / ".github/workflows/repo-hygiene.yml").read_text())
COMMAND = next(step["run"] for step in WORKFLOW["jobs"]["retired-cache-mentions"]["steps"] if step.get("name") == "Check added text")
POLICY_NAMES = ("check-retired-cache.py", "check-retired-cache.test.py")


class RetiredCacheWorkflowTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        self.policy = self.root / "scripts/ci"
        self.git("init", "-q")
        self.git("remote", "add", "origin", str(self.root))

    def git(self, *args):
        return subprocess.check_output(["git", *args], cwd=self.root, text=True).strip()

    def commit(self):
        self.git("add", "-A")
        self.git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.test", "commit", "-qm", "fixture")
        return self.git("rev-parse", "HEAD")

    def install_policy(self):
        self.policy.mkdir(parents=True, exist_ok=True)
        for name in POLICY_NAMES:
            (self.policy / name).write_bytes((ROOT / "scripts/ci" / name).read_bytes())

    def check(self, base, head, expected):
        result = subprocess.run(
            ["bash", "-e", "-o", "pipefail", "-c", COMMAND], cwd=self.root,
            env={**os.environ, "BASE_SHA": base, "HEAD_SHA": head},
            capture_output=True, text=True,
        )
        self.assertEqual(result.returncode, expected, result.stdout + result.stderr)
        return result

    def test_trusted_base_policy_checks_older_and_modified_heads(self):
        self.install_policy()
        base = self.commit()
        for path in self.policy.iterdir():
            path.unlink()
        (self.root / "fixture.json").write_text("source fixture")
        older = self.commit()
        self.check(base, older, 0)
        (self.root / "new.txt").write_text("Redis")
        forbidden = self.commit()
        self.assertIn("new.txt", self.check(base, forbidden, 1).stdout)
        for name in POLICY_NAMES:
            (self.policy / name).write_text("raise SystemExit(0)\n")
        tampered = self.commit()
        self.assertIn("new.txt", self.check(base, tampered, 1).stdout)
        self.check(base, older, 0)

    def test_trunk_policy_checks_its_introducing_diff(self):
        (self.root / "existing.txt").write_text("allowed")
        base = self.commit()
        self.install_policy()
        clean = self.commit()
        self.git("update-ref", "refs/heads/trunk", clean)
        (self.root / "new.txt").write_text("Redis")
        forbidden = self.commit()
        self.check(base, clean, 0)
        self.check(base, forbidden, 1)

    def test_older_base_and_head_use_trunk_without_trusting_head_policy(self):
        (self.root / "existing.txt").write_text("allowed")
        base = self.commit()
        (self.root / "unrelated.txt").write_text("allowed")
        older = self.commit()
        self.install_policy()
        trusted = self.commit()
        self.git("update-ref", "refs/heads/trunk", trusted)
        self.git("checkout", "-q", "--detach", older)
        self.check(base, older, 0)
        (self.root / "new.txt").write_text("Redis")
        forbidden = self.commit()
        self.assertIn("new.txt", self.check(base, forbidden, 1).stdout)
        self.policy.mkdir(parents=True, exist_ok=True)
        for name in POLICY_NAMES:
            (self.policy / name).write_text("raise SystemExit(0)\n")
        tampered = self.commit()
        self.assertIn("new.txt", self.check(base, tampered, 1).stdout)

    def test_missing_trunk_policy_fails_closed(self):
        (self.root / "existing.txt").write_text("allowed")
        base = self.commit()
        self.git("update-ref", "refs/heads/trunk", base)
        self.check(base, base, 128)

    def test_trusted_policy_allows_cleanup_on_diverged_trunk(self):
        self.install_policy()
        (self.root / "existing.txt").write_text("Redis\n")
        ancestor = self.commit()
        (self.root / "existing.txt").write_text("removed\n")
        base = self.commit()
        self.git("checkout", "-q", "--detach", ancestor)
        (self.root / "unrelated.txt").write_text("allowed\n")
        clean = self.commit()
        self.check(base, clean, 0)
        (self.root / "new.txt").write_text("Redis\n")
        forbidden = self.commit()
        self.assertIn("new.txt", self.check(base, forbidden, 1).stdout)


if __name__ == "__main__":
    unittest.main()
