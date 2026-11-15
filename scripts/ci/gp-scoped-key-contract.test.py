#!/usr/bin/env python3
"""Prove the GP scoped-key contract fails unless every configured case executed and passed."""
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import yaml

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("contract", HERE / "gp-scoped-key-contract.py")
contract = importlib.util.module_from_spec(spec)
spec.loader.exec_module(contract)

SOURCE = """
    [UnitTest]
    public async Task GPScopedKey_Allows() {}
    [UnitTheory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task GPScopedKey_Refuses(int value) {}
"""


def trx(rows, executed=None, failed=0):
    """rows: (className, method, outcome) tuples."""
    namespace = contract.NAMESPACE["t"]
    definitions = "".join(
        f'<UnitTest id="id{index}"><TestMethod className="Honua.{cls}" name="{method}"/></UnitTest>'
        for index, (cls, method, _) in enumerate(rows))
    results = "".join(f'<UnitTestResult testId="id{index}" outcome="{outcome}"/>'
                      for index, (_, _, outcome) in enumerate(rows))
    executed = len(rows) if executed is None else executed
    return (f'<TestRun xmlns="{namespace}"><ResultSummary><Counters executed="{executed}" failed="{failed}"/>'
            f'</ResultSummary><TestDefinitions>{definitions}</TestDefinitions><Results>{results}</Results></TestRun>')


PASSING = [("KeyTests", "GPScopedKey_Allows", "Passed"), ("KeyTests", "GPScopedKey_Refuses", "Passed"),
           ("KeyTests", "GPScopedKey_Refuses", "Passed"), ("AdminTests", "Other", "Passed")]


class ContractTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.source = self.root / "KeyTests.cs"
        self.source.write_text(SOURCE)

    def check(self, rows, **case):
        path = self.root / "case.trx"
        path.write_text(trx(rows, **{key: case.pop(key) for key in ("executed", "failed") if key in case}))
        return contract.check(path, case)

    def test_every_source_case_and_theory_row_passing_is_accepted(self):
        self.assertEqual("4", self.check(PASSING, source=str(self.source), prefix="GPScopedKey_")["executed"])

    def test_a_missing_theory_row_failed_outcome_or_wrong_prefix_is_refused(self):
        for rows, case in ((PASSING[:2], {}), (PASSING[:2] + [("KeyTests", "GPScopedKey_Refuses", "Failed")], {}),
                           (PASSING, {"prefix": "Other_"})):
            with self.subTest(rows=len(rows), case=case), self.assertRaises(contract.ContractError):
                self.check(rows, source=str(self.source), **case)

    def test_required_classes_and_the_additional_method_must_run_and_pass(self):
        self.check(PASSING, required_classes="AdminTests", additional_method="Other")
        for case in ({"required_classes": "MissingTests"}, {"required_classes": "AdminTests", "additional_method": "Gone"}):
            with self.subTest(case=case), self.assertRaises(contract.ContractError):
                self.check(PASSING, **case)

    def test_nothing_configured_nothing_executed_or_any_failure_is_refused(self):
        for case in ({}, {"required_classes": "AdminTests", "executed": 0},
                     {"required_classes": "AdminTests", "failed": 1}):
            with self.subTest(case=case), self.assertRaises(contract.ContractError):
                self.check(PASSING, **case)

    def test_cases_share_one_build_and_keep_their_own_database_and_results(self):
        cases = [{"name": "with-db", "filter": "A"}, {"name": "no-db", "filter": "B", "needs_postgres": False}]
        calls = []

        def run(command, **kwargs):
            calls.append((command, kwargs.get("env", {}).get("HONUA_TEST_DB_URL")))
            return subprocess.CompletedProcess(command, 0)

        with patch.object(contract.subprocess, "run", side_effect=run), \
                patch.object(contract, "check", return_value={"executed": "1"}), \
                patch.dict(contract.os.environ, {"HONUA_GP_DB_URL": "Host=db"}), \
                patch.object(contract.sys, "argv", ["x", "--project", "p.csproj", "--cases", json.dumps(cases),
                                                    "--results", str(self.root)]):
            self.assertEqual(0, contract.main())
        builds = [command for command, _ in calls if command[1] == "build"]
        tests = [(command, database) for command, database in calls if command[1] == "test"]
        self.assertEqual(1, len(builds))
        self.assertEqual([("Host=db", "with-db.trx"), ("", "no-db.trx")],
                         [(database, command[command.index("--logger") + 1].split("=")[1]) for command, database in tests])
        self.assertTrue(all("--no-build" in command for command, _ in tests))

    def test_one_failing_case_fails_the_job_after_every_case_runs(self):
        cases = [{"name": "bad", "filter": "A"}, {"name": "good", "filter": "B"}]
        outcomes = iter([subprocess.CompletedProcess([], 0), subprocess.CompletedProcess([], 1),
                         subprocess.CompletedProcess([], 0)])
        with patch.object(contract.subprocess, "run", side_effect=lambda *a, **k: next(outcomes)) as run, \
                patch.object(contract, "check", return_value={"executed": "1"}), \
                patch.object(contract.sys, "argv", ["x", "--project", "p", "--cases", json.dumps(cases),
                                                    "--results", str(self.root)]):
            self.assertEqual(1, contract.main())
        self.assertEqual(3, run.call_count)

    def test_workflow_keeps_every_case_and_runs_each_project_once(self):
        workflow = yaml.safe_load((HERE.parents[1] / ".github/workflows/gp-scoped-key-contract.yml").read_text())
        include = workflow["jobs"]["contract"]["strategy"]["matrix"]["include"]
        projects = [job["project"] for job in include]
        self.assertEqual(len(projects), len(set(projects)))
        self.assertEqual(13, sum(len(job["cases"]) for job in include))
        for job in include:
            for case in job["cases"]:
                self.assertTrue(case["filter"] and (case.get("source") or case.get("required_classes")), case)
                if job.get("needs_postgres") is False:
                    self.assertTrue(case.get("needs_postgres") is False, case)


if __name__ == "__main__":
    unittest.main()
