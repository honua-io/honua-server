"""Tests for the bounded 2026.1 external-client certification verdict.

Two kinds of evidence live here.

*Real fixture, independently computed expectation.* The contract-tier and
release-tier suites run against the committed frozen mirror
(``certification/client-protocol-requirements.v1.json``) and the committed baseline
envelopes under ``tests/baselines/client-compat/``. The expected verdict is not a
snapshot of what the verifier prints: it is recomputed here from the raw governed
denominator fields and the raw ``expected-pairs.json`` contract, and asserted cell
by cell, including the exact client versions and surfaces involved.

*Constructed receipts for every fail-closed path.* One test per reason code, each
built from a receipt that satisfies every other rule, so a test that goes green
proves the named rule is the thing that failed.
"""
from __future__ import annotations

import copy
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

VERIFIER = Path(__file__).with_name("verify-client-certification-receipts.py")
_SPEC = importlib.util.spec_from_file_location("verify_client_certification_receipts", VERIFIER)
assert _SPEC and _SPEC.loader
module = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(module)

ROOT = module.repository_root(VERIFIER.parent)
MIRROR = json.loads((ROOT / module.REQUIREMENTS_RELATIVE_PATH).read_text(encoding="utf-8"))
EXPECTED_PAIRS = json.loads(
    (ROOT / "tests/baselines/client-compat/expected-pairs.json").read_text(encoding="utf-8"))

CANDIDATE_SHA = "a" * 40
PRODUCER_SHA = "c" * 40
CANDIDATE_DIGEST = "sha256:" + "b" * 64


def candidate(**overrides) -> dict:
    value = {
        "source_sha": CANDIDATE_SHA,
        "image_digest": CANDIDATE_DIGEST,
        "cut_at": module.parse_timestamp("2026-09-01T00:00:00Z", "cut_at"),
        "producer_source_sha": PRODUCER_SHA,
    }
    value.update(overrides)
    return value


def requirement(**overrides) -> dict:
    """One governed row, shaped exactly like the denominator's rows."""
    value = {
        "capability_key": "serve.ogc-api-features",
        "surface": "ogc-features",
        "operation": "collections",
        "maturity": "supported",
        "canonical_client": "OWSLib",
        "client_lane": "py-owslib",
        "client_version": "0.36.0",
        "deployment_target": "local-docker",
        "required_tier": "nightly",
        "licensed": False,
        "addressable_by_client": True,
        "scenario_facets": ["positive"],
        "contract_revision": "config-v1",
        "auth_policy_revision": "anonymous-v1",
        "fixture_revision": "docker/client-compat/seed.sql@{source_sha}",
        "test_ids": ["CERT-DISC-01"],
        "receiptBinding": {
            "status": "implemented",
            "producerBinding": {"status": "present", "producer": None,
                                "reasonCode": None, "reason": None},
            "denominatorJoin": {"status": "joinable", "testIds": ["CERT-DISC-01"],
                                "reasonCode": None, "reason": None},
        },
    }
    value.update(overrides)
    return value


def requirements_document(*rows) -> dict:
    return {
        "schema": module.SCHEMA,
        "revision": "test-revision.1",
        "requirements_source_revision": "d" * 40,
        "requirements_source_denominator_revision": "test-denominator.1",
        "receiptContract": copy.deepcopy(MIRROR["receiptContract"]),
        "requirements": [copy.deepcopy(row) for row in rows],
    }


def envelope(**overrides) -> dict:
    """A receipt that satisfies every governed rule for ``requirement()``."""
    value = {
        "schema_version": "1.0",
        "run_id": "20260902T101500Z",
        "run_date": "2026-09-02T10:15:00Z",
        "server_commit": CANDIDATE_SHA,
        "producer_source_sha": PRODUCER_SHA,
        "image_digest": CANDIDATE_DIGEST,
        "fixture_revision": f"docker/client-compat/seed.sql@{CANDIDATE_SHA}",
        "server_config_revision": "config-v1",
        "auth_policy_revision": "anonymous-v1",
        "client_id": "OWSLib",
        "runner_lane": "py-owslib",
        "client_version": "0.36.0",
        "protocol": "ogc-features",
        "protocol_version": "1.0",
        "protocol_profile": "core",
        "environment": "local-docker",
        "deployment_target": "local-docker",
        "results": [{
            "test_case_id": "CERT-DISC-01",
            "status": "pass",
            "notes": "OWSLib discovered the governed collection.",
            "performed_by": "OWSLib",
            "request_url": "https://candidate.test/collections",
            "exercised_capabilities": ["positive"],
        }],
    }
    value.update(overrides)
    return value


