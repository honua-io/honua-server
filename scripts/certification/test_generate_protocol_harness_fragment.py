from __future__ import annotations

import importlib.util
import json
import re
import tempfile
import unittest
from argparse import Namespace
from pathlib import Path

SCRIPT = Path(__file__).with_name("generate-protocol-harness-fragment.py")
REPOSITORY_ROOT = SCRIPT.parents[2]
CONTRACT = REPOSITORY_ROOT / "docs" / "gis" / "data" / "protocol-harness-assignments.v1.json"
WORKFLOW = REPOSITORY_ROOT / ".github" / "workflows" / "protocol-harness-certification.yml"
SPEC = importlib.util.spec_from_file_location("protocol_harness_fragment", SCRIPT)
module = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
SPEC.loader.exec_module(module)


def trx(results, summary="Completed", counters=None):
    counters = counters or {"total": len(results), "executed": len(results), "passed": len(results), "failed": 0, "notExecuted": 0}
    attrs = " ".join(f'{key}="{value}"' for key, value in counters.items())
    rows = "".join(
        f'<UnitTestResult testId="test-{index}" testName="{name.replace("_", " ")}" outcome="{outcome}" />'
        for index, (name, outcome) in enumerate(results)
    )
    definitions = "".join(
        f'<UnitTest id="test-{index}" name="{name}"><TestMethod className="Honua.Tests.{name.split(".", 1)[0]}" name="{name.split(".", 1)[1]}" /></UnitTest>'
        for index, (name, _outcome) in enumerate(results)
    )
    return f'<TestRun><Results>{rows}</Results><TestDefinitions>{definitions}</TestDefinitions><ResultSummary outcome="{summary}"><Counters {attrs} /></ResultSummary></TestRun>'


def contract():
    return {
        "schema": "honua.server-protocol-harness-assignments/v1",
        "revision": "2026-08-21.1",
        "canonical_client": "Honua server public protocol integration harness",
        "client_lane": "server-protocol-harness",
        "deployment_target": "source-test-host",
        "auth_policy_revision": "server-test-host-v1",
        "assignments": [{
            "capability_key": "serve.example",
            "surface": "example",
            "operation": "GET /example",
            "test_ids": ["ExampleTests.Get_ReturnsDocument"],
            "scenario_facets": ["positive", "media-schema"],
        }],
    }


