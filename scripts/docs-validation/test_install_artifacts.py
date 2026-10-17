#!/usr/bin/env python3
"""Regression checks for artifact consistency, proof routing and public replay."""
import json
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import install_artifacts as artifacts
import validate_public_install as public


class ArtifactTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        for path in (artifacts.MANIFEST, *artifacts.INSTALL_PAGES, *artifacts.CLIENT_PAGES):
            dest = self.root / path
            dest.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(artifacts.ROOT / path, dest)

    def replace(self, path, old, new):
        file = self.root / path
        file.write_text(file.read_text().replace(old, new))

    def test_current_installation_examples_are_consistent(self):
        self.assertEqual([], artifacts.check(self.root))

    def test_new_image_needs_no_test_code_change(self):
        manifest = json.loads((self.root / artifacts.MANIFEST).read_text())
        old = manifest["serverImage"]
        for file in self.root.rglob("*"):
            if file.is_file():
                file.write_text(file.read_text().replace(old, "ghcr.io/honua-io/honua-server:example-2"))
        self.assertEqual([], artifacts.check(self.root))

    def test_stale_windows_default_is_rejected(self):
        path = "docs/get-started/windows-packages.md"
        image = json.loads((self.root / artifacts.MANIFEST).read_text())["serverImage"]
        self.replace(path, f"$Image = '{image}'", "$Image = 'ghcr.io/honua-io/honua-server:stale'")
        self.assertTrue(any(path in error for error in artifacts.check(self.root)))

    def test_stale_executable_client_pin_is_rejected(self):
        path = "docs/get-started/quickstart.md"
        version = json.loads((self.root / artifacts.MANIFEST).read_text())["packages"]["honua-sdk"]
        self.replace(path, f"honua-sdk=={version}", "honua-sdk==0.0.1")
        self.assertTrue(any("honua-sdk" in error for error in artifacts.check(self.root)))

    def test_release_prose_is_not_a_contract_assertion(self):
        self.replace("docs/get-started/quickstart.md", "2026.1-rc", "future-release")
        self.assertEqual([], artifacts.check(self.root))

    def test_public_recipe_extraction_and_missing_annotation(self):
        with patch.object(public, "ROOT", self.root):
            self.assertIn("CREATE TABLE public.places", public.public_blocks()["seed"])
            self.replace("docs/get-started/quickstart.md",
                         "<!-- docs-validation:install.start mode=run -->", "")
            with self.assertRaisesRegex(ValueError, "no docs-validation annotation"):
                public.public_blocks()

    def test_wrong_coordinates_and_duplicates_are_not_a_success(self):
        features = [
            {"attributes": {"id": 1, "name": "west"}, "geometry": {"x": -157.875, "y": 21.3125}},
            {"attributes": {"id": 2, "name": "east"}, "geometry": {"x": -155.0625, "y": 19.6875}},
        ]
        public.verify_features(features)
        with self.assertRaises(ValueError):
            public.verify_features(features + [features[0]])
        features[1]["geometry"]["x"] = 0
        with self.assertRaises(ValueError):
            public.verify_features(features)


class RoutingTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.git("init", "-q")
        self.git("config", "user.name", "Fixture")
        self.git("config", "user.email", "fixture@example.invalid")
        self.git("config", "commit.gpgsign", "false")
        for path in (artifacts.MANIFEST, *artifacts.INSTALL_PAGES):
            dest = self.root / path
            dest.parent.mkdir(parents=True, exist_ok=True)
            dest.write_text('{"serverImage":"old"}' if path == artifacts.MANIFEST
                            else "# Install\n\n```bash\ndocker compose up -d\n```\n")
        self.commit()
        self.base = self.git("rev-parse", "HEAD")

    def git(self, *args):
        return subprocess.run(["git", *args], cwd=self.root, capture_output=True,
                              text=True, check=True).stdout.strip()

    def commit(self):
        self.git("add", ".")
        self.git("commit", "-qm", "fixture")

    def test_wording_edit_skips_docker(self):
        file = self.root / artifacts.INSTALL_PAGES[0]
        file.write_text(file.read_text().replace("# Install", "# Revised wording"))
        self.commit()
        self.assertFalse(artifacts.needs_smoke(self.base, self.root))

    def test_executable_edit_runs_docker(self):
        file = self.root / artifacts.INSTALL_PAGES[1]
        file.write_text(file.read_text().replace("up -d", "up -d --wait"))
        self.commit()
        self.assertTrue(artifacts.needs_smoke(self.base, self.root))

    def test_image_change_runs_docker(self):
        (self.root / artifacts.MANIFEST).write_text('{"serverImage":"new"}')
        self.commit()
        self.assertTrue(artifacts.needs_smoke(self.base, self.root))

    def test_manifest_formatting_skips_docker(self):
        (self.root / artifacts.MANIFEST).write_text('{\n  "serverImage": "old"\n}\n')
        self.commit()
        self.assertFalse(artifacts.needs_smoke(self.base, self.root))

    def test_harness_change_runs_docker(self):
        file = self.root / "scripts/docs-validation/proof.py"
        file.parent.mkdir(parents=True)
        file.write_text("# proof changed\n")
        self.commit()
        self.assertTrue(artifacts.needs_smoke(self.base, self.root))

    def test_missing_base_does_not_skip_proof(self):
        with self.assertRaises(subprocess.CalledProcessError):
            artifacts.needs_smoke("missing-base", self.root)


if __name__ == "__main__":
    unittest.main()
