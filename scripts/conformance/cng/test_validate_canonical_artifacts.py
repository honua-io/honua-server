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
        evidence_digest="sha256:" + "c" * 64,
    )


class CanonicalArtifactEvidenceTests(unittest.TestCase):
    def test_every_governed_format_assignment_has_a_budget_profile(self):
        self.assertEqual(23, len(MODULE.GOVERNED_ASSIGNMENTS))
        for identity, assignment in MODULE.GOVERNED_ASSIGNMENTS.items():
            self.assertIn(assignment.budget_profile, MODULE.FORMAT_BUDGET_PROFILES)
            self.assertEqual(f"format.{identity[0]}", assignment.capability_key)

    def test_budget_incomplete_observation_remains_an_explicit_skip(self):
        started = "2026-08-21T00:00:00Z"
        row = MODULE._observation(
            "cog", "window-read", "Rasterio", "diagnostic", started, args()
        )

        normalized = MODULE._normalize_observations([row], args())

        self.assertEqual("skip", normalized[0]["result"])
        self.assertEqual(MODULE.BUDGET_EVIDENCE_GAP, normalized[0]["skip_reason"])
        self.assertIsNone(normalized[0]["evidence_receipt"])

    def test_fixture_generators_match_governed_shape_and_archive_contract(self):
        fixture_source = (SCRIPT.parent / "generate-canonical-fixtures.py").read_text(
            encoding="utf-8"
        )
        artifact_source = (SCRIPT.parent / "artifact-gen" / "Program.cs").read_text(
            encoding="utf-8"
        )

        self.assertIn("reshape(4, 8, 16)", fixture_source)
        self.assertIn('("time", "y", "x")', fixture_source)
        self.assertIn('"chunksizes": (1, 4, 4)', fixture_source)
        self.assertIn("overview_level=3", fixture_source)
        self.assertIn("nodata=-9999.0", fixture_source)
        self.assertIn("for (var z = 0; z <= 2; z++)", artifact_source)
        self.assertIn('tileContentUris: ["content/0.glb"]', artifact_source)
        self.assertIn("maxHeightMeters: 100.0", artifact_source)

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

    def test_javascript_start_time_precedes_validator_execution(self):
        events = []
        original_run = MODULE._run
        original_now = MODULE._now
        try:
            MODULE._now = lambda: events.append("now") or "2026-08-21T00:00:00Z"
            MODULE._run = lambda *command: events.append("run") or Namespace(stdout="[]")
            MODULE.validate_javascript(Path("artifacts"), args())
        finally:
            MODULE._run = original_run
            MODULE._now = original_now
        self.assertEqual(["now", "run"], events)

    def test_compose_build_secrets_have_standalone_file_defaults(self):
        compose = (SCRIPT.parents[3] / "docker" / "cng" / "compose.yml").read_text(encoding="utf-8")
        self.assertIn("HONUA_GITHUB_ACTOR_SECRET_FILE:-.empty-build-secret", compose)
        self.assertIn("HONUA_GITHUB_TOKEN_SECRET_FILE:-.empty-build-secret", compose)
        self.assertNotIn("environment: HONUA_DOCKER_GITHUB_TOKEN", compose)

    def test_standalone_harness_requires_and_resolves_github_packages_token(self):
        harness = (SCRIPT.parent / "run-cng-conformance.sh").read_text(encoding="utf-8")
        self.assertIn(
            'GITHUB_PACKAGES_TOKEN="${HONUA_DOCKER_GITHUB_TOKEN:-${GITHUB_TOKEN:-${GH_TOKEN:-}}}"',
            harness,
        )
        self.assertIn("GitHub Packages authentication is required", harness)
        self.assertNotIn("using anonymous package restore", harness)

    def test_workflow_retries_the_exact_3d_tiles_validator_install(self):
        workflow = (SCRIPT.parents[3] / ".github" / "workflows" / "cng-conformance.yml").read_text(
            encoding="utf-8"
        )
        self.assertIn("for attempt in 1 2 3", workflow)
        self.assertIn("retry npm install -g 3d-tiles-validator@0.6.1", workflow)
        self.assertNotIn("retry npm install -g 3d-tiles-validator@latest", workflow)


if __name__ == "__main__":
    unittest.main()