def only_verdict(rows, receipts, **candidate_overrides) -> dict:
    verdicts = module.verify_release(
        requirements_document(*rows), receipts, candidate(**candidate_overrides))
    assert len(verdicts) == 1, verdicts
    return verdicts[0]


def blocker_codes(verdict: dict) -> list[str]:
    return [blocker["reason_code"] for blocker in verdict["blockers"]]


class ReleaseJoinTests(unittest.TestCase):
    def test_a_fully_governed_receipt_passes(self):
        verdict = only_verdict([requirement()], [("py-owslib-ogc-features.cert.json", envelope())])
        self.assertEqual("pass", verdict["result"])
        self.assertEqual([], verdict["blockers"])
        self.assertEqual(
            "ogc-features|collections|OWSLib|0.36.0|local-docker", verdict["cell"])

    def test_extension_results_can_satisfy_a_governed_cell(self):
        # NB-* extension IDs live in a separate array; the governed consumer reads
        # both, so a denominator row bound to one must resolve here too.
        raw = envelope(results=[], extensions=[{
            "test_case_id": "CERT-DISC-01", "status": "pass",
            "performed_by": "OWSLib", "request_url": "https://candidate.test/collections",
            "exercised_capabilities": ["positive"],
        }])
        verdict = only_verdict([requirement()], [("py-owslib-ogc-features.cert.json", raw)])
        self.assertEqual("pass", verdict["result"])