class ProtocolHarnessFragmentTests(unittest.TestCase):
    def test_workflow_executes_every_governed_project_in_sorted_evidence_order(self):
        value = json.loads(CONTRACT.read_text(encoding="utf-8"))
        expected_projects = sorted({
            module.test_project(test_id)
            for assignment in value["assignments"]
            for test_id in assignment["test_ids"]
        })
        workflow = WORKFLOW.read_text(encoding="utf-8")
        invocations = re.findall(
            r"^\s+run_project (\S+) (\S+) code_(\S+)$",
            workflow,
            flags=re.MULTILINE,
        )

        self.assertEqual(expected_projects, [project for project, _slug, _output in invocations])
        expected_evidence = [(slug, output) for _project, slug, output in invocations]
        self.assertEqual(
            [(output, code) for output, code in re.findall(r'echo "exit_(\S+)=\$\{code_(\S+)\}"', workflow)],
            [(output, output) for _slug, output in expected_evidence],
        )
        self.assertEqual(
            re.findall(
                r'--trx evidence/(\S+)\.trx --trx-exit-code "\$\{\{ steps\.tests\.outputs\.exit_(\S+) \}\}"',
                workflow,
            ),
            expected_evidence,
        )
        codes = re.search(r"codes=\((.*?)\n\s*\)", workflow, flags=re.DOTALL)
        self.assertIsNotNone(codes)
        self.assertEqual(
            re.findall(r'"\$\{\{ steps\.tests\.outputs\.exit_(\S+) \}\}"', codes.group(1)),
            [output for _slug, output in expected_evidence],
        )

    def test_filter_contains_every_unique_owned_test(self):
        value = contract()
        value["assignments"].append({**value["assignments"][0], "operation": "GET /other"})
        self.assertEqual("FullyQualifiedName~ExampleTests.Get_ReturnsDocument", module.test_filter(value))
        self.assertEqual(
            "FullyQualifiedName~ExampleTests.Get_ReturnsDocument",
            module.test_filter(value, "Honua.Server.Tests"),
        )

    def test_filter_routes_mutation_scenarios_to_their_protocol_projects(self):
        value = contract()
        value["assignments"] = [
            {
                **value["assignments"][0],
                "test_ids": ["OgcFeaturesMutationScenarioTests.MutationLifecycle_CreateReplacePatchDelete_RoundTripsEachState"],
            },
            {
                **value["assignments"][0],
                "test_ids": ["Wfs20MutationScenarioTests.Transaction_InsertUpdateReplaceDelete_RoundTripsEachState"],
            },
            {
                **value["assignments"][0],
                "test_ids": ["FeatureServerMutationScenarioTests.MutationEndpoints_AcceptValidEdits_AndRoundTripEachState"],
            },
        ]

        filters = {
            project: module.test_filter(value, project)
            for project in (
                "Honua.Protocols.OgcApi.Tests",
                "Honua.Protocols.OgcClassic.Tests",
                "Honua.Protocols.GeoServices.Tests",
            )
        }
        expected_project_by_class = {
            "OgcFeaturesMutationScenarioTests": "Honua.Protocols.OgcApi.Tests",
            "Wfs20MutationScenarioTests": "Honua.Protocols.OgcClassic.Tests",
            "FeatureServerMutationScenarioTests": "Honua.Protocols.GeoServices.Tests",
        }
        for class_name, expected_project in expected_project_by_class.items():
            with self.subTest(class_name=class_name):
                self.assertIn(class_name, filters[expected_project])
                for project, test_filter in filters.items():
                    if project != expected_project:
                        self.assertNotIn(class_name, test_filter)
        with self.assertRaisesRegex(ValueError, "no governed tests belong"):
            module.test_filter(value, "Honua.Server.Tests")

    def test_wps_contract_covers_advertised_operations_in_the_owning_project(self):
        value = module.load_contract(CONTRACT)
        rows = [row for row in value["assignments"] if row["capability_key"] == "serve.wps"]
        self.assertEqual({
            "GET /wps?request=GetCapabilities", "GET /wps?request=DescribeProcess",
            "POST /wps", "GET /wps?request=GetStatus", "GET /wps?request=GetResult",
        }, {row["operation"] for row in rows})
        self.assertEqual(5, len(rows))
        source = (REPOSITORY_ROOT / "tests/dotnet/Honua.Protocols.OgcClassic.Tests/Source/Wps20/Wps20EndpointsTests.cs").read_text()
        selected = module.test_filter(value, "Honua.Protocols.OgcClassic.Tests").split("|")
        for row in rows:
            self.assertEqual("wps", row["surface"])
            self.assertIn("positive", row["scenario_facets"])
            for test_id in row["test_ids"]:
                self.assertEqual("Honua.Protocols.OgcClassic.Tests", module.test_project(test_id))
                self.assertIn("FullyQualifiedName~" + test_id, selected)
                self.assertIn("public async Task " + test_id.split(".")[1] + "()", source)
                self.assertNotIn("ConformanceEcho", test_id)
        self.assertNotIn("Wps20EndpointsTests", module.test_filter(value, "Honua.Server.Tests"))
        self.assertEqual("source-test-host", value["deployment_target"])

    def test_wps_fragment_retains_failed_operation_and_refuses_missing_results(self):
        value = module.load_contract(CONTRACT)
        value["assignments"] = [row for row in value["assignments"] if row["capability_key"] == "serve.wps"]
        expected = {test_id for row in value["assignments"] for test_id in row["test_ids"]}
        failed = "Wps20EndpointsTests.GetStatus_CanonicalRunningJob_ReturnsIdentityAndProgress"
        results = [(test_id, "Failed" if test_id == failed else "Passed") for test_id in sorted(expected)]
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "wps.trx"
            path.write_text(trx(results, summary="Failed", counters={
                "total": len(results), "executed": len(results), "passed": len(results) - 1,
                "failed": 1, "notExecuted": 0,
            }))
            outcomes = module.parse_trx(path, expected)
            path.write_text(trx([(test_id, "Passed") for test_id in sorted(expected - {failed})]))
            with self.assertRaisesRegex(ValueError, "missing governed test results"):
                module.parse_trx(path, expected)
        args = Namespace(
            source_sha="a" * 40, producer_source_sha="b" * 40,
            image_digest="sha256:" + "c" * 64,
            candidate_cut_at="2026-08-21T00:00:00Z", started_at="2026-08-21T01:00:00Z",
            completed_at="2026-08-21T01:01:00Z", generated_at="2026-08-21T01:02:00Z",
        )
        observations = module.build_fragment(value, outcomes, args)["observations"]
        self.assertEqual(5, len(observations))
        for row in observations:
            self.assertEqual("fail" if row["operation"] == "GET /wps?request=GetStatus" else "pass", row["result"])
            self.assertEqual(row["test_ids"], row["evidence_receipt"]["identity"]["test_ids"])
            self.assertIsNone(row["image_digest"])

    def test_exact_pass_is_bound_to_test_ids(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "results.trx"
            path.write_text(trx([("ExampleTests.Get_ReturnsDocument", "Passed")]), encoding="utf-8")
            outcomes = module.parse_trx(path, {"ExampleTests.Get_ReturnsDocument"})
        args = Namespace(
            source_sha="a" * 40, producer_source_sha="b" * 40,
            image_digest="sha256:" + "c" * 64,
            candidate_cut_at="2026-08-21T00:00:00Z", started_at="2026-08-21T01:00:00Z",
            completed_at="2026-08-21T01:01:00Z", generated_at="2026-08-21T01:02:00Z",
        )
        fragment = module.build_fragment(contract(), outcomes, args)
        observation = fragment["observations"][0]
        self.assertEqual(["ExampleTests.Get_ReturnsDocument"], observation["test_ids"])
        self.assertEqual(observation["test_ids"], observation["evidence_receipt"]["identity"]["test_ids"])
        self.assertEqual("pass", observation["result"])
        self.assertIsNone(observation["image_digest"])
        self.assertIsNone(observation["evidence_receipt"]["identity"]["image_digest"])
        self.assertEqual("sha256:" + "c" * 64, fragment["candidate"]["image_digest"])

    def test_missing_duplicate_not_executed_and_bad_summary_fail_closed(self):
        cases = [
            (trx([]), "incomplete test execution"),
            (trx([("ExampleTests.Get_ReturnsDocument", "Passed"), ("ExampleTests.Get_ReturnsDocument", "Passed")]), "duplicate governed"),
            (trx([("ExampleTests.Get_ReturnsDocument", "NotExecuted")], counters={"total": 1, "executed": 0, "passed": 0, "failed": 0, "notExecuted": 1}), "incomplete test execution"),
            (trx([("ExampleTests.Get_ReturnsDocument", "Passed")], summary="Aborted"), "not complete"),
            (trx([("ExampleTests.Get_ReturnsDocument", "Passed")], summary="Failed", counters={"total": 1, "executed": 1, "passed": 0, "failed": 1, "notExecuted": 0}), "do not match result outcomes"),
            (trx([], counters={"total": 1, "executed": 1, "passed": 1, "failed": 0, "notExecuted": 0}), "do not match result count"),
            (trx([("ExampleTests.Get_ReturnsDocument", "Passed")], summary="Failed"), "ResultSummary does not match"),
        ]
        for contents, message in cases:
            with self.subTest(message=message), tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / "results.trx"
                path.write_text(contents, encoding="utf-8")
                with self.assertRaisesRegex(ValueError, message):
                    module.parse_trx(path, {"ExampleTests.Get_ReturnsDocument"})

    def test_completed_governed_failure_is_preserved(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "results.trx"
            path.write_text(
                trx(
                    [("ExampleTests.Get_ReturnsDocument", "Failed")],
                    summary="Failed",
                    counters={"total": 1, "executed": 1, "passed": 0, "failed": 1, "notExecuted": 0},
                ),
                encoding="utf-8",
            )
            outcomes = module.parse_trx(path, {"ExampleTests.Get_ReturnsDocument"})
        self.assertEqual({"ExampleTests.Get_ReturnsDocument": "fail"}, outcomes)

    def test_ungoverned_selected_result_fails_closed(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "results.trx"
            path.write_text(trx([("OtherTests.Unowned", "Failed")]), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "ungoverned selected test"):
                module.parse_trx(path, {"ExampleTests.Get_ReturnsDocument"})

    def test_duplicate_or_missing_summary_structure_fails_closed(self):
        for contents in (
            '<TestRun><Results /></TestRun>',
            '<TestRun><ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" /></ResultSummary><ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" /></ResultSummary></TestRun>',
        ):
            with tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / "results.trx"
                path.write_text(contents, encoding="utf-8")
                with self.assertRaisesRegex(ValueError, "exactly one"):
                    module.parse_trx(path, {"ExampleTests.Get_ReturnsDocument"})


if __name__ == "__main__":
    unittest.main()
