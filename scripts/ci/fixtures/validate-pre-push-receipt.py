#!/usr/bin/env python3
"""Enrolled pre-push accepts only a matching supervisor receipt."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[3]


class PrePushReceiptTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        hook = self.root / "scripts/hooks"
        hook.mkdir(parents=True)
        shutil.copy2(ROOT / "scripts/hooks/pre-push", hook / "pre-push")
        prepr = self.root / "scripts/ci/pre-pr-check.sh"
        prepr.parent.mkdir(parents=True)
        prepr.write_text("#!/bin/bash\necho RAN > prepr-ran\nexit 0\n")
        prepr.chmod(0o755)
        self.bin = self.root / "bin"
        self.bin.mkdir()
        supervisor = self.bin / "honua-supervisor"
        supervisor.write_text(
            "#!/bin/bash\n"
            "printf '%s\\n' \"$*\" >> \"${HONUA_FAKE_SUPERVISOR_LOG}\"\n"
            "exit \"${HONUA_FAKE_SUPERVISOR_EXIT:-0}\"\n")
        supervisor.chmod(0o755)
        self.git("init", "-q")
        self.git("config", "user.name", "Fixture")
        self.git("config", "user.email", "fixture@example.invalid")
        (self.root / "README.md").write_text("fixture\n")
        self.git("add", ".")
        self.git("commit", "-qm", "baseline")
        self.sha = self.git("rev-parse", "HEAD")
        self.log = self.root.parent / (self.root.name + "-supervisor.log")
        self.addCleanup(lambda: self.log.unlink(missing_ok=True))

    def git(self, *args):
        return subprocess.check_output(["git", *args], cwd=self.root, text=True).strip()

    def push(self, ref, enabled=True, path=None, extra=None):
        env = {"PATH": path or f"{self.bin}:/usr/bin:/bin", "HOME": os.environ.get("HOME", "/tmp")}
        if enabled:
            env["HONUA_SUPERVISOR_ENABLED"] = "1"
            env["HONUA_FAKE_SUPERVISOR_LOG"] = str(self.log)
        if extra:
            env.update(extra)
        return subprocess.run(
            ["bash", "scripts/hooks/pre-push"],
            cwd=self.root,
            env=env,
            input=f"{ref}\n",
            text=True,
            capture_output=True,
            check=False,
        )

    def test_matching_receipt_skips_pre_pr(self):
        ref = f"refs/heads/topic {self.sha} refs/heads/topic {self.sha}"
        result = self.push(ref)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertFalse((self.root / "prepr-ran").exists())
        self.assertIn(f"--head {self.sha}", self.log.read_text())
        self.assertIn("ci verify", self.log.read_text())

    def test_verify_failure_and_missing_cli_fail_closed(self):
        ref = f"refs/heads/topic {self.sha} refs/heads/topic {self.sha}"
        failed = self.push(ref, extra={"HONUA_FAKE_SUPERVISOR_EXIT": "9"})
        self.assertEqual(75, failed.returncode, failed.stdout + failed.stderr)
        self.assertFalse((self.root / "prepr-ran").exists())
        missing = self.push(ref, path="/usr/bin:/bin")
        self.assertEqual(75, missing.returncode, missing.stdout + missing.stderr)
        self.assertFalse((self.root / "prepr-ran").exists())

    def test_dirty_tree_is_still_rejected_before_the_receipt(self):
        (self.root / "README.md").write_text("dirty\n")
        ref = f"refs/heads/topic {self.sha} refs/heads/topic {self.sha}"
        result = self.push(ref)
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        self.assertFalse(self.log.exists())
        self.assertFalse((self.root / "prepr-ran").exists())

    def test_trunk_push_skips_validation(self):
        ref = f"refs/heads/trunk {self.sha} refs/heads/trunk {self.sha}"
        result = self.push(ref)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertFalse(self.log.exists())
        self.assertFalse((self.root / "prepr-ran").exists())


if __name__ == "__main__":
    unittest.main()