class FailClosedTests(unittest.TestCase):
    def assert_blocked(self, code: str, receipts, rows=None, **candidate_overrides):
        verdict = only_verdict(rows or [requirement()], receipts, **candidate_overrides)
        self.assertIn(code, blocker_codes(verdict), verdict)
        self.assertNotEqual("pass", verdict["result"])
        return verdict

    def test_missing_receipt_is_a_skip_not_a_pass(self):
        verdict = self.assert_blocked("missing-envelope", [])
        self.assertEqual("skip", verdict["result"])

    def test_wrong_lane_does_not_satisfy_the_cell(self):
        raw = envelope(runner_lane="py-geopandas")
        self.assert_blocked("missing-envelope", [("other.cert.json", raw)])

    def test_wrong_client_version_does_not_satisfy_the_cell(self):
        raw = envelope(client_version="0.35.0")
        self.assert_blocked("missing-envelope", [("other.cert.json", raw)])

    def test_wrong_surface_does_not_satisfy_the_cell(self):
        raw = envelope(protocol="wfs")
        self.assert_blocked("missing-envelope", [("other.cert.json", raw)])

    def test_a_skipped_cell_fails_closed(self):
        raw = envelope(results=[{
            **envelope()["results"][0], "status": "skip", "request_url": None}])
        verdict = self.assert_blocked("cell-skipped", [("a.cert.json", raw)])
        self.assertEqual("skip", verdict["result"])

    def test_a_failed_cell_fails(self):
        raw = envelope(results=[{**envelope()["results"][0], "status": "fail"}])
        verdict = self.assert_blocked("cell-failed", [("a.cert.json", raw)])
        self.assertEqual("fail", verdict["result"])

    def test_a_source_built_server_cannot_certify(self):
        raw = envelope(image_digest="local-build")
        self.assert_blocked("source-built-candidate", [("a.cert.json", raw)])

    def test_an_unresolvable_server_commit_is_source_built(self):
        raw = envelope(server_commit="1.0.0+local")
        self.assert_blocked("source-built-candidate", [("a.cert.json", raw)])

    def test_a_different_image_digest_is_a_mismatch(self):
        raw = envelope(image_digest="sha256:" + "e" * 64)
        self.assert_blocked("candidate-digest-mismatch", [("a.cert.json", raw)])

    def test_a_different_server_commit_is_a_mismatch(self):
        raw = envelope(server_commit="f" * 40)
        self.assert_blocked("candidate-sha-mismatch", [("a.cert.json", raw)])

    def test_an_untrusted_producer_sha_is_rejected(self):
        raw = envelope(producer_source_sha="9" * 40)
        self.assert_blocked("producer-sha-mismatch", [("a.cert.json", raw)])

    def test_an_observation_before_the_cut_is_stale(self):
        raw = envelope(run_date="2026-08-31T23:59:59Z")
        self.assert_blocked("stale-observation", [("a.cert.json", raw)])

    def test_a_foreign_fixture_revision_is_rejected(self):
        raw = envelope(fixture_revision="sha256:" + "1" * 64)
        self.assert_blocked("revision-mismatch", [("a.cert.json", raw)])

    def test_a_foreign_auth_policy_revision_is_rejected(self):
        raw = envelope(auth_policy_revision="anonymous-v2")
        self.assert_blocked("revision-mismatch", [("a.cert.json", raw)])

    def test_a_missing_envelope_binding_is_named(self):
        raw = envelope()
        del raw["auth_policy_revision"]
        verdict = self.assert_blocked("receipt-field-missing", [("a.cert.json", raw)])
        self.assertIn("auth_policy_revision", verdict["blockers"][0]["reason"])

    def test_the_hyphenated_not_applicable_token_is_not_governed(self):
        # The lanes write "not-applicable"; the governed vocabulary is
        # "not_applicable". The consumer rejects the whole receipt rather than
        # reinterpreting the token, so this must never be silently tolerated.
        raw = envelope(results=[{**envelope()["results"][0], "status": "not-applicable"}])
        self.assert_blocked("status-not-governed", [("a.cert.json", raw)])

    def test_a_result_without_request_provenance_is_rejected(self):
        raw = envelope(results=[{
            key: value for key, value in envelope()["results"][0].items()
            if key != "request_url"}])
        self.assert_blocked("provenance-missing", [("a.cert.json", raw)])

    def test_a_generic_probe_cannot_stand_in_for_the_client(self):
        raw = envelope(results=[{**envelope()["results"][0], "performed_by": "curl"}])
        self.assert_blocked("provenance-missing", [("a.cert.json", raw)])

    def test_a_credentialled_request_url_is_rejected(self):
        raw = envelope(results=[{
            **envelope()["results"][0], "request_url": "https://u:p@candidate.test/collections"}])
        self.assert_blocked("provenance-missing", [("a.cert.json", raw)])

    def test_a_pass_cannot_claim_facets_it_did_not_exercise(self):
        row = requirement(scenario_facets=["positive", "invalid-credential"])
        self.assert_blocked("facets-not-exercised", [("a.cert.json", envelope())], rows=[row])

    def test_two_producers_claiming_one_cell_are_ambiguous(self):
        receipts = [("a.cert.json", envelope()), ("b.cert.json", envelope())]
        verdict = self.assert_blocked("ambiguous-cell", receipts)
        self.assertEqual("fail", verdict["result"])

    def test_a_row_without_test_ids_can_never_be_joined(self):
        row = requirement(test_ids=[])
        row["receiptBinding"]["denominatorJoin"] = {
            "status": "unjoinable", "testIds": [],
            "reasonCode": "denominator-has-no-test-ids", "reason": "no test_ids"}
        verdict = self.assert_blocked(
            "denominator-unjoinable", [("a.cert.json", envelope())], rows=[row])
        self.assertEqual("skip", verdict["result"])

    def test_every_defect_is_reported_not_just_the_first(self):
        raw = envelope(image_digest="sha256:" + "e" * 64, auth_policy_revision="anonymous-v2")
        verdict = only_verdict([requirement()], [("a.cert.json", raw)])
        self.assertEqual(
            ["candidate-digest-mismatch", "revision-mismatch"], sorted(blocker_codes(verdict)))


