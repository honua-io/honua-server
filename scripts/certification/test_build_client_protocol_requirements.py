"""Tests for the frozen bounded 2026.1 external-client roster mirror.

These guard the mirror as a *projection*, not as a hand-written file: every claim it
makes about a honua-server producer is recomputed here from
``tests/baselines/client-compat/expected-pairs.json`` and the committed baseline
envelopes, and every claim it makes about the governed denominator is recomputed
from the row's own fields. A mirror that drifts from either source fails here.
"""
from __future__ import annotations

import copy
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

BUILDER = Path(__file__).with_name("build-client-protocol-requirements.py")
_SPEC = importlib.util.spec_from_file_location("build_client_protocol_requirements", BUILDER)
assert _SPEC and _SPEC.loader
module = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(module)

ROOT = module.repository_root(BUILDER.parent)
MIRROR = json.loads((ROOT / module.OUTPUT_RELATIVE_PATH).read_text(encoding="utf-8"))
EXPECTED_PAIRS = json.loads(
    (ROOT / module.EXPECTED_PAIRS_RELATIVE_PATH).read_text(encoding="utf-8"))

# The bounded roster the issue names, in the denominator's spellings, with the row
# counts the pinned denominator revision carries. Written out rather than derived
# so a silent change to the projection filter is a failure, not a new baseline.
EXPECTED_CLIENT_ROWS = {
    "QGIS": 23,
    "GDAL/OGR": 11,
    "MapLibre GL JS": 10,
    "GDAL": 8,
    "OWSLib": 6,
    "PySTAC-Client": 1,
}

GOVERNED_ROW_FIELDS = (
    "capability_key", "surface", "operation", "maturity", "canonical_client",
    "client_lane", "client_version", "deployment_target", "required_tier", "licensed",
    "addressable_by_client", "scenario_facets", "contract_revision",
    "auth_policy_revision", "fixture_revision",
)


class FrozenProfileShapeTests(unittest.TestCase):
    def test_the_mirror_is_the_bounded_roster_and_nothing_else(self):
        counts: dict[str, int] = {}
        for row in MIRROR["requirements"]:
            counts[row["canonical_client"]] = counts.get(row["canonical_client"], 0) + 1
        self.assertEqual(EXPECTED_CLIENT_ROWS, counts)
        self.assertEqual(sum(EXPECTED_CLIENT_ROWS.values()), len(MIRROR["requirements"]))

    def test_every_bounded_client_is_declared_in_the_roster_header(self):
        self.assertEqual(
            sorted(EXPECTED_CLIENT_ROWS), sorted(MIRROR["boundedRosterClients"]))
        self.assertEqual(
            sorted(module.BOUNDED_ROSTER_CLIENTS), sorted(MIRROR["boundedRosterClients"]))

    def test_every_row_names_its_operation_client_and_version(self):
        for row in MIRROR["requirements"]:
            for field in GOVERNED_ROW_FIELDS:
                self.assertIn(field, row, row.get("capability_key"))
                self.assertIsNotNone(row[field], f"{field} of {row.get('capability_key')}")
            self.assertTrue(str(row["client_version"]).strip())
            self.assertTrue(str(row["operation"]).strip())

    def test_every_cell_identity_is_unique(self):
        identities = [
            (row["surface"], row["operation"], row["canonical_client"],
             row["client_version"], row["deployment_target"])
            for row in MIRROR["requirements"]]
        self.assertEqual(len(identities), len(set(identities)))

    def test_the_mirror_is_pinned_to_an_exact_upstream_revision(self):
        self.assertRegex(MIRROR["requirements_source_revision"], r"^[0-9a-f]{40}$")
        self.assertEqual(
            "honua-io/honua-release/certification/protocol-certification-requirements.v1.json",
            MIRROR["requirements_source"])
        self.assertTrue(MIRROR["requirements_source_denominator_revision"])

    def test_delegated_clients_are_named_rather_than_silently_filtered_out(self):
        delegated = {entry["canonicalClient"] for entry in MIRROR["delegatedClients"]}
        self.assertIn("ArcGIS Pro/arcpy", delegated)
        self.assertIn("OGC CITE", delegated)
        for entry in MIRROR["delegatedClients"]:
            self.assertTrue(entry["delegation"].strip())
        self.assertFalse(delegated & set(MIRROR["boundedRosterClients"]))

    def test_the_roster_carries_only_supported_release_required_rows(self):
        for row in MIRROR["requirements"]:
            self.assertEqual("supported", row["maturity"])
            self.assertTrue(row["addressable_by_client"])


