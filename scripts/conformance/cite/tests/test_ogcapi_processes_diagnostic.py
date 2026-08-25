from __future__ import annotations

import json
import os
import re
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
    omitted_methods: frozenset[tuple[str, str]] = frozenset(),
) -> str:
    supplied_primary_methods = set(re.findall(r'name="([^"]+)"', methods))
    generated_primary_methods = [
        method_name
        for mapped_class, method_name in parser.METHOD_SCENARIO_FACETS
        if mapped_class == class_name
        and method_name not in supplied_primary_methods
        and (mapped_class, method_name) not in omitted_methods
    ]
    methods += "\n" + "\n".join(
        f'<test-method status="PASS" name="{method_name}" />'
        for method_name in sorted(generated_primary_methods)
    )

    other_classes = sorted(parser.MANDATORY_VERDICT_CLASSES - {class_name})
    generated_other_methods = {
        mapped_class: sorted(
            method_name
            for candidate_class, method_name in parser.METHOD_SCENARIO_FACETS
            if candidate_class == mapped_class
            and (candidate_class, method_name) not in omitted_methods
        )
        for mapped_class in other_classes
    }
    extra_classes = "\n".join(
        f'<class name="{mapped_class}">'
        + "".join(
            f'<test-method status="PASS" name="{method_name}" />'
            for method_name in generated_other_methods[mapped_class]
        )
        + "</class>"
        for mapped_class in other_classes
    )
    extra_classes += (
        f'<class name="{parser.SUITE_PRECONDITIONS_CLASS}">'
        '<test-method status="PASS" name="verifyTestSubject" is-config="true" />'
        "</class>"
    )
    generated_count = len(generated_primary_methods) + sum(
        len(class_methods) for class_methods in generated_other_methods.values()
    )
    return _testng(
        methods,
        total=total + generated_count,
        passed=passed + generated_count,
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
        self.assertEqual(
            {
                "total": len(parser.METHOD_SCENARIO_FACETS),
                "passed": len(parser.METHOD_SCENARIO_FACETS) - 1,
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

    def test_missing_pinned_method_fails_closed(self) -> None:
        omitted_method = (LANDING_PAGE_CLASS, "testLandingPageValidation")
        methods = '<test-method status="PASS" name="testLandingPageRetrieval" />'
        with tempfile.TemporaryDirectory() as directory:
            paths = self._files(
                Path(directory),
                _complete_testng(
                    methods,
                    total=1,
                    passed=1,
                    failed=0,
                    skipped=0,
                    omitted_methods=frozenset({omitted_method}),
                ),
            )
            payload, exit_code = self._parse(paths)

        self.assertEqual(2, exit_code)
        errors = " ".join(payload["infrastructureErrors"])
        self.assertIn("omitted pinned verdict methods", errors)
        self.assertIn("LandingPage#testLandingPageValidation", errors)

    def test_pinned_method_invocation_count_drift_fails_closed(self) -> None:
        methods = """
          <test-method status="PASS" name="testLandingPageRetrieval" />
          <test-method status="PASS" name="testLandingPageRetrieval" />
          <test-method status="PASS" name="testLandingPageValidation" />
        """
        with tempfile.TemporaryDirectory() as directory:
            paths = self._files(
                Path(directory),
                _complete_testng(
                    methods,
                    total=3,
                    passed=3,
                    failed=0,
                    skipped=0,
                ),
            )
            payload, exit_code = self._parse(paths)

        self.assertEqual(2, exit_code)
        self.assertEqual("incomplete", payload["status"])
        errors = " ".join(payload["infrastructureErrors"])
        self.assertIn("pinned verdict invocation counts differ", errors)
        self.assertIn(
            "LandingPage#testLandingPageRetrieval expected 1, observed 2",
            errors,
        )

    def test_missing_pinned_configuration_method_fails_closed(self) -> None:
        methods = '<test-method status="PASS" name="testLandingPageRetrieval" />'
        xml = _complete_testng(methods, total=1, passed=1, failed=0, skipped=0)
        xml = xml.replace(
            '<test-method status="PASS" name="verifyTestSubject" is-config="true" />',
            "",
        )
        with tempfile.TemporaryDirectory() as directory:
            paths = self._files(Path(directory), xml)
            payload, exit_code = self._parse(paths)

        self.assertEqual(2, exit_code)
        errors = " ".join(payload["infrastructureErrors"])
        self.assertIn("omitted pinned configuration methods", errors)
        self.assertIn("SuitePreconditions#verifyTestSubject", errors)

    def test_requested_digest_must_match_inspected_image(self) -> None:
        methods = """
          <test-method status="PASS" name="testLandingPageRetrieval" />
          <test-method status="PASS" name="testLandingPageValidation" />
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
                ),
            )
            requested_digest = "sha256:" + ("d" * 64)
            inspected_digest = "sha256:" + ("e" * 64)
            provenance = json.loads(paths[1].read_text(encoding="utf-8"))
            provenance.update(
                {
                    "requestedServerImage": (
                        "ghcr.io/honua-io/honua-server@" + requested_digest
                    ),
                    "serverImageRepoDigests": [
                        "ghcr.io/honua-io/honua-server@" + inspected_digest
                    ],
                }
            )
            paths[1].write_text(json.dumps(provenance), encoding="utf-8")
            payload, exit_code = self._parse(paths)

        self.assertEqual(2, exit_code)
        self.assertEqual("incomplete", payload["status"])
        self.assertIn(
            "requested server image digest does not match the inspected image",
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
        self.assertIn("validate-image:", workflow)
        self.assertIn("needs: validate-image", workflow)
        self.assertIn(
            "SERVER_IMAGE: ${{ inputs.server_image || '' }}",
            workflow,
        )
        self.assertIn(
            "@sha256:[0-9a-f]{64}$",
            workflow,
        )
        self.assertNotIn("[0-9a-fA-F]", workflow)
        self.assertIn("canonical lowercase immutable", workflow)
        self.assertLess(
            common.index("id: run-suite"),
            common.index("name: Generate SBOM (SPDX JSON) for honua-server:latest"),
            "workflow-owned SBOM output must not dirty source-build inputs",
        )
        self.assertEqual(2, dockerfile.count(parser.ETS_COMMIT))
        self.assertIn("HONUA_CITE_SKIP_ETS_BUILD", runner)
        self.assertIn(f"commit={parser.ETS_COMMIT}", runner)
        self.assertIn(
            '--build-arg "HONUA_GIT_SHA=$TESTED_HONUA_GIT_SHA"',
            runner,
        )
        self.assertIn("Licensing__DevGrantEdition: Pro", compose)
        self.assertIn("OgcProcesses__CertificationProfile: ogcapi-processes10", compose)
        self.assertIn(
            'ExecutionAdmission__MaxConcurrentJobsPerPartition: "100"', compose
        )
        self.assertIn('ExecutionAdmission__MaxConcurrentJobsGlobal: "100"', compose)
        self.assertIn('ExecutionAdmission__MaxSubmissionsPerWindow: "100"', compose)
        self.assertIn('ExecutionAdmission__MaxCostWeightPerPartition: "100"', compose)
        self.assertEqual(
            "ogcapi-processes-cite-profile-v11",
            parser.FIXTURE_REVISION,
        )
        self.assertIn("upstream-aio-plus-pinned-testdata", dockerfile)

    def test_runner_supports_the_documented_interactive_mode(self) -> None:
        repo = Path(__file__).resolve().parents[4]
        runner = repo / "scripts/conformance/cite/run-cite-ogcapi-processes-tests.sh"

        completed = subprocess.run(
            ["/bin/bash", str(runner), "--help"],
            cwd=repo,
            capture_output=True,
            text=True,
            check=False,
        )

        self.assertEqual(0, completed.returncode)
        self.assertIn("--interactive", completed.stdout)
        runner_source = runner.read_text(encoding="utf-8")
        self.assertIn(
            "--interactive) INTERACTIVE=true; CLEANUP=false; shift ;;",
            runner_source,
        )
        self.assertIn('if [[ "$INTERACTIVE" == "true" ]]', runner_source)
        self.assertIn("tail -f /dev/null", runner_source)

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

    def test_source_build_runner_rejects_a_dirty_checkout(self) -> None:
        repo = Path(__file__).resolve().parents[4]
        runner = repo / "scripts/conformance/cite/run-cite-ogcapi-processes-tests.sh"
        with tempfile.TemporaryDirectory() as directory:
            fake_git = Path(directory) / "git"
            fake_git.write_text(
                f"""#!/bin/sh
if [ "$1" = "rev-parse" ]; then
    printf '%s\\n' '{SHA}'
    exit 0
fi
if [ "$1" = "status" ]; then
    printf '%s\\n' ' M src/Honua.Server/Program.cs'
    exit 0
fi
exit 1
""",
                encoding="utf-8",
            )
            fake_git.chmod(0o755)
            environment = os.environ.copy()
            environment.update(
                {
                    "GITHUB_ACTIONS": "false",
                    "HONUA_CITE_SERVER_BUILD_MODE": "source-build",
                    "HONUA_CITE_TESTED_GIT_SHA": SHA,
                    "PATH": f"{directory}:{environment['PATH']}",
                }
            )

            completed = subprocess.run(
                ["/bin/bash", str(runner)],
                cwd=repo,
                env=environment,
                capture_output=True,
                text=True,
                check=False,
            )

        self.assertEqual(2, completed.returncode)
        self.assertIn(
            "Source-build CITE evidence requires a clean Git worktree",
            completed.stderr,
        )


if __name__ == "__main__":
    unittest.main()
