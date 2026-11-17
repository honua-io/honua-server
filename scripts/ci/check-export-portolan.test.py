#!/usr/bin/env python3
"""Offline tests for check-export-portolan.py with a stand-in validator."""

from __future__ import annotations

import importlib.util
import json
import shlex
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).with_name("check-export-portolan.py")
SPEC = importlib.util.spec_from_file_location("check_export_portolan", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

PIN = MODULE.load_pin()
FINDING = {"rule_id": "PTL-GEN-000", "severity": "error", "message": "root catalog.json is missing",
           "path": "catalog.json"}


def report(findings=(FINDING,), **validator):
    return {
        "validator": {"version": PIN["validator_version"], "applied_profile": PIN["profile"],
                      "spec_commit": PIN["spec_commit"], **validator},
        "passed": not findings, "files_checked": 3, "error_count": len(findings), "warning_count": 0, "info_count": 0,
        "summary": {"by_rule": [{"rule_id": "PTL-GEN-000", "severity": "error", "count": len(findings),
                                 "description": "the catalog root has a readable catalog.json"}]},
        "findings": list(findings),
    }


class ExportPortolanCheckTests(unittest.TestCase):
    def setUp(self):
        self.work = Path(tempfile.mkdtemp())
        self.snapshot = self.work / "snapshot"
        self.snapshot.mkdir()
        (self.snapshot / "descriptor.json").write_text("{}")
        self.output = self.work / "out/report.json"

    def validator(self, payload, exit_code=1):
        stub = self.work / "rashid.py"
        stub.write_text(f"import sys\nsys.stdout.write({payload!r})\nsys.exit({exit_code})\n")
        return shlex.join([sys.executable, str(stub)])

    def run_check(self, payload, exit_code=1):
        return MODULE.main([str(self.snapshot), str(self.output), "--rashid", self.validator(payload, exit_code)])

    def test_findings_are_advisory_and_retained(self):
        self.assertEqual(self.run_check(json.dumps(report())), 0)
        self.assertEqual(json.loads(self.output.read_text())["findings"], [FINDING])

    def test_a_conforming_export_passes(self):
        self.assertEqual(self.run_check(json.dumps(report(findings=())), exit_code=0), 0)

    def test_another_specification_version_fails_the_harness(self):
        self.assertEqual(self.run_check(json.dumps(report(applied_profile="v0.2.0"))), 2)
        self.assertFalse(self.output.exists())

    def test_a_crashed_or_silent_validator_fails_the_harness(self):
        self.assertEqual(self.run_check(json.dumps(report()), exit_code=3), 2)
        self.assertEqual(self.run_check("Traceback", exit_code=1), 2)

    def test_an_empty_snapshot_fails_the_harness(self):
        (self.snapshot / "descriptor.json").unlink()
        self.assertEqual(self.run_check(json.dumps(report())), 2)

    def test_summary_names_the_pin_and_each_rule(self):
        text = MODULE.summary_markdown(report(), PIN)
        self.assertIn(PIN["profile"], text)
        self.assertIn("does not conform", text)
        self.assertIn("| `PTL-GEN-000` | error | 1 |", text)

    def test_annotations_are_capped(self):
        lines = MODULE.annotations(report(findings=[FINDING] * (MODULE.MAX_ANNOTATIONS + 5)))
        self.assertEqual(len(lines), MODULE.MAX_ANNOTATIONS + 1)
        self.assertIn("5 more errors", lines[-1])


if __name__ == "__main__":
    unittest.main()
