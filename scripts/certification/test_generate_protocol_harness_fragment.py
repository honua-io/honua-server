from __future__ import annotations

import importlib.util
import tempfile
import unittest
from argparse import Namespace
from pathlib import Path

SCRIPT = Path(__file__).with_name("generate-protocol-harness-fragment.py")
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
    def test_filter_contains_every_unique_owned_test(self):
        value = contract()
        value["assignments"].append({**value["assignments"][0], "operation": "GET /other"})
        self.assertEqual("FullyQualifiedName~ExampleTests.Get_ReturnsDocument", module.test_filter(value))
        self.assertEqual(
            "FullyQualifiedName~ExampleTests.Get_ReturnsDocument",
            module.test_filter(value, "Honua.Server.Tests"),
        )

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
        ]
        for contents, message in cases:
            with self.subTest(message=message), tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / "results.trx"
                path.write_text(contents, encoding="utf-8")
                with self.assertRaisesRegex(ValueError, message):
                    module.parse_trx(path, {"ExampleTests.Get_ReturnsDocument"})

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
