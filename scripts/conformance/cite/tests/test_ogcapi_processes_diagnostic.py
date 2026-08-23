from __future__ import annotations

import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path

from scripts.conformance.cite import parse_ogcapi_processes_results as parser


SHA = "a" * 40
PRODUCER_SHA = "c" * 40
IMAGE = "sha256:" + "b" * 64
LANDING_PAGE_CLASS = "org.opengis.cite.ogcapiprocesses10.landingpage.LandingPage"


def _testng(
    methods: str,
    *,
    total: int,
    passed: int,
    failed: int,
    skipped: int,
    extra_classes: str = "",
    class_name: str = LANDING_PAGE_CLASS,
) -> str:
    return f"""<?xml version="1.0" encoding="UTF-8"?>
<testng-results ignored="0" total="{total}" passed="{passed}" failed="{failed}" skipped="{skipped}">
  <suite name="ogcapi-processes-1.0-1.4-SNAPSHOT">
    <test name="Core">
      <class name="{class_name}">
        {methods}
      </class>
      {extra_classes}
    </test>
  </suite>
</testng-results>
"""


def _complete_testng(
    methods: str,
    *,
    total: int,
    passed: int,
    failed: int,
    skipped: int,
    class_name: str = LANDING_PAGE_CLASS,
) -> str:
    other_classes = sorted(parser.MANDATORY_VERDICT_CLASSES - {class_name})
    extra_classes = "\n".join(
        f'<class name="{class_name}">'
        f'<test-method status="PASS" name="{parser.REPRESENTATIVE_METHODS[class_name]}" />'
        "</class>"
        for class_name in other_classes
    )
    extra_classes += (
        f'<class name="{parser.SUITE_PRECONDITIONS_CLASS}">'
        '<test-method status="PASS" name="verifyTestSubject" is-config="true" />'
        "</class>"
    )
    return _testng(
        methods,
        total=total + len(other_classes),
        passed=passed + len(other_classes),
        failed=failed,
        skipped=skipped,
        extra_classes=extra_classes,
        class_name=class_name,
    )