class WholeReceiptAdmissionTests(unittest.TestCase):
    """The consumer refuses an entire receipt on one bad row; so does this."""

    def test_a_valid_result_cannot_be_salvaged_from_a_rejected_receipt(self):
        # The receipt carries a perfectly good result for the governed cell *and* a
        # second result with an ungoverned status. Joining only the matching result
        # would certify the cell from evidence the consumer would have thrown away.
        raw = envelope(results=[
            envelope()["results"][0],
            {**envelope()["results"][0], "test_case_id": "CERT-CONN-01",
             "status": "not-applicable"},
        ])
        row = requirement(test_ids=["CERT-DISC-01", "CERT-CONN-01"])
        verdict = only_verdict([row], [("a.cert.json", raw)])

        self.assertEqual("fail", verdict["result"])
        self.assertIn("status-not-governed", blocker_codes(verdict))
        self.assertIn("the whole receipt was refused", verdict["blockers"][0]["reason"])

    def test_an_off_candidate_receipt_is_refused_before_any_cell_joins(self):
        raw = envelope(image_digest="sha256:" + "e" * 64)
        verdict = only_verdict([requirement()], [("a.cert.json", raw)])

        self.assertEqual("fail", verdict["result"])
        self.assertIn("candidate-digest-mismatch", blocker_codes(verdict))

    def test_admission_reports_every_defect_in_the_receipt(self):
        raw = envelope(image_digest="sha256:" + "e" * 64, auth_policy_revision="anonymous-v2")
        _, rejected = module.admit_receipts(
            requirements_document(requirement()), [("a.cert.json", raw)], candidate())

        codes = sorted(defect.partition(": ")[0] for defect in rejected["a.cert.json"])
        self.assertEqual(["candidate-digest-mismatch", "revision-mismatch"], codes)

    def test_a_result_outside_the_bounded_roster_does_not_reject_the_receipt(self):
        # The bounded roster is a subset of the denominator. A lane legitimately
        # emits IDs this gate does not govern; they are judged upstream, not here.
        raw = envelope(results=[
            envelope()["results"][0],
            {"test_case_id": "CERT-QFLT-01", "status": "pass", "performed_by": "OWSLib",
             "request_url": "https://candidate.test/items?limit=1",
             "exercised_capabilities": ["positive"]},
        ])
        verdict = only_verdict([requirement()], [("a.cert.json", raw)])
        self.assertEqual("pass", verdict["result"])

    def test_a_result_claiming_two_governed_rows_is_ambiguous(self):
        rows = [
            requirement(),
            requirement(operation="items", capability_key="serve.ogc-api-features.items"),
        ]
        _, rejected = module.admit_receipts(
            requirements_document(*rows), [("a.cert.json", envelope())], candidate())

        self.assertIn("a.cert.json", rejected)
        self.assertTrue(any(
            defect.startswith("ambiguous-resolution") for defect in rejected["a.cert.json"]))


class DeploymentTargetTests(unittest.TestCase):
    def test_a_receipt_from_another_execution_context_cannot_certify(self):
        raw = envelope(deployment_target="cloud-aws")
        verdict = only_verdict([requirement()], [("a.cert.json", raw)])

        self.assertEqual("fail", verdict["result"])
        self.assertEqual(["deployment-target-mismatch"], blocker_codes(verdict))
        self.assertIn("cloud-aws", verdict["blockers"][0]["reason"])

    def test_a_receipt_that_omits_its_target_is_not_admitted(self):
        raw = envelope()
        del raw["deployment_target"]
        verdict = only_verdict([requirement()], [("a.cert.json", raw)])

        self.assertEqual("fail", verdict["result"])
        self.assertIn("receipt-field-missing", blocker_codes(verdict))


