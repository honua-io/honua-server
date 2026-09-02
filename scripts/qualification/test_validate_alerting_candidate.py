#!/usr/bin/env python3
"""Negative contract matrix for validate-alerting-candidate.py."""

from __future__ import annotations

import copy
import hashlib
import importlib.util
import json
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path

MODULE_PATH = Path(__file__).with_name("validate-alerting-candidate.py")
SPEC = importlib.util.spec_from_file_location("validate_alerting_candidate", MODULE_PATH)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader
SPEC.loader.exec_module(MODULE)

SHA = "a" * 40
NOW = datetime(2026, 9, 2, 5, tzinfo=timezone.utc)


class CandidateGateTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.artifact = self._file("candidate.tar", b"candidate")
        self.trx = self._file("result.trx", b"<TestRun />")
        self.workflow = self._file("workflow.yml", b"name: gate\n")
        evidence = self._file("evidence.json", b'{"hosted":true}\n')
        self.receipt = {
            "schema": MODULE.SCHEMA,
            "candidate": {"sourceSha": SHA, "artifactDigest": MODULE.digest(self.artifact)},
            "workflow": {"path": str(self.workflow), "revisionDigest": MODULE.digest(self.workflow)},
            "testRun": {"selectedTests": 1, "resultDigest": MODULE.digest(self.trx)},
            "createdAt": "2026-09-02T05:00:00Z",
            "scenarios": [
                {"name": name, "status": "green", "evidenceKind": "hosted-api",
                 "evidencePath": evidence.name, "evidenceDigest": MODULE.digest(evidence)}
                for name in sorted(MODULE.REQUIRED_SCENARIOS)
            ],
        }

    def tearDown(self) -> None:
        self.temp.cleanup()

    def _file(self, name: str, content: bytes) -> Path:
        path = self.root / name
        path.write_bytes(content)
        return path

    def validate(self, receipt=None, *, sha=SHA):
        MODULE.validate(receipt or self.receipt, candidate_sha=sha, artifact=self.artifact,
                        trx=self.trx, workflow=self.workflow, evidence_root=self.root, now=NOW)

    def test_complete_exact_candidate_receipt_passes(self):
        self.validate()

    def test_negative_matrix_fails_closed(self):
        cases = {}
        cases["red"] = lambda value: value["scenarios"][0].update(status="red")
        cases["missing-scenario"] = lambda value: value["scenarios"].pop()
        cases["unknown-status"] = lambda value: value["scenarios"][0].update(status="unknown")
        cases["duplicate-scenario"] = lambda value: value["scenarios"].append(copy.deepcopy(value["scenarios"][0]))
        cases["absent-evidence"] = lambda value: value["scenarios"][0].update(evidencePath="absent.json")
        cases["unit-only"] = lambda value: value["scenarios"][0].update(evidenceKind="unit-test")
        cases["zero-selected-tests"] = lambda value: value["testRun"].update(selectedTests=0)
        cases["stale-source-sha"] = lambda value: value["candidate"].update(sourceSha="b" * 40)
        cases["unbound-artifact"] = lambda value: value["candidate"].update(artifactDigest="sha256:" + "0" * 64)
        for name, mutate in cases.items():
            with self.subTest(name=name):
                value = copy.deepcopy(self.receipt)
                mutate(value)
                with self.assertRaises(ValueError):
                    self.validate(value)

    def test_stale_receipt_fails(self):
        value = copy.deepcopy(self.receipt)
        value["createdAt"] = "2026-09-01T00:00:00Z"
        with self.assertRaisesRegex(ValueError, "stale"):
            self.validate(value)


if __name__ == "__main__":
    unittest.main()
