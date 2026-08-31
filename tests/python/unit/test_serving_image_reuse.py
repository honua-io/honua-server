import importlib.util
import json
import os
import tempfile
import unittest
from pathlib import Path
from unittest import mock


SCRIPT = Path(__file__).resolve().parents[3] / "scripts" / "ci" / "serving-image-reuse.py"
SPEC = importlib.util.spec_from_file_location("serving_image_reuse", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)

DIGEST = "a" * 64


class ServingImageReuseTests(unittest.TestCase):
    def test_disabled_is_always_a_truthful_rebuild(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "marker.json"
            path.write_text(json.dumps(MODULE.marker("generic", DIGEST)), encoding="utf-8")
            self.assertEqual(
                MODULE.decide("false", "generic", DIGEST, path),
                (False, "HONUA_SERVING_IMAGE_SKIP is not exactly true"),
            )

    def test_exact_marker_is_reusable_when_enabled(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "marker.json"
            path.write_text(json.dumps(MODULE.marker("lambda", DIGEST)), encoding="utf-8")
            skip, reason = MODULE.decide("true", "lambda", DIGEST, path)
            self.assertTrue(skip)
            self.assertIn("successful authoritative verification", reason)

    def test_digest_mismatch_rebuilds(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "marker.json"
            path.write_text(json.dumps(MODULE.marker("functions", "b" * 64)), encoding="utf-8")
            skip, reason = MODULE.decide("true", "functions", DIGEST, path)
            self.assertFalse(skip)
            self.assertIn("does not match", reason)

    def test_missing_or_malformed_marker_rebuilds(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "marker.json"
            self.assertFalse(MODULE.decide("true", "generic", DIGEST, path)[0])
            path.write_text("not json", encoding="utf-8")
            self.assertFalse(MODULE.decide("true", "generic", DIGEST, path)[0])

    def test_marker_contract_rejects_bad_inputs(self):
        with self.assertRaises(ValueError):
            MODULE.marker("worker", DIGEST)
        with self.assertRaises(ValueError):
            MODULE.marker("generic", "short")

    def test_cli_records_full_digest_decision_and_reason(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            marker = root / "marker.json"
            output = root / "output"
            summary = root / "summary"
            marker.write_text(json.dumps(MODULE.marker("generic", DIGEST)), encoding="utf-8")
            with mock.patch.dict(
                os.environ,
                {"GITHUB_OUTPUT": str(output), "GITHUB_STEP_SUMMARY": str(summary)},
                clear=False,
            ):
                result = MODULE.main(
                    [
                        "decide",
                        "--enabled",
                        "true",
                        "--variant",
                        "generic",
                        "--digest",
                        DIGEST,
                        "--marker",
                        str(marker),
                    ]
                )
            self.assertEqual(result, 0)
            self.assertIn("skip=true", output.read_text(encoding="utf-8"))
            rendered = summary.read_text(encoding="utf-8")
            self.assertIn(DIGEST, rendered)
            self.assertIn("`reuse-skip`", rendered)
            self.assertIn("successful authoritative verification", rendered)


if __name__ == "__main__":
    unittest.main()
