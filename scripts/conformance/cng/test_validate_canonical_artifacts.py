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

    def test_third_party_produced_artifact_can_never_pass(self):
        """#4398: the COG cell validates rio_cogeo's own output, so however clean the
        canonical client read is, the row cannot be Honua cloud-native evidence."""
        started = "2026-08-21T00:00:00Z"
        row = MODULE._observation(
            "cog", "window-read", "Rasterio", "diagnostic", started, args()
        )
        # Even with every declared metadata oracle satisfied, the producer bars a pass.
        row["observed_metadata"] = dict(MODULE.FORMAT_BUDGET_PROFILES["cog"]["expected_metadata"])

        normalized = MODULE._normalize_observations([row], args())

        self.assertEqual("skip", normalized[0]["result"])
        self.assertEqual(MODULE.NON_HONUA_PRODUCER_GAP, normalized[0]["skip_reason"])
        self.assertFalse(normalized[0]["honua_in_loop"])
        self.assertEqual("third-party-fixture", normalized[0]["artifact_producer"])
        self.assertIsNone(normalized[0]["evidence_receipt"])

    def test_scope_disposition_names_the_third_party_cells(self):
        """The fragment's own disposition must state which cells cannot support the
        claim, so a downstream GA citation cannot read it as cloud-native proof."""
        rows = [
            {"surface": "cog", "result": "skip", "honua_in_loop": False},
            {"surface": "zarr", "result": "skip", "honua_in_loop": False},
            {"surface": "pmtiles", "result": "pass", "honua_in_loop": True},
        ]

        disposition = MODULE._scope_disposition(rows)

        self.assertIn("1 of 3", disposition)
        self.assertIn("cog", disposition)
        self.assertIn("zarr", disposition)
        self.assertIn("not by Honua", disposition)

    def test_every_governed_surface_declares_a_producer(self):
        for surface, _operation, _client in MODULE.GOVERNED_ASSIGNMENTS:
            self.assertIn(
                surface,
                MODULE.ARTIFACT_PRODUCERS,
                f"surface '{surface}' has no declared artifact producer",
            )

    def test_unmeasured_budget_names_the_oracle_it_could_not_prove(self):
        """The blanket BUDGET_EVIDENCE_GAP rewrite is retired: a cell that measured
        nothing now says which oracle is unproven, per cell."""
        started = "2026-08-21T00:00:00Z"
        row = MODULE._observation(
            "pmtiles", "archive-read", "pmtiles", "python-pmtiles", started, args()
        )

        normalized = MODULE._normalize_observations([row], args())

        self.assertEqual("skip", normalized[0]["result"])
        self.assertIn("no metadata was read back", normalized[0]["skip_reason"])
        self.assertFalse(normalized[0]["budget_results"]["met"])
        self.assertIsNone(normalized[0]["evidence_digest"])

    def test_metadata_mismatch_is_reported_per_key_and_blocks_the_pass(self):
        started = "2026-08-21T00:00:00Z"
        row = MODULE._observation(
            "pmtiles", "archive-read", "pmtiles", "python-pmtiles", started, args()
        )
        expected = MODULE.FORMAT_BUDGET_PROFILES["pmtiles-range"]["expected_metadata"]
        row["observed_metadata"] = dict(expected) | {"tile_count": 20}

        normalized = MODULE._normalize_observations([row], args())

        self.assertEqual("skip", normalized[0]["result"])
        self.assertIn("tile_count", normalized[0]["skip_reason"])
        self.assertIn("expected 21", normalized[0]["skip_reason"])

    def test_measured_honua_observation_passes_and_carries_a_digest(self):
        """The retirement half of the fix: a Honua-produced cell that met every declared
        oracle now passes with a real evidence digest, instead of being rewritten to skip
        along with every other row."""
        started = "2026-08-21T00:00:00Z"
        row = MODULE._observation(
            "pmtiles", "archive-read", "pmtiles", "python-pmtiles", started, args()
        )
        row["observed_metadata"] = dict(
            MODULE.FORMAT_BUDGET_PROFILES["pmtiles-range"]["expected_metadata"]
        )
        row["observed_transfer"] = {
            "requests": 3,
            "transferred_bytes": 20_480,
            "range_requests": 3,
            "full_object_downloads": 0,
        }

        normalized = MODULE._normalize_observations([row], args())

        self.assertEqual("pass", normalized[0]["result"], normalized[0].get("skip_reason"))
        self.assertTrue(normalized[0]["honua_in_loop"])
        self.assertTrue(normalized[0]["budget_results"]["met"])
        self.assertEqual(args().evidence_digest, normalized[0]["evidence_digest"])
        self.assertIsNotNone(normalized[0]["facet_results"])

    def test_range_efficiency_facet_requires_measured_transfer(self):
        """`min_range_requests` / `max_full_object_downloads` are the declared
        range-efficiency budgets; nothing counted requests or bytes before #4398."""
        started = "2026-08-21T00:00:00Z"
        identity = ("pmtiles", "archive-read", "pmtiles")
        assignment = MODULE.GOVERNED_ASSIGNMENTS[identity]
        self.assertIn("range-efficiency", assignment.facets)

        row = MODULE._observation(*identity, "python-pmtiles", started, args())
        row["observed_metadata"] = dict(
            MODULE.FORMAT_BUDGET_PROFILES[assignment.budget_profile]["expected_metadata"]
        )

        unmeasured = MODULE._normalize_observations([dict(row)], args())
        self.assertEqual("skip", unmeasured[0]["result"])
        self.assertIn("range-efficiency", unmeasured[0]["skip_reason"])

        # A full-object download is exactly what "range-efficient cloud-native access"
        # is supposed to exclude, so the budget must reject it.
        overspent = dict(row)
        overspent["observed_transfer"] = {
            "requests": 3,
            "transferred_bytes": 20_480,
            "range_requests": 0,
            "full_object_downloads": 9,
        }
        blocked = MODULE._normalize_observations([overspent], args())[0]
        self.assertEqual("skip", blocked["result"])
        self.assertIn("full-object downloads", blocked["skip_reason"])
        self.assertIn("range requests", blocked["skip_reason"])

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

    def test_selftest_and_lane_do_not_share_a_concurrency_group(self):
        """#4479: a push to trunk touching the lane paths shares `refs/heads/trunk` with
        the scheduled run. With one group and cancel-in-progress the cheap self-test
        cancelled the heavyweight lane and, because the `cng` job is schedule/dispatch
        only, produced no conformance result to replace it."""
        workflow = (SCRIPT.parents[3] / ".github" / "workflows" / "cng-conformance.yml").read_text(
            encoding="utf-8"
        )
        group = next(
            line.strip() for line in workflow.splitlines() if line.strip().startswith("group:")
        )
        self.assertNotEqual("group: cng-conformance-${{ github.ref }}", group)
        self.assertIn("github.event_name == 'schedule'", group)
        self.assertIn("github.event_name == 'workflow_dispatch'", group)
        self.assertIn("'lane'", group)
        self.assertIn("'selftest'", group)