class ReceiptIdentityTests(unittest.TestCase):
    def test_same_named_receipts_in_different_directories_stay_distinct(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for producer in ("run-a", "run-b"):
                (root / producer).mkdir()
                (root / producer / "py-owslib-ogc-features.cert.json").write_text(
                    json.dumps(envelope()), encoding="utf-8")

            receipts = module.read_receipts(root)
            self.assertEqual(
                ["run-a/py-owslib-ogc-features.cert.json",
                 "run-b/py-owslib-ogc-features.cert.json"],
                sorted(name for name, _ in receipts))

            verdict = only_verdict([requirement()], receipts)
            self.assertEqual("fail", verdict["result"])
            self.assertEqual(["ambiguous-cell"], blocker_codes(verdict))


class CredentialUrlTests(unittest.TestCase):
    def test_a_plain_url_is_publishable(self):
        self.assertTrue(module.is_publishable_url("https://candidate.test/collections?limit=10"))

    def test_userinfo_credentials_are_rejected(self):
        self.assertFalse(module.is_publishable_url("https://user:secret@candidate.test/"))

    def test_query_string_credentials_are_rejected(self):
        for url in (
            "https://candidate.test/items?token=secret",
            "https://candidate.test/items?API_KEY=secret",
            "https://candidate.test/items?limit=1&access_token=secret",
        ):
            self.assertFalse(module.is_publishable_url(url), url)

    def test_a_query_credential_blocks_the_cell(self):
        raw = envelope(results=[{
            **envelope()["results"][0],
            "request_url": "https://candidate.test/collections?api_key=secret"}])
        verdict = only_verdict([requirement()], [("a.cert.json", raw)])

        self.assertEqual("fail", verdict["result"])
        self.assertIn("provenance-missing", blocker_codes(verdict))


class ReasonCodeVocabularyTests(unittest.TestCase):
    def test_reason_codes_are_closed_and_unique(self):
        self.assertEqual(len(module.REASON_CODES), len(set(module.REASON_CODES)))

    def test_an_unknown_reason_code_cannot_be_emitted(self):
        with self.assertRaises(ValueError):
            module._blocker("invented-code", "why", "honua-io/honua-server")


class RealMirrorContractTierTests(unittest.TestCase):
    """The committed mirror, verified against the real repository."""

    @classmethod
    def setUpClass(cls):
        cls.verdicts = module.verify_contract(MIRROR, ROOT)
        cls.by_cell = {verdict["cell"]: verdict for verdict in cls.verdicts}

    def test_the_bounded_roster_is_exactly_the_fifty_nine_governed_rows(self):
        self.assertEqual(59, len(self.verdicts))
        counts: dict[str, int] = {}
        for verdict in self.verdicts:
            counts[verdict["canonical_client"]] = counts.get(verdict["canonical_client"], 0) + 1
        self.assertEqual(
            {"QGIS": 23, "GDAL/OGR": 11, "MapLibre GL JS": 10, "GDAL": 8,
             "OWSLib": 6, "PySTAC-Client": 1},
            counts)

    def test_no_cell_is_certified_and_the_gate_is_red(self):
        summary = module.summarize(self.verdicts)
        self.assertFalse(summary["green"])
        self.assertEqual(0, summary["byResult"]["pass"])
        self.assertEqual(59, summary["byResult"]["skip"])

    def test_the_mirror_is_not_stale_against_the_repository(self):
        stale = [v["cell"] for v in self.verdicts if "mirror-stale" in blocker_codes(v)]
        self.assertEqual([], stale, "re-run build-client-protocol-requirements.py")

    def test_every_governed_row_is_blocked_on_the_missing_denominator_test_ids(self):
        # Recomputed from the governed rows themselves, not from the stored binding.
        expected = [
            module.cell_id(row) for row in MIRROR["requirements"] if not row.get("test_ids")]
        blocked = [
            v["cell"] for v in self.verdicts if "denominator-unjoinable" in blocker_codes(v)]
        self.assertEqual(59, len(expected))
        self.assertEqual(sorted(expected), sorted(blocked))

    def test_producer_absence_matches_the_lanes_this_repository_contracts_to_emit(self):
        # Independent recomputation straight from expected-pairs.json: a governed
        # pair this repository does not emit must be reported as an absent lane or
        # surface, and a pair it does emit must be reported as a version mismatch.
        emitted = {(pair["client_lane"], pair["protocol"])
                   for pair in EXPECTED_PAIRS["expected_pairs"]}
        emitted_lanes = {lane for lane, _ in emitted}

        expected: dict[str, str] = {}
        for row in MIRROR["requirements"]:
            pair = (row["client_lane"], row["surface"])
            if row["client_lane"] not in emitted_lanes:
                expected[module.cell_id(row)] = "producer-lane-not-emitted"
            elif pair not in emitted:
                expected[module.cell_id(row)] = "producer-surface-not-emitted"
            else:
                expected[module.cell_id(row)] = "producer-client-version-mismatch"

        reported = {
            verdict["cell"]: blocker["reason_code"]
            for verdict in self.verdicts for blocker in verdict["blockers"]
            if blocker["reason_code"].startswith("producer-")}
        self.assertEqual(expected, reported)

    def test_the_producer_half_is_owned_here_and_the_denominator_half_is_not(self):
        owners = {blocker["reason_code"]: blocker["owner"]
                  for verdict in self.verdicts for blocker in verdict["blockers"]}
        self.assertEqual("honua-io/honua-release", owners["denominator-unjoinable"])
        for code, owner in owners.items():
            if code.startswith("producer-"):
                self.assertEqual("honua-io/honua-server", owner)

    def test_the_only_governed_pairs_this_repository_emits_are_the_two_qgis_vector_lanes(self):
        emitted = {(pair["client_lane"], pair["protocol"])
                   for pair in EXPECTED_PAIRS["expected_pairs"]}
        governed_pairs_emitted = sorted(
            (row["client_lane"], row["surface"]) for row in MIRROR["requirements"]
            if (row["client_lane"], row["surface"]) in emitted)
        self.assertEqual(
            [("desktop-qgis", "ogc-features"), ("desktop-qgis", "wfs")], governed_pairs_emitted)

    def test_those_two_pairs_are_still_blocked_on_an_exact_client_version(self):
        # The governed rows pin QGIS 3.40; the lane image reports 3.44.13-Solothurn.
        # The normalizer matches client_version exactly, so these are not near-misses.
        for surface in ("ogc-features", "wfs"):
            row = next(r for r in MIRROR["requirements"]
                       if r["client_lane"] == "desktop-qgis" and r["surface"] == surface)
            self.assertEqual("3.40", row["client_version"])
            binding = row["receiptBinding"]["producerBinding"]
            self.assertEqual("client-version-mismatch", binding["reasonCode"])
            self.assertIn("3.44.13-Solothurn", binding["reason"])

    def test_the_governed_qgis_raster_surfaces_have_no_lane_at_all(self):
        for surface in ("wms", "wmts"):
            row = next(r for r in MIRROR["requirements"]
                       if r["client_lane"] == "desktop-qgis" and r["surface"] == surface)
            self.assertEqual(
                "surface-not-emitted", row["receiptBinding"]["producerBinding"]["reasonCode"])

    def test_a_lane_that_starts_emitting_a_governed_pair_makes_the_mirror_stale(self):
        # Guards the recomputation itself: the contract tier must not simply echo
        # the stored binding, or a producer landing without a mirror refresh would
        # silently keep reporting the old reason.
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "Honua.sln").write_text("", encoding="utf-8")
            (root / "scripts" / "certification").mkdir(parents=True)
            (root / "scripts" / "certification" / "build-client-protocol-requirements.py").write_bytes(
                (ROOT / "scripts" / "certification"
                 / "build-client-protocol-requirements.py").read_bytes())
            baselines = root / "tests" / "baselines" / "client-compat"
            (baselines / "pystac").mkdir(parents=True)
            (baselines / "expected-pairs.json").write_text(json.dumps({
                "expected_pairs": [{"client_lane": "pystac-client-stac", "protocol": "stac"}]}),
                encoding="utf-8")
            (baselines / "pystac" / "py-pystac-stac.cert.json").write_text(json.dumps({
                "client_lane": "pystac-client-stac", "protocol": "stac",
                "client_version": "0.9.0"}), encoding="utf-8")

            row = next(r for r in MIRROR["requirements"]
                       if r["client_lane"] == "pystac-client-stac")
            document = {**MIRROR, "requirements": [copy.deepcopy(row)]}
            verdict = module.verify_contract(document, root)[0]
            self.assertEqual("fail", verdict["result"])
            self.assertIn("mirror-stale", blocker_codes(verdict))


