"""Contract readiness succeeds without certifying a candidate."""
from __future__ import annotations

import copy
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

VERIFIER = Path(__file__).with_name("verify-client-certification-receipts.py")
_SPEC = importlib.util.spec_from_file_location("roster_readiness_verifier", VERIFIER)
assert _SPEC and _SPEC.loader
module = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(module)
ROOT = module.repository_root(VERIFIER.parent)
MIRROR = json.loads((ROOT / module.REQUIREMENTS_RELATIVE_PATH).read_text(encoding="utf-8"))


def blocker_codes(verdict):
    return [blocker["reason_code"] for blocker in verdict["blockers"]]


class ContractReadinessTests(unittest.TestCase):
    def test_contract_readiness_accepts_only_candidate_absence_without_certifying(self):
        verdicts = module.verify_contract(MIRROR, ROOT)
        ready_rows = [row for row, verdict in zip(MIRROR["requirements"], verdicts)
                      if blocker_codes(verdict) == ["no-candidate"]]
        self.assertTrue(ready_rows, "exercise a real emitted, joinable producer")
        with tempfile.TemporaryDirectory() as directory:
            requirements = Path(directory) / "requirements.json"
            output = Path(directory) / "verdict.json"
            requirements.write_text(json.dumps({**MIRROR, "requirements": ready_rows}))
            arguments = ["--requirements", str(requirements), "--output", str(output)]
            self.assertEqual(1, module.main(arguments))
            self.assertEqual(0, module.main([*arguments, "--check-readiness"]))
            report = json.loads(output.read_text())
            self.assertFalse(report["summary"]["green"])
            self.assertEqual(0, report["summary"]["byResult"]["pass"])
            self.assertEqual(len(ready_rows), report["summary"]["byResult"]["skip"])
            self.assertIsNone(report["candidate"])

    def test_contract_readiness_rejects_missing_producers_and_an_empty_roster(self):
        self.assertEqual(1, module.main(["--check-readiness"]))
        with tempfile.TemporaryDirectory() as directory:
            requirements = Path(directory) / "requirements.json"
            requirements.write_text(json.dumps({**MIRROR, "requirements": []}))
            self.assertEqual(1, module.main([
                "--requirements", str(requirements), "--check-readiness"]))

    def test_readiness_cannot_replace_release_certification(self):
        with self.assertRaises(SystemExit) as raised:
            module.main(["--mode", "release", "--check-readiness"])
        self.assertIn("only valid in contract mode", str(raised.exception))

    def test_contract_readiness_rejects_stale_and_unjoinable_bindings(self):
        ready_row = next(row for row, verdict in zip(
            MIRROR["requirements"], module.verify_contract(MIRROR, ROOT))
            if blocker_codes(verdict) == ["no-candidate"])
        stale = copy.deepcopy(ready_row)
        stale["receiptBinding"]["producerBinding"]["reasonCode"] = "lane-not-emitted"
        unjoinable = copy.deepcopy(ready_row)
        unjoinable["test_ids"] = []
        unjoinable["receiptBinding"]["denominatorJoin"] = {
            "status": "unjoinable", "testIds": [],
            "reasonCode": "denominator-has-no-test-ids", "reason": "No test IDs"}
        for row, reason in ((stale, "mirror-stale"), (unjoinable, "denominator-unjoinable")):
            with self.subTest(reason=reason), tempfile.TemporaryDirectory() as directory:
                requirements = Path(directory) / "requirements.json"
                output = Path(directory) / "verdict.json"
                requirements.write_text(json.dumps({**MIRROR, "requirements": [row]}))
                self.assertEqual(1, module.main([
                    "--requirements", str(requirements), "--output", str(output),
                    "--check-readiness"]))
                report = json.loads(output.read_text())
                self.assertEqual([reason], blocker_codes(report["cells"][0]))


if __name__ == "__main__":
    unittest.main()