class ReceiptContractTests(unittest.TestCase):
    """The mirrored copy of what the governed consumer demands of a raw receipt."""

    contract = MIRROR["receiptContract"]

    def test_the_consumer_is_named_exactly(self):
        consumer = self.contract["consumer"]
        self.assertEqual("honua-io/honua-evidence", consumer["repository"])
        self.assertEqual("client-interop-cert-v1", consumer["normalizer"])
        self.assertEqual("honua-server-client-interop", consumer["producer"])

    def test_every_candidate_binding_the_gate_needs_is_required(self):
        required = self.contract["requiredEnvelopeFields"]
        for field in (
            "server_commit", "producer_source_sha", "image_digest", "fixture_revision",
            "server_config_revision", "auth_policy_revision", "client_id", "runner_lane",
            "client_version", "protocol", "protocol_version", "protocol_profile", "run_date",
        ):
            self.assertIn(field, required)

    def test_the_receipt_names_its_own_deployment_target(self):
        # Stricter than the consumer, deliberately: deployment_target is part of
        # the governed cell identity but the consumer reads it from the requirement.
        self.assertEqual(["deployment_target"], self.contract["honuaAdditionalReleaseFields"])

    def test_credential_bearing_query_keys_are_enumerated(self):
        keys = self.contract["credentialQueryKeys"]
        for key in ("token", "api_key", "access_token", "password"):
            self.assertIn(key, keys)
        self.assertEqual(sorted(set(keys)), sorted(keys))

    def test_every_result_must_carry_its_own_request_provenance(self):
        self.assertEqual(
            ["exercised_capabilities", "performed_by", "request_url", "status", "test_case_id"],
            sorted(self.contract["requiredResultFields"]))

    def test_the_governed_status_vocabulary_uses_the_underscore_token(self):
        # The lanes write "not-applicable"; the governed vocabulary does not contain
        # it. Recorded here so the divergence is a tracked contract fact.
        self.assertEqual(
            ["fail", "not_applicable", "pass", "skip"],
            sorted(self.contract["resultStatusVocabulary"]))
        self.assertNotIn("not-applicable", self.contract["resultStatusVocabulary"])


