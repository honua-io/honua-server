from __future__ import annotations

import importlib.util
import json
import tempfile
import unittest
from argparse import Namespace
from pathlib import Path


SCRIPT = Path(__file__).with_name("validate-canonical-artifacts.py")
SPEC = importlib.util.spec_from_file_location("validate_canonical_artifacts", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def args() -> Namespace:
    return Namespace(
        source_sha="a" * 40,
        image_digest="sha256:" + "b" * 64,
        fixture_revision="fixture-v1",
        evidence_uri="https://example.test/evidence",
    )


class CanonicalArtifactEvidenceTests(unittest.TestCase):
    def test_client_failure_is_attributed_without_hiding_sibling_pass(self):
        observations = []
        MODULE._collect_client(observations, "surface", "read", "PyArrow", "pyarrow", args(), lambda: None)
        MODULE._collect_client(
            observations, "surface", "geometry-read", "GeoPandas", "geopandas", args(),
            lambda: (_ for _ in ()).throw(ValueError("bad geometry")),
        )
        self.assertEqual(["pass", "fail"], [row["result"] for row in observations])
        self.assertEqual("GeoPandas", observations[1]["canonical_client"])

    def test_unbound_transform_never_converts_failure_to_skip(self):
        rows = [{"result": "pass"}, {"result": "fail", "failure_reason": "broken"}]
        MODULE._mark_unbound(rows)
        self.assertEqual("skip", rows[0]["result"])
        self.assertEqual("fail", rows[1]["result"])

    def test_native_validator_outputs_emit_normalized_passes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "pmtiles-verify.log").write_text("Completed verify in 1ms.\n", encoding="utf-8")
            (root / "3d-tiles-validator.json").write_text(json.dumps({"numErrors": 0}), encoding="utf-8")
            rows = MODULE.validate_native_results(root, args())
        self.assertEqual(["pass", "pass"], [row["result"] for row in rows])
        self.assertEqual(["go-pmtiles", "3d-tiles-validator"], [row["canonical_client"] for row in rows])

    def test_native_3d_validator_recursively_rejects_error_severity(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "pmtiles-verify.log").write_text("Completed verify in 1ms.\n", encoding="utf-8")
            (root / "3d-tiles-validator.json").write_text(
                json.dumps({"issues": [{"severity": "ERROR", "causes": []}]}), encoding="utf-8"
            )
            rows = MODULE.validate_native_results(root, args())
        self.assertEqual("fail", rows[1]["result"])

    def test_javascript_results_preserve_each_client_verdict(self):
        payload = [
            {
                "surface": "flatgeobuf", "operation": "feature-read",
                "canonical_client": "flatgeobuf-js", "client_version": "4.3.1",
                "lane": "node-flatgeobuf", "result": "fail", "failure_reason": "bad file",
            },
            {
                "surface": "pmtiles", "operation": "browser-archive-read",
                "canonical_client": "PMTiles-browser-viewer", "client_version": "4.4.0",
                "lane": "node-pmtiles", "result": "pass",
            },
        ]
        original_run = MODULE._run
        try:
            MODULE._run = lambda *command: Namespace(stdout=json.dumps(payload))
            rows = MODULE.validate_javascript(Path("artifacts"), args())
        finally:
            MODULE._run = original_run
        self.assertEqual(["fail", "pass"], [row["result"] for row in rows])
        self.assertEqual(["flatgeobuf-js", "PMTiles-browser-viewer"], [row["canonical_client"] for row in rows])


if __name__ == "__main__":
    unittest.main()