# The exact `geo` metadata `GeoParquetFeatureWriter.BuildGeoParquetMetadata` emits for a
# point layer served as EPSG:4326: no `crs` field (the GeoParquet default is OGC:CRS84)
# and no metadata `bbox` — a `covering` descriptor addressing the physical bbox struct
# column instead.
EMITTED_GEOPARQUET_METADATA = {
    "version": "1.1.0",
    "primary_column": "geometry",
    "columns": {
        "geometry": {
            "encoding": "WKB",
            "geometry_types": ["Point"],
            "covering": {
                "bbox": {
                    "xmin": ["bbox", "xmin"],
                    "ymin": ["bbox", "ymin"],
                    "xmax": ["bbox", "xmax"],
                    "ymax": ["bbox", "ymax"],
                }
            },
        }
    },
}

# One per-row bbox for each of the six fixture points, as the physical struct column
# holds them. Their aggregate is the profile's declared extent.
FIXTURE_BBOX_COLUMN = {
    "xmin": [-122.4194, 0.0, 179.5, 13.0, -70.0, 100.0],
    "ymin": [37.7749, 0.0, 86.0, 52.5, 40.0, 1.3],
    "xmax": [-122.4194, 0.0, 179.5, 13.0, -70.0, 100.0],
    "ymax": [37.7749, 0.0, 86.0, 52.5, 40.0, 1.3],
}


