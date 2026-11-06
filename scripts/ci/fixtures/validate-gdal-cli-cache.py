#!/usr/bin/env python3
"""Run native provisioning controls without host tools, apt or the network."""
from __future__ import annotations

import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[3]
INSTALLER = ROOT / "scripts/ci/provision-gdal-cli.sh"
PACKAGES = ("gdal-bin", "python3-gdal", "python3-numpy", "python3-scipy", "libatomic1")
TOOLS = ("gdaldem", "ogr2ogr", "ogrinfo", "gdal_translate", "gdalinfo", "gdal_proximity.py", "python3")
COMMAND_DRIVER = r'''import json, os, pathlib, subprocess, sys
name, args = pathlib.Path(sys.argv[0]).name, sys.argv[1:]
with open(os.environ["NATIVE_TEST_LOG"], "a") as log:
    log.write(json.dumps([name, *args]) + "\n")
failure = os.environ.get("NATIVE_FAIL", "")
if name == "sudo":
    os.execvp(args[0], args)
elif name == "timeout":
    assert args[0] == "--kill-after=15s"
    assert args[1] in ("90s", "180s")
    os.execvp(args[2], args[2:])
elif name == "apt-get":
    assert os.environ["DEBIAN_FRONTEND"] == "noninteractive"
    phase = "download" if "--download-only" in args else "update"
    if "--no-download" in args:
        phase = "install"
        directory = next(a.split("=", 1)[1] for a in args if a.startswith("Dir::Cache::archives="))
        debs = [pathlib.Path(a) for a in args if a.endswith(".deb")]
        assert debs and all(package.parent == pathlib.Path(directory) for package in debs)
        assert pathlib.Path(directory) != pathlib.Path(os.environ["RUNNER_TEMP"], "gdal-cli-debs")
    alternate = any(a.startswith("Dir::Etc::sourceparts=") for a in args)
    if failure == phase or (failure == "primary" and phase != "install" and not alternate):
        sys.exit(41)
    if phase == "install" and failure == "dpkg-install":
        sys.exit(subprocess.run(["dpkg", "-i", *[str(p) for p in debs]]).returncode)
    if phase == "download":
        directory = next(a.split("=", 1)[1] for a in args if a.startswith("Dir::Cache::archives="))
        for package in os.environ["NATIVE_PACKAGES"].split():
            pathlib.Path(directory, package + ".deb").write_bytes(package.encode())
elif name == "dpkg":
    sys.exit(44 if failure == "dpkg-install" else 0)
elif name == "dpkg-query":
    if failure == "dpkg-query":
        sys.exit(42)
    print("not-installed" if failure == "package" else "installed")
elif name != "sleep":
    if failure == name:
        sys.exit(43)
    print("GDAL fixture")
'''


class GdalCacheTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.bin = self.root / "bin"
        self.bin.mkdir()
        self.tools = self.root / "tools"
        self.tools.mkdir()
        for name in ("bash", "sh", "mkdir", "mktemp", "chmod", "cp", "cmp", "sha256sum", "env", "sed"):
            (self.bin / name).symlink_to(shutil.which(name))
        for name in ("sudo", "timeout", "apt-get", "dpkg-query", "dpkg", "sleep"):
            self.write_driver(self.bin / name)
        for name in TOOLS:
            self.write_driver(self.tools / name)
        self.cache = self.root / "gdal-cli-debs"
        self.log = self.root / "commands.jsonl"
        self.sources = self.root / "ubuntu.sources"
        self.sources.write_text("Types: deb\nURIs: http://fixture.invalid/ubuntu\nSuites: noble noble-updates\n")
        self.env = {"PATH": str(self.bin), "RUNNER_TEMP": str(self.root),
                    "NATIVE_TEST_LOG": str(self.log), "NATIVE_FAIL": "", "NATIVE_PACKAGES": " ".join(PACKAGES),
                    "GDAL_DEB_CACHE_HIT": "false", "HONUA_APT_SOURCES_FILE": str(self.sources),
                    "HONUA_GDAL_TOOL_PATH": str(self.tools), "HONUA_GDAL_PYTHON": str(self.tools / "python3"),
                    "GITHUB_PATH": str(self.root / "github-path"), "GITHUB_OUTPUT": str(self.root / "github-output"),
                    "ImageOS": "ubuntu24", "ImageVersion": "20261101.1"}

    def write_driver(self, path):
        path.write_text(f"#!{sys.executable}\n" + COMMAND_DRIVER)
        path.chmod(0o755)

    def run_installer(self, mode="install"):
        return subprocess.run([str(self.bin / "bash"), str(INSTALLER), mode], env=self.env,
                              capture_output=True, text=True, timeout=30)

    def commands(self):
        return [json.loads(line) for line in self.log.read_text().splitlines()] if self.log.exists() else []

    def seed_cache(self):
        result = self.run_installer()
        self.assertEqual(result.returncode, 0, result.stderr + result.stdout)
        self.log.write_text("")
        self.env["GDAL_DEB_CACHE_HIT"] = "true"

    def test_resolve_needs_exact_image_and_no_package_index(self):
        result = self.run_installer("resolve")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.commands(), [])
        self.assertIn("image=ubuntu24-20261101.1", Path(self.env["GITHUB_OUTPUT"]).read_text())
        for missing in ("ImageOS", "ImageVersion"):
            original = self.env.pop(missing)
            self.assertNotEqual(self.run_installer("resolve").returncode, 0)
            self.env[missing] = original

    def test_miss_downloads_complete_set_with_whole_attempt_timeout(self):
        result = self.run_installer()
        self.assertEqual(result.returncode, 0, result.stderr)
        apt = [row for row in self.commands() if row[0] == "apt-get"]
        self.assertEqual(len(apt), 3)
        self.assertIn("--error-on=any", apt[0])
        self.assertIn("--reinstall", apt[1])
        self.assertIn("--download-only", apt[1])
        self.assertTrue(set(PACKAGES).issubset(apt[1]))
        self.assertIn("--no-download", apt[2])
        bounds = [row for row in self.commands() if row[0] == "timeout"]
        self.assertEqual([row[2] for row in bounds], ["90s", "180s"])
        self.assertIn("update --error-on=any", bounds[0][7])
        self.assertIn("--download-only", bounds[0][7])
        self.assertEqual(Path(self.env["GITHUB_PATH"]).read_text(), "/usr/bin\n")

    def test_hit_installs_offline_without_index_even_for_setup_python(self):
        self.seed_cache()
        Path(self.env["GITHUB_PATH"]).unlink()
        self.env["GDAL_PYTHON_ENVIRONMENT"] = "setup-python"
        result = self.run_installer()
        self.assertEqual(result.returncode, 0, result.stderr)
        apt = [row for row in self.commands() if row[0] == "apt-get"]
        self.assertEqual(len(apt), 1)
        self.assertIn("--no-download", apt[0])
        self.assertFalse(Path(self.env["GITHUB_PATH"]).exists())

    def test_invalid_cache_fails_before_install(self):
        self.seed_cache()
        package = self.cache / "libatomic1.deb"
        for invalid in ("missing", "corrupt", "extra", "manifest"):
            with self.subTest(invalid=invalid):
                contents = {p.name: p.read_bytes() for p in self.cache.iterdir()}
                if invalid == "missing":
                    package.unlink()
                elif invalid == "corrupt":
                    package.write_bytes(b"corrupt")
                elif invalid == "extra":
                    (self.cache / "unverified.deb").write_bytes(b"extra")
                else:
                    (self.cache / "SHA256SUMS").unlink()
                self.assertNotEqual(self.run_installer().returncode, 0)
                self.assertEqual(self.commands(), [])
                for path in self.cache.iterdir():
                    path.unlink()
                for name, content in contents.items():
                    (self.cache / name).write_bytes(content)

    def test_primary_failure_uses_bounded_https_fallback(self):
        self.env["NATIVE_FAIL"] = "primary"
        result = self.run_installer()
        self.assertEqual(result.returncode, 0, result.stderr)
        updates = [row for row in self.commands() if row[0] == "apt-get" and "update" in row]
        self.assertEqual(len(updates), 4)
        self.assertIn("https://archive.ubuntu.com/ubuntu", (self.root / "gdal-cli-sources/ubuntu.sources").read_text())
        self.assertEqual(self.sources.read_text().count("http://fixture.invalid"), 1)
        self.assertEqual([row[1] for row in self.commands() if row[0] == "sleep"], ["5", "10"])

    def test_failed_update_or_download_exhausts_both_mirrors(self):
        for failure in ("update", "download"):
            with self.subTest(failure=failure):
                self.log.write_text("")
                self.env["NATIVE_FAIL"] = failure
                result = self.run_installer()
                self.assertNotEqual(result.returncode, 0)
                attempts = [row for row in self.commands() if row[0] == "timeout"]
                self.assertEqual(len(attempts), 6)
                self.assertTrue(all(row[2] == "90s" for row in attempts))
                self.assertFalse(any("--no-download" in row for row in self.commands()))

    def test_install_dpkg_status_and_binding_failures_stay_red(self):
        self.seed_cache()
        for failure in ("install", "dpkg-install", "dpkg-query", "package", "python3", "ogrinfo", "gdalinfo"):
            with self.subTest(failure=failure):
                self.env["NATIVE_FAIL"] = failure
                self.assertNotEqual(self.run_installer().returncode, 0)

    def test_missing_tools_cannot_use_decoys_in_inherited_path(self):
        self.seed_cache()
        for tool in TOOLS:
            with self.subTest(tool=tool):
                installed = self.tools / tool
                installed.unlink()
                self.write_driver(self.bin / tool)
                result = self.run_installer()
                self.assertNotEqual(result.returncode, 0, result.stdout)
                self.write_driver(installed)
                (self.bin / tool).unlink()


if __name__ == "__main__":
    unittest.main()
