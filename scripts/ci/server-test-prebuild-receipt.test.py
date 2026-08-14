#!/usr/bin/env python3

from __future__ import annotations

import importlib.util
import json
import subprocess
import tempfile
import time
import unittest
from pathlib import Path

SCRIPT = Path(__file__).with_name("server-test-prebuild-receipt.py")
SPEC = importlib.util.spec_from_file_location("prebuild_receipt", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
ROOT = Path(__file__).resolve().parents[2]
PROJECT = "tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj"


class PrebuildReceiptTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.directory = Path(self.temp.name)
        self.policy = self.directory / "policy"
        self.policy.mkdir()
        subprocess.run(["git", "init", "--initial-branch=trunk"], cwd=self.policy, check=True, capture_output=True)
        subprocess.run(["git", "config", "user.email", "ci@example.invalid"], cwd=self.policy, check=True)
        subprocess.run(["git", "config", "user.name", "CI Fixture"], cwd=self.policy, check=True)
        for relative in MODULE.POLICY_PATHS:
            target = self.policy / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(f"fixture:{relative}\n", encoding="utf-8")
        subprocess.run(["git", "add", "."], cwd=self.policy, check=True)
        subprocess.run(["git", "commit", "-m", "policy"], cwd=self.policy, check=True, capture_output=True)
        self.policy_sha = str(MODULE.BASE.git(self.policy, "rev-parse", "HEAD"))
        self.source_sha = str(MODULE.BASE.git(ROOT, "rev-parse", "HEAD"))
        self.archive = self.directory / "server-test-binaries-server.tar.gz"
        self.manifest = self.directory / "server-test-binaries-server.manifest.json"
        self.archive.write_bytes(b"bounded archive")
        self.sdk = "10.0.fixture"
        self.now = int(time.time())
        self.manifest.write_text(
            json.dumps(
                {
                    "archive_bytes": self.archive.stat().st_size,
                    "archive_file": self.archive.name,
                    "archive_sha256": MODULE.sha256(self.archive.read_bytes()),
                    "dotnet_sdk": self.sdk,
                    "project": PROJECT,
                    "source_sha": self.source_sha,
                }
            ),
            encoding="utf-8",
        )

    def tearDown(self) -> None:
        self.temp.cleanup()

    def common(self) -> dict:
        return {
            "source_repo": ROOT,
            "policy_repo": self.policy,
            "repository": "honua-io/honua-server",
            "pull_request": 42,
            "source_sha": self.source_sha,
            "policy_sha": self.policy_sha,
            "project": PROJECT,
            "configuration": "Release",
            "dotnet_sdk": self.sdk,
            "runner_os": "Linux",
            "runner_arch": "X64",
            "runner_image": "ubuntu24-20260801.1",
            "producer_run_id": 123,
            "producer_run_attempt": 1,
            "manifest_path": self.manifest,
            "archive_path": self.archive,
            "now_epoch": self.now,
        }

    def test_cross_workflow_receipt_round_trips(self) -> None:
        receipt = MODULE.build_receipt(**self.common())
        accepted = MODULE.validate_receipt(MODULE.BASE.canonical(receipt), **self.common())
        self.assertEqual(receipt["fingerprint"], accepted["fingerprint"])

    def test_policy_and_producer_mismatch_fail_closed(self) -> None:
        receipt = MODULE.build_receipt(**self.common())
        with self.assertRaisesRegex(ValueError, "identity"):
            MODULE.validate_receipt(
                MODULE.BASE.canonical(receipt), **{**self.common(), "producer_run_id": 124}
            )
        receipt = MODULE.build_receipt(**self.common())
        receipt["policy"]["workflow_path"] = ".github/workflows/other.yml"
        receipt["fingerprint"] = MODULE.fingerprint(receipt)
        with self.assertRaisesRegex(ValueError, "identity"):
            MODULE.validate_receipt(MODULE.BASE.canonical(receipt), **self.common())

    def test_inner_archive_tamper_is_rejected(self) -> None:
        receipt = MODULE.build_receipt(**self.common())
        self.archive.write_bytes(b"tampered")
        with self.assertRaisesRegex(ValueError, "artifact digest"):
            MODULE.validate_receipt(MODULE.BASE.canonical(receipt), **self.common())

    def test_unrelated_trunk_moves_are_compatible_but_policy_changes_are_not(self) -> None:
        receipt = MODULE.build_receipt(**self.common())
        producer_policy_sha = self.policy_sha
        (self.policy / "unrelated.txt").write_text("unrelated trunk move\n", encoding="utf-8")
        subprocess.run(["git", "add", "."], cwd=self.policy, check=True)
        subprocess.run(
            ["git", "commit", "-m", "unrelated"], cwd=self.policy, check=True, capture_output=True
        )
        current_policy_sha = str(MODULE.BASE.git(self.policy, "rev-parse", "HEAD"))
        accepted = MODULE.validate_receipt(
            MODULE.BASE.canonical(receipt),
            **{
                **self.common(),
                "policy_sha": current_policy_sha,
                "producer_policy_sha": producer_policy_sha,
            },
        )
        self.assertEqual(receipt["fingerprint"], accepted["fingerprint"])

        changed = self.policy / MODULE.POLICY_PATHS[0]
        changed.write_text("changed policy\n", encoding="utf-8")
        subprocess.run(["git", "add", "."], cwd=self.policy, check=True)
        subprocess.run(
            ["git", "commit", "-m", "policy change"], cwd=self.policy, check=True, capture_output=True
        )
        changed_policy_sha = str(MODULE.BASE.git(self.policy, "rev-parse", "HEAD"))
        with self.assertRaisesRegex(ValueError, "current trusted policy inputs"):
            MODULE.validate_receipt(
                MODULE.BASE.canonical(receipt),
                **{
                    **self.common(),
                    "policy_sha": changed_policy_sha,
                    "producer_policy_sha": producer_policy_sha,
                },
            )


if __name__ == "__main__":
    unittest.main()
