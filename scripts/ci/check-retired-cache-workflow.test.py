#!/usr/bin/env python3
"""Run the hygiene workflow command against heads predating its checker."""

import os
from pathlib import Path
import subprocess
import tempfile
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = yaml.safe_load((ROOT / ".github/workflows/repo-hygiene.yml").read_text())
COMMAND = next(step["run"] for step in WORKFLOW["jobs"]["retired-cache-mentions"]["steps"] if "run" in step)


class RetiredCacheWorkflowTests(unittest.TestCase):
    def test_trusted_base_policy_checks_older_and_modified_heads(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)

            def git(*args):
                return subprocess.check_output(["git", *args], cwd=root, text=True).strip()

            def commit():
                git("add", "-A")
                git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.test", "commit", "-qm", "fixture")
                return git("rev-parse", "HEAD")

            git("init", "-q")
            policy = root / "scripts/ci"
            policy.mkdir(parents=True)
            for name in ("check-retired-cache.py", "check-retired-cache.test.py"):
                (policy / name).write_bytes((ROOT / "scripts/ci" / name).read_bytes())
            base = commit()
            for path in policy.iterdir():
                path.unlink()
            (root / "fixture.json").write_text("source fixture")
            older = commit()

            def check(head):
                return subprocess.run(
                    ["bash", "-e", "-o", "pipefail", "-c", COMMAND], cwd=root,
                    env={**os.environ, "BASE_SHA": base, "HEAD_SHA": head},
                    capture_output=True, text=True,
                )

            allowed = check(older)
            self.assertEqual(allowed.returncode, 0, allowed.stdout + allowed.stderr)
            (root / "new.txt").write_text("Redis")
            forbidden = commit()
            blocked = check(forbidden)
            self.assertEqual(blocked.returncode, 1, blocked.stdout + blocked.stderr)
            self.assertIn("new.txt", blocked.stdout)
            policy.mkdir(parents=True, exist_ok=True)
            for name in ("check-retired-cache.py", "check-retired-cache.test.py"):
                (policy / name).write_text("raise SystemExit(0)\n")
            tampered = commit()
            blocked = check(tampered)
            self.assertEqual(blocked.returncode, 1, blocked.stdout + blocked.stderr)
            self.assertIn("new.txt", blocked.stdout)
            explicit_head = check(older)
            self.assertEqual(explicit_head.returncode, 0, explicit_head.stdout + explicit_head.stderr)

    def test_introducing_policy_checks_its_own_diff(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)

            def git(*args):
                return subprocess.check_output(["git", *args], cwd=root, text=True).strip()

            def commit():
                git("add", "-A")
                git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.test", "commit", "-qm", "fixture")
                return git("rev-parse", "HEAD")

            git("init", "-q")
            (root / "existing.txt").write_text("allowed")
            base = commit()
            policy = root / "scripts/ci"
            policy.mkdir(parents=True)
            for name in ("check-retired-cache.py", "check-retired-cache.test.py"):
                (policy / name).write_bytes((ROOT / "scripts/ci" / name).read_bytes())
            clean = commit()
            (root / "new.txt").write_text("Redis")
            forbidden = commit()
            for head, expected in [(clean, 0), (forbidden, 1)]:
                result = subprocess.run(
                    ["bash", "-e", "-o", "pipefail", "-c", COMMAND], cwd=root,
                    env={**os.environ, "BASE_SHA": base, "HEAD_SHA": head},
                    capture_output=True, text=True,
                )
                self.assertEqual(result.returncode, expected, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
