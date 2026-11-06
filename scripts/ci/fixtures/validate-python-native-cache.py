#!/usr/bin/env python3
"""Exercise the native dependency installer with an isolated command path."""
from __future__ import annotations

import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[3]
INSTALLER = ROOT / "scripts/ci/provision-python-native.sh"
CACHE_ACTION = "55cc8345863c7cc4c66a329aec7e433d2d1c52a9"
JOB_BUDGET_MINUTES = 25
COMMAND_DRIVER = r'''import json, os, pathlib, sys
name, args = pathlib.Path(sys.argv[0]).name, sys.argv[1:]
with open(os.environ["NATIVE_TEST_LOG"], "a") as log:
    log.write(json.dumps([name, *args]) + "\n")
failure = os.environ.get("NATIVE_FAIL", "")
if name == "sudo":
    os.execvp(args[0], args)
elif name == "timeout":
    assert args[0] == "--kill-after=15s"
    assert args[1] in ("120s", "300s", "180s")
    os.execvp(args[2], args[2:])
elif name == "apt-get":
    phase = "apt-download" if "--download-only" in args else "apt-update"
    if "--no-download" in args:
        phase = "apt-install"
        assert os.environ["DEBIAN_FRONTEND"] == "noninteractive"
        directory = next(a.split("=", 1)[1] for a in args if a.startswith("Dir::Cache::archives="))
        debs = [pathlib.Path(a) for a in args if a.endswith(".deb")]
        assert debs and all(package.parent == pathlib.Path(directory) for package in debs)
    if failure == phase:
        sys.exit(41)
    if phase == "apt-download":
        directory = next(a.split("=", 1)[1] for a in args if a.startswith("Dir::Cache::archives="))
        for package in ("gdal-bin", "libatomic1"):
            pathlib.Path(directory, package + ".deb").write_bytes(package.encode())
elif name == "dpkg-query":
    print("not-installed" if failure == "package" else "installed")
elif name == "ogrinfo":
    print("GDAL fixture")
else:
    sys.exit(90)
'''


class PythonNativeCacheTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.bin = self.root / "bin"
        self.bin.mkdir()
        for name in ("bash", "mkdir", "cp", "sha256sum", "env"):
            (self.bin / name).symlink_to(shutil.which(name))
        for name in ("sudo", "timeout", "apt-get", "dpkg-query", "ogrinfo"):
            command = self.bin / name
            command.write_text(f"#!{sys.executable}\n" + COMMAND_DRIVER)
            command.chmod(0o755)
        self.cache = self.root / "python-native-debs"
        self.log = self.root / "commands.jsonl"
        self.env = {"PATH": str(self.bin), "RUNNER_TEMP": str(self.root),
                    "NATIVE_TEST_LOG": str(self.log), "NATIVE_FAIL": "", "PYTHON_NATIVE_CACHE_HIT": "false"}

    def run_installer(self):
        return subprocess.run([str(self.bin / "bash"), str(INSTALLER)], env=self.env,
                              capture_output=True, text=True, timeout=30)

    def commands(self):
        return [json.loads(line) for line in self.log.read_text().splitlines()] if self.log.exists() else []

    def seed_cache(self):
        result = self.run_installer()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.log.write_text("")
        self.env["PYTHON_NATIVE_CACHE_HIT"] = "true"

    def test_cache_miss_downloads_then_installs_the_complete_set(self):
        result = self.run_installer()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue((self.cache / "SHA256SUMS").is_file())
        commands = self.commands()
        apt = [row for row in commands if row[0] == "apt-get"]
        self.assertEqual(len(apt), 3)
        bounds = [row[2] for row in commands if row[0] == "timeout"]
        self.assertEqual(bounds, ["120s", "300s", "180s"])
        self.assertIn("--error-on=any", apt[0])
        self.assertIn("--reinstall", apt[1])
        self.assertIn("--download-only", apt[1])
        self.assertIn("--no-download", apt[2])
        self.assertIn("GDAL fixture", result.stdout)

    def test_cache_hit_installs_only_local_packages_without_a_download(self):
        self.seed_cache()
        result = self.run_installer()
        self.assertEqual(result.returncode, 0, result.stderr)
        apt = [row for row in self.commands() if row[0] == "apt-get"]
        self.assertEqual(len(apt), 1)
        self.assertIn("--no-download", apt[0])
        self.assertNotIn("--download-only", apt[0])
        self.assertNotIn("update", apt[0])

    def test_missing_cached_package_fails_before_install(self):
        self.seed_cache()
        (self.cache / "libatomic1.deb").unlink()
        self.assertNotEqual(self.run_installer().returncode, 0)
        self.assertEqual(self.commands(), [])

    def test_corrupted_cached_package_fails_before_install(self):
        self.seed_cache()
        (self.cache / "gdal-bin.deb").write_bytes(b"corrupt")
        self.assertNotEqual(self.run_installer().returncode, 0)
        self.assertEqual(self.commands(), [])

    def test_cache_hit_without_a_manifest_fails(self):
        self.cache.mkdir()
        self.env["PYTHON_NATIVE_CACHE_HIT"] = "true"
        self.assertNotEqual(self.run_installer().returncode, 0)
        self.assertEqual(self.commands(), [])

    def test_failed_native_commands_keep_the_gate_red(self):
        for failure in ("apt-update", "apt-download", "apt-install", "package"):
            with self.subTest(failure=failure):
                self.env["NATIVE_FAIL"] = failure
                result = self.run_installer()
                self.assertNotEqual(result.returncode, 0, result.stdout)

    def test_missing_tool_cannot_fall_back_to_the_host_path(self):
        self.seed_cache()
        (self.bin / "ogrinfo").unlink()
        result = self.run_installer()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("ogrinfo", result.stderr)

    def test_workflow_keys_the_cache_to_the_runner_and_installer(self):
        workflow = yaml.safe_load((ROOT / ".github/workflows/ci.yml").read_text())
        job = workflow["jobs"]["python-integration-tests"]
        self.assertEqual(job["timeout-minutes"], JOB_BUDGET_MINUTES)
        steps = {step.get("id"): step for step in job["steps"] if step.get("id")}
        restore = steps["python-native-cache"]
        self.assertEqual(restore["uses"], f"actions/cache/restore@{CACHE_ACTION}")
        key = restore["with"]["key"]
        for identity in ("runner.os", "runner.arch", "python-native-image", "hashFiles('scripts/ci/provision-python-native.sh')"):
            self.assertIn(identity, key)
        self.assertNotIn("restore-keys", restore["with"])
        image = steps["python-native-image"]["run"]
        self.assertIn("${ImageOS:?", image)
        self.assertIn("${ImageVersion:?", image)
        save = next(step for step in job["steps"] if step.get("uses") == f"actions/cache/save@{CACHE_ACTION}")
        self.assertEqual(save["with"], restore["with"])
        self.assertEqual(save["if"], "steps.python-native-cache.outputs.cache-hit != 'true'")
        native = next(step for step in job["steps"] if step.get("run") == "bash scripts/ci/provision-python-native.sh")
        self.assertEqual(native["env"]["PYTHON_NATIVE_CACHE_HIT"], "${{ steps.python-native-cache.outputs.cache-hit }}")
        self.assertIn("Prove GeoParquet independent-consumer execution", [step.get("name") for step in job["steps"]])
        self.assertIn("Run Python Integration Tests (OGC + FeatureServer + GDAL)", [step.get("name") for step in job["steps"]])


if __name__ == "__main__":
    unittest.main()