class RealBaselinesReleaseTierTests(unittest.TestCase):
    """The committed baseline envelopes, run through the real release join."""

    @classmethod
    def setUpClass(cls):
        cls.receipts = module.read_receipts(ROOT / "tests" / "baselines" / "client-compat")
        cls.verdicts = module.verify_release(MIRROR, cls.receipts, candidate())

    def test_the_committed_baselines_are_real_envelopes(self):
        self.assertEqual(27, len(self.receipts))

    def test_no_committed_baseline_certifies_any_governed_cell(self):
        summary = module.summarize(self.verdicts)
        self.assertFalse(summary["green"])
        self.assertEqual(0, summary["byResult"]["pass"])
        self.assertEqual(59, summary["byResult"]["skip"])

    def test_todays_baselines_are_blocked_on_the_denominator_before_anything_else(self):
        codes = {code for verdict in self.verdicts for code in blocker_codes(verdict)}
        self.assertEqual({"denominator-unjoinable"}, codes)


class CommandLineTests(unittest.TestCase):
    def test_contract_mode_exits_non_zero_while_the_roster_is_red(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "verdict.json"
            self.assertEqual(1, module.main(["--output", str(output)]))
            report = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual("honua.client-certification-verdict/v1", report["schema"])
            self.assertEqual("contract", report["mode"])
            self.assertIsNone(report["candidate"])
            self.assertEqual(59, report["summary"]["requiredCells"])

    def test_release_mode_refuses_to_run_without_an_exact_candidate(self):
        with self.assertRaises(SystemExit) as raised:
            module.main(["--mode", "release", "--receipts", str(ROOT)])
        self.assertIn("--source-sha", str(raised.exception))

    def test_release_mode_rejects_a_non_digest_image_reference(self):
        with self.assertRaises(SystemExit) as raised:
            module.main([
                "--mode", "release", "--receipts", str(ROOT),
                "--source-sha", CANDIDATE_SHA, "--image-digest", "honua-server:latest",
                "--cut-at", "2026-09-01T00:00:00Z", "--producer-source-sha", PRODUCER_SHA])
        self.assertIn("sha256", str(raised.exception))


if __name__ == "__main__":
    unittest.main()
