#!/usr/bin/env python3
"""Exercise mirror failover without modifying the host's apt configuration."""

import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "provision-gdal-cli.sh"
UBUNTU_SOURCE = """Types: deb
URIs: http://azure.archive.ubuntu.com/ubuntu/
Suites: noble noble-updates noble-backports
Components: main restricted universe multiverse
Signed-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg

Types: deb
URIs: http://security.ubuntu.com/ubuntu/
Suites: noble-security
Components: main restricted universe multiverse
Signed-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg
"""
APT_STUB = """#!/usr/bin/env python3
import json, os, pathlib, sys
args = sys.argv[1:]
parts = next((arg.split('=', 1)[1] for arg in args if arg.startswith('Dir::Etc::sourceparts=')), None)
mirror = 'fallback' if parts else 'default'
operation = 'update' if 'update' in args else ('download' if '--download-only' in args else 'install')
log = pathlib.Path(os.environ['APT_LOG'])
events = [json.loads(line) for line in log.read_text().splitlines()] if log.exists() else []
source = (pathlib.Path(parts) / 'ubuntu.sources').read_text() if parts else ''
with log.open('a') as output:
    output.write(json.dumps(dict(mirror=mirror, operation=operation, args=args, source=source, parts=parts)) + '\\n')
failures = int(os.environ.get(mirror.upper() + '_' + operation.upper() + '_FAILURES', '0'))
attempt = sum(event['mirror'] == mirror and event['operation'] == operation for event in events)
sys.exit(1 if attempt < failures else 0)
"""


class MirrorFallbackTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.bin = self.root / "bin"
        self.bin.mkdir()
        self.source = self.root / "ubuntu.sources"
        self.source.write_text(UBUNTU_SOURCE)
        self.log = self.root / "apt.jsonl"
        self.env = dict(os.environ, PATH=f"{self.bin}:{os.environ['PATH']}",
                        HONUA_APT_CONF_DIR=str(self.root), HONUA_APT_SOURCES_FILE=str(self.source),
                        APT_LOG=str(self.log), TIMEOUT_LOG=str(self.root / "timeout.log"))
        self.stub("apt-get", APT_STUB)
        self.stub("sudo", '#!/bin/sh\nexec "$@"\n')
        self.stub("timeout", '#!/bin/sh\nprintf "%s\\n" "$*" >> "$TIMEOUT_LOG"\nshift 2\nexec "$@"\n')
        self.stub("sleep", "#!/bin/sh\nexit 0\n")
        self.stub("gdalinfo", "#!/bin/sh\necho GDAL-fixture\n")

    def stub(self, name, content):
        path = self.bin / name
        path.write_text(content)
        path.chmod(0o755)

    def run_provisioner(self, failures=None):
        self.env.update(failures or {})
        return subprocess.run(["bash", str(SCRIPT)], env=self.env, text=True,
                              capture_output=True, timeout=10)

    def events(self):
        return [json.loads(line) for line in self.log.read_text().splitlines()]

    def test_healthy_default_never_switches(self):
        result = self.run_provisioner()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual([("default", name) for name in ("update", "download", "install")],
                         [(event["mirror"], event["operation"]) for event in self.events()])
        self.assertIn("--no-download", self.events()[-1]["args"])

    def test_transient_default_download_retries_before_switching(self):
        result = self.run_provisioner({"DEFAULT_DOWNLOAD_FAILURES": "1"})
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(["update", "download", "update", "download", "install"],
                         [event["operation"] for event in self.events()])
        self.assertTrue(all(event["mirror"] == "default" for event in self.events()))

    def test_unreachable_default_switches_and_preserves_signed_suites(self):
        result = self.run_provisioner({"DEFAULT_UPDATE_FAILURES": "3"})
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        events = self.events()
        self.assertEqual(["default"] * 3 + ["fallback"] * 3,
                         [event["mirror"] for event in events])
        expected = UBUNTU_SOURCE.replace("http://azure.archive.ubuntu.com/ubuntu/",
                                         "https://archive.ubuntu.com/ubuntu").replace(
                                             "http://security.ubuntu.com/ubuntu/",
                                             "https://archive.ubuntu.com/ubuntu")
        for event in events[3:]:
            self.assertEqual(expected, event["source"])
            self.assertIn("Dir::Etc::sourcelist=/dev/null", event["args"])
            self.assertFalse(Path(event["parts"]).exists())
        self.assertEqual(UBUNTU_SOURCE, self.source.read_text())
        self.assertIn("--no-download", events[-1]["args"])
        self.assertIn("--error-on=any", events[3]["args"])
        bounds = (self.root / "timeout.log").read_text().splitlines()
        self.assertEqual(4, sum("--kill-after=15s 240s" in line for line in bounds))
        self.assertIn("--kill-after=15s 600s", bounds[-1])

    def test_both_mirrors_fail_after_six_attempts_without_installing(self):
        result = self.run_provisioner({"DEFAULT_UPDATE_FAILURES": "3", "FALLBACK_UPDATE_FAILURES": "3"})
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(["default"] * 3 + ["fallback"] * 3,
                         [event["mirror"] for event in self.events()])
        self.assertTrue(all(event["operation"] == "update" for event in self.events()))

    def test_fallback_download_uses_the_same_retry_budget(self):
        result = self.run_provisioner({"DEFAULT_DOWNLOAD_FAILURES": "3", "FALLBACK_DOWNLOAD_FAILURES": "2"})
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(3, sum(event["mirror"] == "fallback" and event["operation"] == "download"
                                for event in self.events()))

    def test_offline_install_failure_stays_blocking(self):
        result = self.run_provisioner({"DEFAULT_INSTALL_FAILURES": "1"})
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("install", self.events()[-1]["operation"])
        self.assertNotIn("GDAL-fixture", result.stdout)


if __name__ == "__main__":
    unittest.main()
