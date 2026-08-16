#!/usr/bin/env python3

from __future__ import annotations

import importlib.util
import json
import subprocess
import tempfile
import time
import unittest
from pathlib import Path

SCRIPT = Path(__file__).with_name("server-test-reuse-receipt.py")
SPEC = importlib.util.spec_from_file_location("reuse_receipt", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
ROOT = Path(__file__).resolve().parents[2]
PROJECT = "tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj"


class ReceiptTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.archive = self.root / "server-test-binaries-server.tar.gz"
        self.manifest = self.root / "server-test-binaries-server.manifest.json"
        self.receipt = self.root / "receipt.json"
        self.archive.write_bytes(b"bounded archive")
        self.source = str(MODULE.git(ROOT, "rev-parse", "HEAD"))
        self.sdk = "10.0.fixture"
        self.now = int(time.time())
        manifest = {
            "archive_bytes": self.archive.stat().st_size,
            "archive_file": self.archive.name,
            "archive_sha256": MODULE.sha256(self.archive.read_bytes()),
            "dotnet_sdk": self.sdk,
            "project": PROJECT,
            "source_sha": self.source,
        }
        self.manifest.write_text(json.dumps(manifest), encoding="utf-8")

    def tearDown(self) -> None:
        self.temp.cleanup()

    def common(self) -> dict:
        return dict(
            repo=ROOT, source_sha=self.source, project=PROJECT, configuration="Release",
            dotnet_sdk=self.sdk, runner_os="Linux", runner_arch="X64", run_id=123,
            run_attempt=1, manifest_path=self.manifest, archive_path=self.archive, now_epoch=self.now,
        )

    def test_exact_receipt_round_trips(self) -> None:
        receipt = MODULE.build_receipt(**self.common())
        accepted = MODULE.validate_receipt(MODULE.canonical(receipt), **self.common())
        self.assertEqual(receipt["fingerprint"], accepted["fingerprint"])

    def test_archive_tamper_is_rejected(self) -> None:
        receipt = MODULE.build_receipt(**self.common())
        self.archive.write_bytes(b"tampered")
        with self.assertRaisesRegex(ValueError, "artifact digest"):
            MODULE.validate_receipt(MODULE.canonical(receipt), **self.common())

    def test_source_and_expiry_mismatch_are_rejected(self) -> None:
        receipt = MODULE.build_receipt(**self.common())
        receipt["source"]["runner_arch"] = "ARM64"
        with self.assertRaisesRegex(ValueError, "source/run"):
            MODULE.validate_receipt(MODULE.canonical(receipt), **self.common())
        receipt = MODULE.build_receipt(**self.common())
        with self.assertRaisesRegex(ValueError, "validity"):
            MODULE.validate_receipt(
                MODULE.canonical(receipt), **{**self.common(), "now_epoch": self.now + MODULE.MAX_TTL_SECONDS + 1}
            )


if __name__ == "__main__":
    unittest.main()
