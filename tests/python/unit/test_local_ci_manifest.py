"""Regression evidence: local dirty files must reach project selection."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[3]

class LocalManifestTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.scripts = self.root / "scripts/ci"
        self.scripts.mkdir(parents=True)
        for name in ("classify-pre-pr-changes.sh", "compute-affected-projects.sh"):
            shutil.copy2(ROOT / "scripts/ci" / name, self.scripts / name)
        self.git("init", "-b", "trunk")
        self.git("config", "user.name", "Test")
        self.git("config", "user.email", "test@example.invalid")
        for name in ("A", "B"):
            folder = self.root / "src" / name
            folder.mkdir(parents=True)
            (folder / f"{name}.csproj").write_text("<Project/>")
            (folder / "Existing.cs").write_text("// existing")
        self.git("add", ".")
        self.git("commit", "-m", "baseline")
        self.git("branch", "baseline")

    def tearDown(self):
        self.temp.cleanup()

    def git(self, *args):
        return subprocess.check_output(["git", *args], cwd=self.root, stderr=subprocess.DEVNULL, text=True)

    def test_untracked_and_staged_files_select_both_projects(self):
        (self.root / "src/A/New.cs").write_text("// new")
        (self.root / "src/B/Existing.cs").write_text("// staged")
        self.git("add", "src/B/Existing.cs")
        result = subprocess.check_output(["bash", str(self.scripts / "classify-pre-pr-changes.sh"), "baseline"], cwd=self.root, text=True)
        manifest = self.root / "manifest"
        manifest.write_text("\n".join(result.splitlines()[1:]) + "\n")
        env = dict(os.environ, BASE_REF="baseline", HONUA_CHANGED_FILES_FILE=str(manifest))
        selected = subprocess.check_output(["bash", str(self.scripts / "compute-affected-projects.sh")], cwd=self.root, env=env, text=True)
        self.assertEqual({"src/A/A.csproj", "src/B/B.csproj"}, set(selected.splitlines()))

    def test_missing_manifest_fails_closed(self):
        env = dict(os.environ, BASE_REF="baseline", HONUA_CHANGED_FILES_FILE=str(self.root / "missing"))
        selected = subprocess.check_output(["bash", str(self.scripts / "compute-affected-projects.sh")], cwd=self.root, env=env, text=True)
        self.assertEqual("ALL", selected.strip())

    def test_cross_project_rename_keeps_both_sides(self):
        self.git("mv", "src/A/Existing.cs", "src/B/Moved.cs")
        result = subprocess.check_output(["bash", str(self.scripts / "classify-pre-pr-changes.sh"), "baseline"], cwd=self.root, text=True)
        self.assertIn("src/A/Existing.cs", result.splitlines())
        self.assertIn("src/B/Moved.cs", result.splitlines())

    def test_escaped_filename_is_unknown_not_empty_scope(self):
        (self.root / "src/A/line\nbreak.cs").write_text("// unusual")
        result = subprocess.check_output(["bash", str(self.scripts / "classify-pre-pr-changes.sh"), "baseline"], cwd=self.root, text=True)
        self.assertEqual("UNKNOWN", result.strip())

    def test_invalid_head_fails_closed(self):
        env = dict(os.environ, BASE_REF="baseline", HEAD_REF="missing")
        env.pop("HONUA_CHANGED_FILES_FILE", None)
        selected = subprocess.check_output(["bash", str(self.scripts / "compute-affected-projects.sh")], cwd=self.root, env=env, text=True)
        self.assertEqual("ALL", selected.strip())
