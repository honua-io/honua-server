# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET


SCRIPT = Path(__file__).resolve().parents[3] / "scripts/ci/check-geoparquet-interop.py"


class GeoParquetReportOutputTests(unittest.TestCase):
    def run_report(self, outcome="passed", pytest_exit_code=0, directory="artifacts/test-results"):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            reports = root / directory
            reports.mkdir(parents=True)
            report = reports / "geoparquet-interop.xml"
            suite = ET.Element("testsuite")
            for index in range(5):
                case = ET.SubElement(suite, "testcase", name=f"cell_{index}")
                if index == 0 and outcome != "passed":
                    ET.SubElement(case, outcome)
            ET.ElementTree(suite).write(report)
            working_directory = root / "unrelated-working-directory"
            working_directory.mkdir()
            result = subprocess.run(
                [sys.executable, str(SCRIPT), str(report), str(pytest_exit_code), "a" * 40],
                cwd=working_directory,
                env=os.environ | {"GITHUB_STEP_SUMMARY": str(root / "summary.md")},
                capture_output=True,
                text=True,
                check=False,
            )
            receipt = report.with_suffix(".json")
            self.assertTrue(receipt.is_file(), result.stdout + result.stderr)
            return result, json.loads(receipt.read_text())

    def test_receipt_follows_the_xml_report_from_an_unrelated_working_directory(self):
        result, receipt = self.run_report()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(5, receipt["passed"])
        self.assertTrue(receipt["passing"])

    def test_explicit_report_directory_retains_its_receipt(self):
        result, receipt = self.run_report(directory="custom-reports")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("a" * 40, receipt["commit"])
        self.assertEqual("geoparquet-interop", receipt["suite"])

    def test_nonpassing_cells_and_pytest_exit_remain_nonpassing_with_a_receipt(self):
        for outcome, exit_code in [("skipped", 0), ("failure", 0), ("error", 0), ("passed", 1)]:
            with self.subTest(outcome=outcome, exit_code=exit_code):
                result, receipt = self.run_report(outcome, exit_code)
                self.assertNotEqual(0, result.returncode)
                self.assertFalse(receipt["passing"])
                self.assertEqual(exit_code, receipt["pytest_exit_code"])
