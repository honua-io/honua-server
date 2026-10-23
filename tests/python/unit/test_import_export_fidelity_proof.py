"""Fail-closed receipt reader checks; uses only the Python standard library."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[3] / "scripts/ci/run-import-export-fidelity-proof.py"
SPEC = importlib.util.spec_from_file_location("fidelity_proof", SCRIPT)
proof = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(proof)


class EvidenceTests(unittest.TestCase):
    def test_missing_skipped_failed_and_truncated_results_fail(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "test.trx"
            with self.assertRaises(FileNotFoundError):
                proof.read_results(path, 1)
            for outcome in ("NotExecuted", "Failed", "Unknown", ""):
                path.write_text(f'<TestRun xmlns="{proof.NS["t"]}"><Results>'
                                f'<UnitTestResult testName="proof" outcome="{outcome}" />'
                                '</Results></TestRun>')
                with self.assertRaises(ValueError):
                    proof.read_results(path, 1)
            path.write_text(f'<TestRun xmlns="{proof.NS["t"]}"><Results>'
                            '<UnitTestResult testName="proof" outcome="Passed" />'
                            '</Results></TestRun>')
            self.assertEqual(len(proof.read_results(path, 1)), 1)
            with self.assertRaises(ValueError):
                proof.read_results(path, 2)

    def test_current_measurements_must_match_complete_reviewed_denominator(self):
        baseline = json.loads((proof.ROOT / "scripts/ci/fixtures/import-export-fidelity-baseline.json").read_text())
        proof.compare(dict(baseline), baseline)
        for field in ("imports", "exports"):
            current = {**baseline, field: baseline[field][1:]}
            with self.assertRaises(ValueError):
                proof.compare(current, baseline)
        for field, value in (("measured_count", 1), ("measured_delta", 0.1)):
            current = json.loads(json.dumps(baseline))
            current["exports"][0][field] = value
            with self.assertRaises(ValueError):
                proof.compare(current, baseline)


if __name__ == "__main__":
    unittest.main()
