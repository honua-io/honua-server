"""Protocol profile selection and output retention. Dry-run only."""
import json
import os
import subprocess
import tempfile
import time
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
SELECTOR = ROOT / "scripts/ci/protocol-profile-selection.py"
PRUNER = ROOT / "scripts/ci/prune-protocol-profile-outputs.py"


def run_selector(args, env=None):
    return subprocess.run(
        ["python3", str(SELECTOR), *args],
        cwd=ROOT,
        check=False,
        capture_output=True,
        text=True,
        env=env,
    )


class ProtocolProfileSelectionTests(unittest.TestCase):
    def test_default_profile_is_all_and_does_not_build(self):
        source = SELECTOR.read_text(encoding="utf-8")
        # Path strings such as docker/Dockerfile.aot are classification inputs.
        # The selector must not invoke docker or dotnet.
        self.assertNotIn('["dotnet"', source)
        self.assertNotIn('["docker"', source)
        self.assertNotRegex(source, r"""subprocess\.(run|Popen|call|check_output)\([^)]*docker""")
        self.assertNotRegex(source, r"""subprocess\.(run|Popen|call|check_output)\([^)]*dotnet""")
        self.assertNotIn("os.system", source)
        self.assertIn('["git", "diff"', source)
        completed = run_selector(["--dry-run"])
        self.assertEqual(completed.returncode, 0, completed.stderr)
        payload = json.loads(completed.stdout)
        self.assertEqual(payload["defaultProfile"], "all")
        self.assertEqual(payload["mode"], "none")
        self.assertEqual(payload["profiles"], ["all"])

    def test_unknown_profile_fails_and_names_supported_profiles(self):
        completed = run_selector(["--profile", "nope"])
        self.assertNotEqual(completed.returncode, 0)
        self.assertIn("gsr, ogc-api, ogc-api-classic, all", completed.stderr)

    def test_composition_change_validates_every_profile_once(self):
        completed = run_selector(["--changed-file", "eng/Honua.ProtocolProfiles.props"])
        self.assertEqual(completed.returncode, 0, completed.stderr)
        payload = json.loads(completed.stdout)
        self.assertEqual(payload["mode"], "full")
        self.assertEqual(payload["profiles"], ["gsr", "ogc-api", "ogc-api-classic", "all"])
        self.assertEqual(payload["shards"], "run_all")

    def test_module_change_selects_profiles_that_include_it(self):
        completed = run_selector([
            "--changed-file",
            "src/Honua.Protocols.GeoServices/FeatureServer/FeatureServerEndpoints.cs",
        ])
        self.assertEqual(completed.returncode, 0, completed.stderr)
        payload = json.loads(completed.stdout)
        self.assertEqual(payload["mode"], "affected")
        self.assertIn("gsr", payload["profiles"])
        self.assertIn("all", payload["profiles"])
        self.assertNotIn("ogc-api", payload["profiles"])
        self.assertNotEqual(payload["shards"], "run_all")
        self.assertTrue(any("FeatureServer" in name or "GeoServices" in name for name in payload["shards"]))

    def test_unresolved_git_range_fails_closed(self):
        env = os.environ.copy()
        env.pop("BASE_REF", None)
        env.pop("HEAD_REF", None)
        completed = run_selector(["--changed-from-git"], env=env)
        self.assertEqual(completed.returncode, 0, completed.stderr)
        payload = json.loads(completed.stdout)
        self.assertEqual(payload["mode"], "full")
        self.assertEqual(payload["profiles"], ["gsr", "ogc-api", "ogc-api-classic", "all"])


class ProtocolProfilePruneTests(unittest.TestCase):
    def test_dry_run_does_not_delete_and_apply_keeps_default_outputs(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            default_output = root / "src/Honua.Server/bin/Debug"
            default_output.mkdir(parents=True)
            (default_output / "Honua.Server.dll").write_text("all", encoding="utf-8")
            restricted = root / "src/Honua.Server/bin/protocol-gsr"
            restricted.mkdir(parents=True)
            (restricted / "Honua.Server.dll").write_text("gsr", encoding="utf-8")
            old = time.time() - (10 * 86400)
            os.utime(restricted, (old, old))

            dry = subprocess.run(
                ["python3", str(PRUNER), "--root", str(root)],
                check=False,
                capture_output=True,
                text=True,
            )
            self.assertEqual(dry.returncode, 0, dry.stderr)
            self.assertIn("dry-run", dry.stdout)
            self.assertTrue((restricted / "Honua.Server.dll").is_file())
            self.assertTrue((default_output / "Honua.Server.dll").is_file())

            applied = subprocess.run(
                ["python3", str(PRUNER), "--root", str(root), "--apply"],
                check=False,
                capture_output=True,
                text=True,
            )
            self.assertEqual(applied.returncode, 0, applied.stderr)
            self.assertFalse(restricted.exists())
            self.assertTrue((default_output / "Honua.Server.dll").is_file())

    def test_active_marker_refuses_deletion(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            restricted = root / "bin/protocol-ogc-api"
            restricted.mkdir(parents=True)
            (restricted / ".protocol-profile-active").write_text("busy", encoding="utf-8")
            os.utime(restricted, (time.time() - (10 * 86400),) * 2)
            applied = subprocess.run(
                ["python3", str(PRUNER), "--root", str(root), "--apply"],
                check=False,
                capture_output=True,
                text=True,
            )
            self.assertNotEqual(applied.returncode, 0)
            self.assertIn("protocol-profile-active", applied.stderr)
            self.assertTrue(restricted.is_dir())


if __name__ == "__main__":
    unittest.main()
