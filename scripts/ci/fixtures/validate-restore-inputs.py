#!/usr/bin/env python3
"""Restore stages keep project and package configuration, not ordinary source."""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
DOCKERFILES = (
    ROOT / "Dockerfile",
    ROOT / "docker/Dockerfile.aot",
    ROOT / "docker/Dockerfile.odata-tests",
)
PREDICATE = """-type f \\( -name '*.csproj' -o -name '*.fsproj' -o -name '*.vbproj' \\
        -o -name '*.props' -o -name '*.targets' \\
        -o -name '*.sln' -o -name '*.slnx' -o -name '*.slnf' \\
        -o -name '*.config' -o -name '.editorconfig' \\
        -o -name 'global.json' -o -name 'packages.lock.json' -o -name 'dotnet-tools.json' \\)"""


class RestoreInputTests(unittest.TestCase):
    def test_three_restore_stages_share_project_configuration_inputs(self):
        for path in DOCKERFILES:
            text = path.read_text()
            with self.subTest(path=path.name):
                self.assertIn(PREDICATE, text)
                self.assertNotIn("-name '*.json'", text)
                self.assertNotIn("-name '*.cs'", text)
                self.assertLess(text.index("restore-inputs"), text.index("COPY . ."))

    def test_odata_unit_image_restores_before_copying_source(self):
        text = (ROOT / "docker/Dockerfile.odata-tests").read_text()
        restore = text.index("restore-dotnet-with-github-packages.sh")
        copy_source = text.index("COPY . .")
        build = text.index("dotnet build")
        self.assertLess(restore, copy_source)
        self.assertLess(copy_source, build)
        self.assertIn("--no-restore", text[build:])


if __name__ == "__main__":
    unittest.main()
