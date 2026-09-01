import importlib.util
import unittest
from pathlib import Path

SCRIPT = Path(__file__).parents[3] / "scripts/conformance/realtime/qualify_candidate.py"
SPEC = importlib.util.spec_from_file_location("qualify_candidate", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)


def evidence(revision="a" * 40):
    return {
        "server": {"revision": revision},
        "sdk": {"revision": "b" * 40},
        "transports": [
            {
                "id": transport,
                "scenarios": [{"id": "baseline-completion", "result": "passed"}],
            }
            for transport in MODULE.TRANSPORTS
        ],
    }


class RealtimeCandidateQualificationTests(unittest.TestCase):
    def test_partial_evidence_is_explicit_for_every_cell(self):
        receipt = MODULE.qualify(evidence(), "a" * 40)
        self.assertEqual("not-yet-qualified", receipt["status"])
        for transport in receipt["transports"]:
            self.assertEqual("qualified", transport["scenarios"][0]["state"])
            self.assertTrue(all(s["state"] == "not-yet-qualified" for s in transport["scenarios"][1:]))
        self.assertTrue(all(s["state"] == "not-yet-qualified" for s in receipt["multiNode"]))

    def test_failed_scenario_cannot_be_reported_as_qualified(self):
        source = evidence()
        source["transports"][0]["scenarios"].append({"id": "transport-duplicate", "result": "failed"})
        receipt = MODULE.qualify(source, "a" * 40)
        duplicate = receipt["transports"][0]["scenarios"][2]
        self.assertEqual("failed", duplicate["state"])

    def test_revision_mismatch_fails_closed(self):
        with self.assertRaisesRegex(ValueError, "exact candidate"):
            MODULE.qualify(evidence(), "c" * 40)

    def test_complete_transport_matrix_does_not_hide_multi_node_gap(self):
        source = evidence()
        scenario_ids = [next(iter(aliases)) for aliases in MODULE.SINGLE_NODE_SCENARIOS.values()]
        for transport in source["transports"]:
            transport["scenarios"] = [{"id": scenario_id, "result": "passed"} for scenario_id in scenario_ids]
        receipt = MODULE.qualify(source, "a" * 40)
        self.assertEqual("qualified", receipt["singleNodeStatus"])
        self.assertEqual("not-yet-qualified", receipt["status"])


if __name__ == "__main__":
    unittest.main()
