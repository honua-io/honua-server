from __future__ import annotations

import json
import unittest
from pathlib import Path

from scripts.conformance.cite import enforce_ogcapi_processes_release_gate as gate


ROOT = Path(__file__).resolve().parents[4]
BASELINE = json.loads(
    (ROOT / "scripts/conformance/cite/ogcapi-processes-release-baseline.json").read_text(
        encoding="utf-8"
    )
)
SOURCE = "a" * 40
DIGEST = "sha256:" + "b" * 64
IMAGE = "ghcr.io/honua-io/honua-server@" + DIGEST


def evidence() -> dict:
    return {
        "schema": "honua.cite-diagnostic/v1",
        "status": "diagnostic-green",
        "suite": {"sourceCommit": BASELINE["etsSourceCommit"]},
        "fixture": {
            "revision": BASELINE["fixtureRevision"],
            "configDigest": BASELINE["configDigest"],
        },
        "candidate": {"imageDigest": DIGEST, "sourceSha": SOURCE},
        "infrastructureErrors": [],
        "observations": [
            {"testId": test_id, "result": "pass"}
            for test_id in BASELINE["requiredTests"]
        ],
    }


class OgcApiProcessesReleaseGateTests(unittest.TestCase):
    def test_exact_complete_baseline_passes(self) -> None:
        report = gate.evaluate(evidence(), BASELINE, IMAGE, SOURCE)
        self.assertEqual("pass", report["verdict"])
        self.assertEqual(54, report["baseline"]["requiredTestCount"])

    def test_one_failed_requirement_is_enumerated(self) -> None:
        payload = evidence()
        payload["status"] = "diagnostic-red"
        failed_id = payload["observations"][7]["testId"]
        payload["observations"][7]["result"] = "fail"
        report = gate.evaluate(payload, BASELINE, IMAGE, SOURCE)
        self.assertEqual("fail", report["verdict"])
        self.assertEqual([failed_id], report["regressions"])
        self.assertIn(failed_id, " ".join(report["failures"]))

    def test_missing_requirement_fails_closed(self) -> None:
        payload = evidence()
        missing_id = payload["observations"].pop()["testId"]
        report = gate.evaluate(payload, BASELINE, IMAGE, SOURCE)
        self.assertEqual("fail", report["verdict"])
        self.assertIn(missing_id, " ".join(report["failures"]))

    def test_digest_provenance_mismatch_fails_closed(self) -> None:
        payload = evidence()
        payload["candidate"]["imageDigest"] = "sha256:" + "c" * 64
        report = gate.evaluate(payload, BASELINE, IMAGE, SOURCE)
        self.assertEqual("fail", report["verdict"])
        self.assertIn("image digest", " ".join(report["failures"]))

    def test_unrecorded_test_cannot_expand_denominator(self) -> None:
        payload = evidence()
        payload["observations"].append({"testId": "optional#silently-added", "result": "pass"})
        report = gate.evaluate(payload, BASELINE, IMAGE, SOURCE)
        self.assertEqual("fail", report["verdict"])
        self.assertIn("declared denominator", " ".join(report["failures"]))


if __name__ == "__main__":
    unittest.main()