class ProducerBindingTests(unittest.TestCase):
    """Every producer claim recomputed from the repository's own contract."""

    @classmethod
    def setUpClass(cls):
        cls.pairs = module.emitted_pairs(ROOT)

    def test_emitted_pairs_are_exactly_the_contracted_pairs(self):
        self.assertEqual(
            sorted((pair["client_lane"], pair["protocol"])
                   for pair in EXPECTED_PAIRS["expected_pairs"]),
            sorted(self.pairs))

    def test_every_contracted_pair_has_a_committed_baseline_version(self):
        unbaselined = [key for key, value in self.pairs.items()
                       if value["envelopeClientVersion"] is None]
        self.assertEqual([], unbaselined)

    def test_stored_producer_binding_matches_a_fresh_classification(self):
        for row in MIRROR["requirements"]:
            fresh = module.classify_producer(row, self.pairs)
            stored = row["receiptBinding"]["producerBinding"]
            self.assertEqual(fresh["status"], stored["status"], row["capability_key"])
            self.assertEqual(fresh["reasonCode"], stored["reasonCode"], row["capability_key"])
            self.assertEqual(fresh["reason"], stored["reason"], row["capability_key"])

    def test_no_two_governed_rows_claim_the_same_test_id(self):
        self.assertEqual({}, module.ambiguous_test_ids(MIRROR["requirements"]))

    def test_duplicate_baselines_for_one_pair_refuse_to_generate(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            baselines = root / "tests" / "baselines" / "client-compat"
            for producer in ("first", "second"):
                (baselines / producer).mkdir(parents=True)
                (baselines / producer / f"{producer}.cert.json").write_text(json.dumps({
                    "client_lane": "py-owslib", "protocol": "ogc-features",
                    "client_version": f"0.36.{producer == 'second'}"}), encoding="utf-8")
            (baselines / "expected-pairs.json").write_text(json.dumps({
                "expected_pairs": [{"client_lane": "py-owslib", "protocol": "ogc-features"}]}),
                encoding="utf-8")

            with self.assertRaises(SystemExit) as raised:
                module.emitted_pairs(root)
            self.assertIn("duplicate baseline", str(raised.exception))

    def test_stored_denominator_join_matches_a_fresh_classification(self):
        collisions = module.ambiguous_test_ids(MIRROR["requirements"])
        for row in MIRROR["requirements"]:
            fresh = module.classify_denominator_join(row, collisions)
            stored = row["receiptBinding"]["denominatorJoin"]
            self.assertEqual(fresh["status"], stored["status"], row["capability_key"])
            self.assertEqual(fresh["reasonCode"], stored["reasonCode"], row["capability_key"])

    def test_a_row_is_implemented_only_when_both_halves_hold(self):
        for row in MIRROR["requirements"]:
            binding = row["receiptBinding"]
            expected = (
                binding["producerBinding"]["status"] == "present"
                and binding["denominatorJoin"]["status"] == "joinable")
            self.assertEqual("implemented" if expected else "absent", binding["status"])

    def test_every_absent_row_records_why(self):
        for row in MIRROR["requirements"]:
            binding = row["receiptBinding"]
            if binding["status"] != "absent":
                continue
            reasons = [
                half["reason"] for half in
                (binding["producerBinding"], binding["denominatorJoin"]) if half["reason"]]
            self.assertTrue(reasons, row["capability_key"])
            for reason in reasons:
                self.assertGreater(len(reason), 40, "an absence reason must be actionable")


class ClassificationTests(unittest.TestCase):
    """The classifier itself, exercised on constructed inputs."""

    def row(self, **overrides) -> dict:
        value = {
            "client_lane": "py-owslib", "surface": "ogc-features", "client_version": "0.36.0",
            "operation": "collections", "test_ids": ["CERT-DISC-01"],
        }
        value.update(overrides)
        return value

    def pairs(self, *entries) -> dict:
        return {
            (lane, protocol): {
                "envelopeClientLane": lane, "envelopeProtocol": protocol,
                "envelopeClientVersion": version, "baseline": f"{lane}-{protocol}.cert.json"}
            for lane, protocol, version in entries
        }

    def test_an_exact_lane_surface_and_version_is_present(self):
        result = module.classify_producer(
            self.row(), self.pairs(("py-owslib", "ogc-features", "0.36.0")))
        self.assertEqual("present", result["status"])
        self.assertIsNone(result["reasonCode"])
        self.assertEqual("py-owslib-ogc-features.cert.json", result["producer"]["baseline"])

    def test_an_unknown_lane_is_lane_not_emitted(self):
        result = module.classify_producer(
            self.row(client_lane="owslib-ogc"), self.pairs(("py-owslib", "ogc-features", "0.36.0")))
        self.assertEqual("lane-not-emitted", result["reasonCode"])
        self.assertIn("py-owslib", result["reason"])

    def test_a_known_lane_on_an_unemitted_surface_is_surface_not_emitted(self):
        result = module.classify_producer(
            self.row(surface="wms"), self.pairs(("py-owslib", "ogc-features", "0.36.0")))
        self.assertEqual("surface-not-emitted", result["reasonCode"])
        self.assertIn("ogc-features", result["reason"])

    def test_a_near_miss_version_is_a_mismatch_not_a_pass(self):
        result = module.classify_producer(
            self.row(), self.pairs(("py-owslib", "ogc-features", "0.36.1")))
        self.assertEqual("client-version-mismatch", result["reasonCode"])
        self.assertIn("'0.36.1'", result["reason"])
        self.assertIn("'0.36.0'", result["reason"])

    def test_an_unbaselined_contracted_pair_cannot_claim_a_version(self):
        result = module.classify_producer(
            self.row(), self.pairs(("py-owslib", "ogc-features", None)))
        self.assertEqual("lane-not-baselined", result["reasonCode"])

    def test_a_test_id_claimed_by_two_rivals_is_unjoinable(self):
        rows = [
            self.row(operation="collections", test_ids=["CERT-DISC-01"]),
            self.row(operation="items", test_ids=["CERT-DISC-01", "CERT-QFLT-01"]),
        ]
        for value in rows:
            value.setdefault("client_lane", "py-owslib")
        collisions = module.ambiguous_test_ids(rows)

        self.assertEqual({"CERT-DISC-01": ["collections", "items"]}, collisions)
        for value in rows:
            result = module.classify_denominator_join(value, collisions)
            self.assertEqual("unjoinable", result["status"])
            self.assertEqual("denominator-ambiguous-test-ids", result["reasonCode"])
            self.assertIn("CERT-DISC-01", result["reason"])

    def test_the_same_test_id_on_a_different_lane_or_surface_is_not_a_collision(self):
        # A receipt is already narrowed by lane, version and surface, so an ID
        # reused across them can still resolve to exactly one requirement.
        rows = [
            self.row(operation="collections"),
            self.row(operation="collections", client_lane="qgis-ogc"),
            self.row(operation="collections", surface="wfs"),
            self.row(operation="collections", client_version="0.37.0"),
        ]
        self.assertEqual({}, module.ambiguous_test_ids(rows))
        self.assertEqual(
            "joinable",
            module.classify_denominator_join(rows[0], module.ambiguous_test_ids(rows))["status"])

    def test_a_row_without_test_ids_is_unjoinable(self):
        for value in ([], None):
            result = module.classify_denominator_join(self.row(test_ids=value))
            self.assertEqual("unjoinable", result["status"])
            self.assertEqual("denominator-has-no-test-ids", result["reasonCode"])

    def test_a_row_with_test_ids_is_joinable(self):
        result = module.classify_denominator_join(self.row())
        self.assertEqual("joinable", result["status"])
        self.assertEqual(["CERT-DISC-01"], result["testIds"])


class ProjectionTests(unittest.TestCase):
    def upstream(self, *rows) -> dict:
        return {
            "schema": module.UPSTREAM_SCHEMA,
            "revision": "test-denominator.1",
            "receipt_schema_min": "v2",
            "source_revisions": {"server": {"commit": "0" * 40}},
            "requirements": list(rows),
        }

    def governed(self, **overrides) -> dict:
        value = {
            "capability_key": "serve.ogc-api-features", "surface": "ogc-features",
            "operation": "collections", "maturity": "supported", "canonical_client": "OWSLib",
            "client_lane": "py-owslib", "client_version": "0.36.0",
            "deployment_target": "local-docker", "required_tier": "nightly", "licensed": False,
            "addressable_by_client": True, "scenario_facets": ["positive"],
            "contract_revision": "config-v1", "auth_policy_revision": "anonymous-v1",
            "fixture_revision": "seed.sql@{source_sha}",
        }
        value.update(overrides)
        return value

    def test_rows_outside_the_bounded_roster_are_dropped(self):
        projection = module.project(
            self.upstream(self.governed(), self.governed(canonical_client="ArcGIS Pro/arcpy")),
            "a" * 40, "test.1", ROOT)
        self.assertEqual(1, len(projection["requirements"]))
        self.assertEqual("OWSLib", projection["requirements"][0]["canonical_client"])
        self.assertEqual(
            ["ArcGIS Pro/arcpy"],
            [entry["canonicalClient"] for entry in projection["delegatedClients"]])

    def test_governed_fields_are_mirrored_verbatim(self):
        governed = self.governed()
        projection = module.project(self.upstream(governed), "a" * 40, "test.1", ROOT)
        row = projection["requirements"][0]
        for field, value in governed.items():
            self.assertEqual(value, row[field], field)

    def test_a_short_upstream_revision_is_refused(self):
        with self.assertRaises(SystemExit):
            module.project(self.upstream(self.governed()), "abc123", "test.1", ROOT)

    def test_a_foreign_upstream_schema_is_refused(self):
        upstream = {**self.upstream(self.governed()), "schema": "something.else/v1"}
        with self.assertRaises(SystemExit):
            module.project(upstream, "a" * 40, "test.1", ROOT)

    def test_an_empty_projection_is_refused_rather_than_published(self):
        with self.assertRaises(SystemExit):
            module.project(
                self.upstream(self.governed(canonical_client="Honua CLI")),
                "a" * 40, "test.1", ROOT)

    def test_the_projection_is_deterministic(self):
        upstream = self.upstream(
            self.governed(), self.governed(surface="wfs", operation="getfeature"))
        first = module.project(upstream, "a" * 40, "test.1", ROOT)
        second = module.project(copy.deepcopy(upstream), "a" * 40, "test.1", ROOT)
        self.assertEqual(json.dumps(first, sort_keys=True), json.dumps(second, sort_keys=True))

    def test_the_generator_writes_the_file_it_projects(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "mirror.json"
            upstream_path = Path(directory) / "upstream.json"
            upstream_path.write_text(json.dumps(self.upstream(self.governed())), encoding="utf-8")
            self.assertEqual(0, module.main([
                "--upstream", str(upstream_path), "--upstream-revision", "a" * 40,
                "--revision", "test.1", "--root", str(ROOT), "--output", str(output)]))
            written = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual(module.SCHEMA, written["schema"])
            self.assertEqual(1, len(written["requirements"]))


if __name__ == "__main__":
    unittest.main()
