#!/usr/bin/env python3
"""Exercise the release gate without registry access or a Docker daemon."""

import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
GATE = ROOT / "scripts/ci/trivy-release-gate.sh"
IMAGE = "ghcr.io/honua-io/honua-server@sha256:" + "a" * 64


class ReleaseGate(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory()
        self.addCleanup(self.scratch.cleanup)
        self.directory = Path(self.scratch.name)
        self.log = self.directory / "docker.jsonl"
        self.env = dict(os.environ, PATH=f"{self.directory}:{os.environ['PATH']}",
                        TRIVY_IMAGE="scanner:test", TRIVY_CACHE_DIR=str(self.directory / "cache"),
                        GATE_TEST_LOG=str(self.log))
        docker = self.directory / "docker"
        docker.write_text(f"#!{sys.executable}\n" + '''
import json, os, pathlib, sys
args = sys.argv[1:]
entry = {"args": args}
if args[0] == "run":
    mount = next(arg for arg in args if arg.endswith(":/vex:ro"))
    entry["vex_dir"] = mount.removesuffix(":/vex:ro")
    entry["vex"] = json.loads((pathlib.Path(entry["vex_dir"]) / "release-gate.openvex.json").read_text())
with open(os.environ["GATE_TEST_LOG"], "a") as log:
    log.write(json.dumps(entry) + "\\n")
sys.exit(int(os.environ.get("GATE_TEST_" + args[0].upper() + "_EXIT", "0")))
''')
        docker.chmod(0o755)

    def run_gate(self, image=IMAGE):
        result = subprocess.run(["bash", str(GATE), image], cwd=self.directory,
                                env=self.env, text=True, capture_output=True, check=False)
        self.calls = [json.loads(line) for line in self.log.read_text().splitlines()] if self.log.exists() else []
        return result

    def test_scans_exact_digest_with_committed_vex_and_existing_policy(self):
        result = self.run_gate()
        self.assertEqual(0, result.returncode, result.stderr)
        pull, scan = self.calls
        self.assertEqual(["pull", "--quiet", IMAGE], pull["args"])
        args = scan["args"]
        self.assertEqual(IMAGE, args[-1])
        for option, value in (("--vex", "/vex/release-gate.openvex.json"),
                              ("--severity", "HIGH,CRITICAL"), ("--exit-code", "1"),
                              ("--ignorefile", "/work/.trivyignore-release")):
            self.assertEqual(value, args[args.index(option) + 1])
        self.assertIn("--show-suppressed", args)
        self.assertIn("--ignore-unfixed", args)
        self.assertIn(f"{ROOT}:/work:ro", args)
        source = json.loads((ROOT / "eng/security/vex-statements.json").read_text())
        self.assertEqual([item["vulnerability"] for item in source["statements"]],
                         [item["vulnerability"]["name"] for item in scan["vex"]["statements"]])
        self.assertFalse(Path(scan["vex_dir"]).exists())

    def test_rejects_mutable_or_malformed_identity_before_docker(self):
        for image in ("server:latest", "server@sha256:", IMAGE[:-1], IMAGE + "f", IMAGE + "\n"):
            with self.subTest(image=image):
                self.assertEqual(2, self.run_gate(image).returncode)
                self.assertEqual([], self.calls)

    def test_vex_generation_failure_prevents_pull_and_scan(self):
        python = self.directory / "python3"
        python.write_text("#!/bin/sh\nexit 23\n")
        python.chmod(0o755)
        self.assertEqual(23, self.run_gate().returncode)
        self.assertEqual([], self.calls)

    def test_pull_failure_prevents_scan(self):
        self.env["GATE_TEST_PULL_EXIT"] = "24"
        self.assertEqual(24, self.run_gate().returncode)
        self.assertEqual(["pull"], [call["args"][0] for call in self.calls])

    def test_scanner_failure_propagates_and_removes_generated_vex(self):
        self.env["GATE_TEST_RUN_EXIT"] = "25"
        self.assertEqual(25, self.run_gate().returncode)
        self.assertFalse(Path(self.calls[-1]["vex_dir"]).exists())


if __name__ == "__main__":
    unittest.main()
