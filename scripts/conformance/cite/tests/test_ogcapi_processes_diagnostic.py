from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

from scripts.conformance.cite import parse_ogcapi_processes_results as parser


SHA = "a" * 40
IMAGE = "sha256:" + "b" * 64


def _testng(methods: str, *, total: int, passed: int, failed: int, skipped: int) -> str:
    return f"""<?xml version="1.0" encoding="UTF-8"?>
<testng-results ignored="0" total="{total}" passed="{passed}" failed="{failed}" skipped="{skipped}">
  <suite name="ogcapi-processes-1.0-1.4-SNAPSHOT">
    <test name="Core">
      <class name="org.opengis.cite.ogcapiprocesses10.landingpage.LandingPage">
        {methods}
      </class>
    </test>
  </suite>
</testng-results>
"""


class OgcApiProcessesDiagnosticTests(unittest.TestCase):
    def _files(self, root: Path, xml: str) -> tuple[Path, Path, Path]:
        results = root / "raw" / "session"
        results.mkdir(parents=True)
        (results / "testng-results.xml").write_text(xml, encoding="utf-8")
        provenance = root / "provenance.json"
        provenance.write_text(
            json.dumps({
                "testedHonuaGitSha": SHA,
                "serverImageId": IMAGE,
                "requestedServerImage": None,
            }),
            encoding="utf-8",
        )
        config = root / "test-run-props.xml"
        config.write_text("<properties><entry key='iut'>http://honua</entry></properties>", encoding="utf-8")
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
          <test-method status="PASS" name="landingPage" signature="landingPage()[pri:0, instance:null]" />
          <test-method status="FAIL" name="badLink" signature="badLink()[pri:0, instance:null]">
            <exception><message>wrong relation</message></exception>
          </test-method>
        """
        with tempfile.TemporaryDirectory() as directory:
            paths = self._files(Path(directory), _testng(methods, total=2, passed=1, failed=1, skipped=0))
            payload, exit_code = self._parse(paths)

        self.assertEqual(0, exit_code)
        self.assertEqual("diagnostic-red", payload["status"])
        self.assertEqual({"total": 2, "passed": 1, "failed": 1, "skipped": 0, "canttell": 0}, payload["totals"])
        self.assertEqual("process.ogc-api-processes", payload["observations"][0]["capabilityKey"])
        self.assertEqual("landing-page", payload["observations"][0]["operation"])
        self.assertEqual(
            "org.opengis.cite.ogcapiprocesses10.landingpage.LandingPage#badLink",
            payload["observations"][1]["testId"],
        )
        self.assertEqual("wrong relation", payload["observations"][1]["reason"])
        self.assertRegex(payload["execution"]["resultDigest"], r"^sha256:[0-9a-f]{64}$")

    def test_all_skip_run_is_incomplete(self) -> None:
        methods = '<test-method status="SKIP" name="landingPage" />'
        with tempfile.TemporaryDirectory() as directory:
            paths = self._files(Path(directory), _testng(methods, total=1, passed=0, failed=0, skipped=1))
            payload, exit_code = self._parse(paths)

        self.assertEqual(2, exit_code)
        self.assertEqual("incomplete", payload["status"])
        self.assertIn("all-skip", " ".join(payload["infrastructureErrors"]))

    def test_unknown_class_fails_exact_mapping(self) -> None:
        xml = _testng('<test-method status="PASS" name="landingPage" />', total=1, passed=1, failed=0, skipped=0)
        xml = xml.replace(
            "org.opengis.cite.ogcapiprocesses10.landingpage.LandingPage",
            "org.opengis.cite.ogcapiprocesses10.future.Unknown",
        )
        with tempfile.TemporaryDirectory() as directory:
            paths = self._files(Path(directory), xml)
            payload, exit_code = self._parse(paths)

        self.assertEqual(2, exit_code)
        self.assertIn("unmapped ETS class", " ".join(payload["infrastructureErrors"]))

    def test_root_accounting_mismatch_fails_closed(self) -> None:
        methods = '<test-method status="PASS" name="landingPage" />'
        with tempfile.TemporaryDirectory() as directory:
            paths = self._files(Path(directory), _testng(methods, total=2, passed=2, failed=0, skipped=0))
            payload, exit_code = self._parse(paths)

        self.assertEqual(2, exit_code)
        self.assertIn("root totals", " ".join(payload["infrastructureErrors"]))

    def test_nonzero_ets_exit_is_infrastructure_failure(self) -> None:
        methods = '<test-method status="PASS" name="landingPage" />'
        with tempfile.TemporaryDirectory() as directory:
            paths = self._files(Path(directory), _testng(methods, total=1, passed=1, failed=0, skipped=0))
            payload, exit_code = self._parse(paths, exit_code=17)

        self.assertEqual(2, exit_code)
        self.assertIn("nonzero code 17", " ".join(payload["infrastructureErrors"]))

    def test_workflow_preserves_diagnostic_and_strict_policies(self) -> None:
        repo = Path(__file__).resolve().parents[4]
        common = (repo / ".github/workflows/cite-conformance-common.yml").read_text(encoding="utf-8")
        workflow = (repo / ".github/workflows/cite-ogcapi-processes-conformance.yml").read_text(encoding="utf-8")
        dockerfile = (repo / "docker/cite/ogc-api-processes/Dockerfile.ets").read_text(encoding="utf-8")
        runner = (repo / "scripts/conformance/cite/run-cite-ogcapi-processes-tests.sh").read_text(encoding="utf-8")
        compose = (repo / "docker/cite/ogc-api-processes/compose.yml").read_text(encoding="utf-8")

        self.assertIn("diagnostic-only:", common)
        self.assertIn("default: false", common)
        self.assertIn("executed_tests == '0'", common)
        self.assertIn("diagnostic-only: true", workflow)
        self.assertIn("cron: '0 8 * * *'", workflow)
        self.assertNotIn("pull_request:", workflow)
        self.assertEqual(2, dockerfile.count(parser.ETS_COMMIT))
        self.assertIn("HONUA_CITE_SKIP_ETS_BUILD", runner)
        self.assertIn(f'commit={parser.ETS_COMMIT}', runner)
        self.assertIn("Licensing__DevGrantEdition: Pro", compose)


if __name__ == "__main__":
    unittest.main()