def read_fixture_covering(parts):
    """Stands in for the Arrow column read so the aggregation is testable without pyarrow."""
    if list(parts)[:1] != ["bbox"]:
        return None
    return FIXTURE_BBOX_COLUMN.get(list(parts)[1])


class GeoParquetObservationTests(unittest.TestCase):
    """#4479: the observation must be read from the format Honua actually emits."""

    def test_omitted_crs_is_the_geoparquet_default_not_a_missing_crs(self):
        column = EMITTED_GEOPARQUET_METADATA["columns"]["geometry"]
        self.assertNotIn("crs", column)
        self.assertEqual("EPSG:4326", MODULE._geoparquet_column_crs(column))
        self.assertEqual("EPSG:4326", MODULE._geoparquet_column_crs({"crs": "OGC:CRS84"}))

    def test_explicit_crs_still_resolves_to_its_authority_code(self):
        projjson = {"id": {"authority": "EPSG", "code": 3857}}
        self.assertEqual("EPSG:3857", MODULE._geoparquet_column_crs({"crs": projjson}))
        self.assertIsNone(MODULE._geoparquet_column_crs({"crs": None}))

    def test_bounds_aggregate_the_declared_covering_column(self):
        column = EMITTED_GEOPARQUET_METADATA["columns"]["geometry"]
        self.assertNotIn("bbox", column)
        self.assertEqual(
            [-122.4194, 0.0, 179.5, 86.0],
            MODULE._geoparquet_bounds(column, read_fixture_covering),
        )

    def test_bounds_fall_back_to_a_metadata_bbox_when_no_covering_is_declared(self):
        column = {"encoding": "WKB", "bbox": [-1.0, -2.0, 3.0, 4.0]}
        self.assertEqual(
            [-1.0, -2.0, 3.0, 4.0],
            MODULE._geoparquet_bounds(column, lambda parts: None),
        )

    def test_bounds_are_unobserved_when_the_covering_column_is_absent(self):
        column = EMITTED_GEOPARQUET_METADATA["columns"]["geometry"]
        self.assertEqual([], MODULE._geoparquet_bounds(column, lambda parts: None))

    def test_emitted_geoparquet_meets_its_declared_budget_and_carries_a_digest(self):
        """The regression this file exists for: before #4479 the PyArrow cell read
        `column["crs"]` and `column["bbox"]`, which the writer never emits, so the
        governed GeoParquet cell observed `None` / `[]`, always missed its oracle and
        was rewritten to `skip` with its evidence digest stripped."""
        observed = MODULE._geoparquet_metadata(
            EMITTED_GEOPARQUET_METADATA, 6, read_fixture_covering)
        started = "2026-08-21T00:00:00Z"
        row = MODULE._observation(
            "geoparquet", "feature-read", "PyArrow", "pyarrow-geoparquet", started, args()
        )
        row["result"] = "pass"
        row["observed_metadata"] = observed
        normalized = MODULE._normalize_observations([row], args())[0]

        self.assertEqual([], normalized["budget_results"]["unmet"])
        self.assertTrue(normalized["budget_results"]["met"])
        self.assertEqual("pass", normalized["result"])
        self.assertEqual("sha256:" + "c" * 64, normalized["evidence_digest"])


if __name__ == "__main__":
    unittest.main()