class OgcApiProcessesDiagnosticTests(unittest.TestCase):
    def _files(self, root: Path, xml: str) -> tuple[Path, Path, Path]:
        results = root / "raw" / "session"
        results.mkdir(parents=True)
        (results / "testng-results.xml").write_text(xml, encoding="utf-8")
        provenance = root / "provenance.json"
        provenance.write_text(
            json.dumps(
                {
                    "testedHonuaGitSha": SHA,
                    "checkedOutHonuaGitSha": PRODUCER_SHA,
                    "serverImageId": IMAGE,
                    "requestedServerImage": None,
                }
            ),
            encoding="utf-8",
        )
        config = root / "test-run-props.xml"
        config.write_text(
            "<properties><entry key='iut'>http://honua</entry></properties>",
            encoding="utf-8",
        )
        return root / "raw", provenance, config

    def _parse(self, paths: tuple[Path, Path, Path], *, exit_code: int = 0):
        return parser.parse_results(
            *paths,
            ets_exit_code=exit_code,
            started_at="2026-08-22T00:00:00Z",
            completed_at="2026-08-22T00:01:00Z",
            run_url="https://github.com/honua-io/honua-server/actions/runs/1",
        )

    def test_complete_red_run_retains_exact_test_mappings(self) -> None:
        methods = """
          <test-method status="PASS" name="testLandingPageRetrieval" signature="testLandingPageRetrieval()[pri:0, instance:null]" />
          <test-method status="FAIL" name="testLandingPageValidation" signature="testLandingPageValidation()[pri:0, instance:null]">
            <exception><message>wrong relation</message></exception>
          </test-method>
        """
        with tempfile.TemporaryDirectory() as directory:
            paths = self._files(
                Path(directory),
                _complete_testng(methods, total=2, passed=1, failed=1, skipped=0),
            )
            payload, exit_code = self._parse(paths)

        self.assertEqual(0, exit_code)
        self.assertEqual("diagnostic-red", payload["status"])
        mandatory_extras = len(parser.MANDATORY_VERDICT_CLASSES) - 1
        self.assertEqual(
            {
                "total": 2 + mandatory_extras,
                "passed": 1 + mandatory_extras,
                "failed": 1,
                "skipped": 0,
                "canttell": 0,
            },
            payload["totals"],
        )
        self.assertEqual(
            "process.ogc-api-processes", payload["observations"][0]["capabilityKey"]
        )
        self.assertEqual(SHA, payload["observations"][0]["sourceSha"])
        self.assertEqual(PRODUCER_SHA, payload["observations"][0]["producerSourceSha"])
        self.assertEqual("landing-page", payload["observations"][0]["operation"])
        self.assertEqual(
            "org.opengis.cite.ogcapiprocesses10.landingpage.LandingPage#testLandingPageValidation",
            payload["observations"][1]["testId"],
        )
        self.assertEqual("wrong relation", payload["observations"][1]["reason"])
        self.assertRegex(payload["execution"]["resultDigest"], r"^sha256:[0-9a-f]{64}$")

    def test_all_green_partial_run_is_incomplete(self) -> None:
        methods = '<test-method status="PASS" name="testLandingPageRetrieval" />'
        with tempfile.TemporaryDirectory() as directory:
            paths = self._files(
                Path(directory),
                _testng(methods, total=1, passed=1, failed=0, skipped=0),
            )
            payload, exit_code = self._parse(paths)

        self.assertEqual(2, exit_code)
        self.assertEqual("incomplete", payload["status"])
        self.assertIn(
            "omitted mandatory classes",
            " ".join(payload["infrastructureErrors"]),
        )

    def test_all_skip_run_is_incomplete(self) -> None:
        methods = '<test-method status="SKIP" name="testLandingPageRetrieval" />'
        with tempfile.TemporaryDirectory() as directory:
            paths = self._files(
                Path(directory),
                _testng(methods, total=1, passed=0, failed=0, skipped=1),
            )
            payload, exit_code = self._parse(paths)

        self.assertEqual(2, exit_code)
        self.assertEqual("incomplete", payload["status"])
        self.assertIn("all-skip", " ".join(payload["infrastructureErrors"]))

    def test_unknown_class_fails_exact_mapping(self) -> None:
        xml = _testng(
            '<test-method status="PASS" name="testLandingPageRetrieval" />',
            total=1,
            passed=1,
            failed=0,
            skipped=0,
        )
        xml = xml.replace(
            "org.opengis.cite.ogcapiprocesses10.landingpage.LandingPage",
            "org.opengis.cite.ogcapiprocesses10.future.Unknown",
        )
        with tempfile.TemporaryDirectory() as directory:
            paths = self._files(Path(directory), xml)
            payload, exit_code = self._parse(paths)

        self.assertEqual(2, exit_code)
        self.assertIn("unmapped ETS class", " ".join(payload["infrastructureErrors"]))

    def test_unknown_method_facets_fail_closed(self) -> None:
        methods = '<test-method status="PASS" name="futureLandingPageCheck" />'
        with tempfile.TemporaryDirectory() as directory:
            paths = self._files(
                Path(directory),
                _complete_testng(methods, total=1, passed=1, failed=0, skipped=0),
            )
            payload, exit_code = self._parse(paths)

        self.assertEqual(2, exit_code)
        self.assertIn(
            "unmapped ETS method facets",
            " ".join(payload["infrastructureErrors"]),
        )

    def test_jobs_methods_retain_only_their_exact_scenario_facets(self) -> None:
        methods = """
          <test-method status="PASS" name="testJobCreationInputValidation" />
          <test-method status="PASS" name="testJobSuccess" />
        """
        with tempfile.TemporaryDirectory() as directory:
            paths = self._files(
                Path(directory),
                _complete_testng(
                    methods,
                    total=2,
                    passed=2,
                    failed=0,
                    skipped=0,
                    class_name=parser.JOBS_CLASS,
                ),
            )
            payload, exit_code = self._parse(paths)

        self.assertEqual(0, exit_code)
        observations = {
            observation["methodName"]: observation
            for observation in payload["observations"]
            if observation["className"] == parser.JOBS_CLASS
        }
        self.assertEqual(
            ["negative", "media-schema"],
            observations["testJobCreationInputValidation"]["scenarioFacets"],
        )
        self.assertEqual(
            ["positive"],
            observations["testJobSuccess"]["scenarioFacets"],
        )

    def test_root_accounting_mismatch_fails_closed(self) -> None:
        methods = '<test-method status="PASS" name="testLandingPageRetrieval" />'
        with tempfile.TemporaryDirectory() as directory:
            paths = self._files(
                Path(directory),
                _testng(methods, total=2, passed=2, failed=0, skipped=0),
            )
            payload, exit_code = self._parse(paths)

        self.assertEqual(2, exit_code)
        self.assertIn("root totals", " ".join(payload["infrastructureErrors"]))

    def test_nonzero_ets_exit_is_infrastructure_failure(self) -> None:
        methods = '<test-method status="PASS" name="testLandingPageRetrieval" />'
        with tempfile.TemporaryDirectory() as directory:
            paths = self._files(
                Path(directory),
                _testng(methods, total=1, passed=1, failed=0, skipped=0),
            )
            payload, exit_code = self._parse(paths, exit_code=17)

        self.assertEqual(2, exit_code)
        self.assertIn("nonzero code 17", " ".join(payload["infrastructureErrors"]))

    def test_workflow_preserves_diagnostic_and_strict_policies(self) -> None:
        repo = Path(__file__).resolve().parents[4]
        common = (repo / ".github/workflows/cite-conformance-common.yml").read_text(
            encoding="utf-8"
        )
        workflow = (
            repo / ".github/workflows/cite-ogcapi-processes-conformance.yml"
        ).read_text(encoding="utf-8")
        dockerfile = (repo / "docker/cite/ogc-api-processes/Dockerfile.ets").read_text(
            encoding="utf-8"
        )
        runner = (
            repo / "scripts/conformance/cite/run-cite-ogcapi-processes-tests.sh"
        ).read_text(encoding="utf-8")
        compose = (repo / "docker/cite/ogc-api-processes/compose.yml").read_text(
            encoding="utf-8"
        )

        self.assertIn("diagnostic-only:", common)
        self.assertIn("default: false", common)
        self.assertIn("executed_tests == '0'", common)
        self.assertIn("diagnostic-only: true", workflow)
        self.assertIn("cron: '0 8 * * *'", workflow)
        self.assertNotIn("pull_request:", workflow)
        self.assertEqual(2, dockerfile.count(parser.ETS_COMMIT))
        self.assertIn("HONUA_CITE_SKIP_ETS_BUILD", runner)
        self.assertIn(f"commit={parser.ETS_COMMIT}", runner)
        self.assertIn("Licensing__DevGrantEdition: Pro", compose)
        self.assertIn("OgcProcesses__CertificationProfile: ogcapi-processes10", compose)
        self.assertIn(
            'ExecutionAdmission__MaxConcurrentJobsPerPartition: "100"', compose
        )
        self.assertIn('ExecutionAdmission__MaxConcurrentJobsGlobal: "100"', compose)
        self.assertIn('ExecutionAdmission__MaxSubmissionsPerWindow: "100"', compose)
        self.assertIn('ExecutionAdmission__MaxCostWeightPerPartition: "100"', compose)
        self.assertEqual(
            "ogcapi-processes-cite-profile-v7",
            parser.FIXTURE_REVISION,
        )
        self.assertIn("upstream-aio-plus-pinned-testdata", dockerfile)

    def test_local_existing_runner_requires_explicit_candidate_sha(self) -> None:
        repo = Path(__file__).resolve().parents[4]
        runner = repo / "scripts/conformance/cite/run-cite-ogcapi-processes-tests.sh"
        environment = os.environ.copy()
        environment.update(
            {
                "GITHUB_ACTIONS": "false",
                "HONUA_CITE_SKIP_BUILD": "true",
                "HONUA_CITE_SERVER_BUILD_MODE": "local-existing",
                "HONUA_CITE_TESTED_GIT_SHA": "",
            }
        )

        completed = subprocess.run(
            ["/bin/bash", str(runner), "--skip-build"],
            cwd=repo,
            env=environment,
            capture_output=True,
            text=True,
            check=False,
        )

        self.assertEqual(2, completed.returncode)
        self.assertIn(
            "Local-existing CITE images require HONUA_CITE_TESTED_GIT_SHA as a full SHA",
            completed.stderr,
        )


if __name__ == "__main__":
    unittest.main()
