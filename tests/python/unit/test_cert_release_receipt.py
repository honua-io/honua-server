# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""Release-tier receipt emission for the bounded 2026.1 client roster (#3434).

The contract these assert against is not invented here: it is the
``receiptContract`` block of ``certification/client-protocol-requirements.v1.json``,
which mirrors what the governed ``client-interop-cert-v1`` consumer in
honua-evidence demands. The tests read that file, so a receipt that stops
satisfying the governed contract fails here rather than at release time.
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from shared.cert_envelope import CertificationEvidenceCollector, LaneRuntime

REPOSITORY_ROOT = Path(__file__).parents[3]
RECEIPT_CONTRACT = json.loads(
    (REPOSITORY_ROOT / "certification" / "client-protocol-requirements.v1.json").read_text(
        encoding="utf-8"))["receiptContract"]

CANDIDATE_SHA = "a" * 40
PRODUCER_SHA = "c" * 40
CANDIDATE_DIGEST = "sha256:" + "b" * 64


def runtime(**overrides) -> LaneRuntime:
    values = {
        "base_url": "https://candidate.test",
        "environment": "local-docker",
        "server_version": "1.0.0+" + CANDIDATE_SHA,
        "server_commit": CANDIDATE_SHA,
        "fixture_revision": "seed.sql@" + CANDIDATE_SHA,
        "server_config_revision": "config-v1",
        "image_digest": CANDIDATE_DIGEST,
        "producer_source_sha": PRODUCER_SHA,
        "auth_policy_revision": "anonymous-v1",
    }
    values.update(overrides)
    return LaneRuntime(**values)


def collector(**overrides) -> CertificationEvidenceCollector:
    values = {
        "client_lane": "py-owslib",
        "client_version": "0.36.0",
        "protocol": "ogc-features",
        "protocol_version": "1.0",
        "applicable": frozenset({"CERT-CONN-01", "CERT-DISC-01"}),
        "not_applicable_reason": "OWSLib has no drawing surface.",
        "client_id": "OWSLib",
        "protocol_profile": "core",
    }
    lane_runtime = overrides.pop("runtime", None) or runtime()
    values.update(overrides)
    return CertificationEvidenceCollector(lane_runtime, **values)


def record_substantiated(instance: CertificationEvidenceCollector, **overrides) -> None:
    values = {
        "test_case_id": "CERT-DISC-01",
        "status": "pass",
        "request_url": "https://candidate.test/collections",
        "exercised_capabilities": ("positive",),
    }
    values.update(overrides)
    instance.record(
        values.pop("test_case_id"), values.pop("status"), **values)


# ---------------------------------------------------------------------------
# the governed contract is satisfied
# ---------------------------------------------------------------------------

def test_a_release_receipt_carries_every_required_envelope_field() -> None:
    instance = collector()
    record_substantiated(instance)

    receipt = instance.build_release_receipt()

    for field in RECEIPT_CONTRACT["requiredEnvelopeFields"]:
        assert field in receipt, field
    assert receipt["schema_version"] == RECEIPT_CONTRACT["envelopeSchemaVersion"]
    assert receipt["server_commit"] == CANDIDATE_SHA
    assert receipt["image_digest"] == CANDIDATE_DIGEST
    assert receipt["producer_source_sha"] == PRODUCER_SHA
    assert receipt["auth_policy_revision"] == "anonymous-v1"
    assert receipt["client_id"] == "OWSLib"
    assert receipt["runner_lane"] == "py-owslib"
    assert receipt["protocol_profile"] == "core"


def test_every_published_result_carries_its_own_request_provenance() -> None:
    instance = collector()
    record_substantiated(instance)

    receipt = instance.build_release_receipt()
    executed = [entry for entry in receipt["results"] if entry["status"] != "not_applicable"]

    assert executed, "the substantiated observation must be published"
    for entry in executed:
        for field in RECEIPT_CONTRACT["requiredResultFields"]:
            assert field in entry, field
        assert entry["performed_by"] == "OWSLib"
        assert entry["exercised_capabilities"] == ["positive"]


def test_the_governed_status_vocabulary_replaces_the_hyphenated_token() -> None:
    instance = collector()
    record_substantiated(instance)

    receipt = instance.build_release_receipt()
    statuses = {entry["status"] for entry in receipt["results"]}

    assert "not-applicable" not in statuses
    assert statuses <= set(RECEIPT_CONTRACT["resultStatusVocabulary"])
    assert "not_applicable" in statuses, "the untouched common-core IDs stay declared"


def test_a_skip_may_omit_a_request_url_but_not_its_facets() -> None:
    instance = collector()
    record_substantiated(instance)
    instance.record(
        "CERT-CONN-01", "skip", request_url=None,
        exercised_capabilities=("positive",),
        notes="The governed endpoint was unreachable.")

    receipt = instance.build_release_receipt()
    skipped = next(e for e in receipt["results"] if e["test_case_id"] == "CERT-CONN-01")

    assert skipped["status"] == "skip"
    assert skipped["request_url"] is None
    assert skipped["exercised_capabilities"] == ["positive"]


def test_extension_results_stay_in_their_own_array() -> None:
    instance = collector()
    record_substantiated(instance)
    record_substantiated(
        instance, test_case_id="NB-OWS-OAF-LAND-01",
        request_url="https://candidate.test/")

    receipt = instance.build_release_receipt()

    assert [entry["test_case_id"] for entry in receipt["extensions"]] == ["NB-OWS-OAF-LAND-01"]
    assert all(entry["test_case_id"].startswith("CERT-") for entry in receipt["results"])


# ---------------------------------------------------------------------------
# fail-closed emission
# ---------------------------------------------------------------------------

@pytest.mark.parametrize("binding", ["image_digest", "producer_source_sha", "auth_policy_revision"])
def test_a_missing_candidate_binding_refuses_to_emit(binding: str) -> None:
    instance = collector(runtime=runtime(**{binding: None}))
    record_substantiated(instance)

    with pytest.raises(ValueError, match=binding):
        instance.build_release_receipt()


@pytest.mark.parametrize("identity", ["client_id", "protocol_profile"])
def test_a_missing_governed_identity_refuses_to_emit(identity: str) -> None:
    instance = collector(**{identity: None})
    record_substantiated(instance)

    with pytest.raises(ValueError, match=identity):
        instance.build_release_receipt()


def test_a_source_built_server_cannot_emit_a_release_receipt() -> None:
    # `read_server_commit` falls back to "unknown" when git is unavailable, which
    # is exactly the developer/source-built case the gate must never admit.
    instance = collector(runtime=runtime(server_commit="unknown"))
    record_substantiated(instance)

    with pytest.raises(ValueError, match="source-built"):
        instance.build_release_receipt()


def test_a_locally_built_image_cannot_emit_a_release_receipt() -> None:
    instance = collector(runtime=runtime(image_digest="honua-server:local"))
    record_substantiated(instance)

    with pytest.raises(ValueError, match="locally built"):
        instance.build_release_receipt()


def test_an_untrusted_producer_revision_cannot_emit_a_release_receipt() -> None:
    instance = collector(runtime=runtime(producer_source_sha="dev"))
    record_substantiated(instance)

    with pytest.raises(ValueError, match="producer_source_sha"):
        instance.build_release_receipt()


def test_an_observation_without_a_request_url_is_omitted_not_invented() -> None:
    instance = collector()
    record_substantiated(instance)
    instance.record("CERT-CONN-01", "pass", exercised_capabilities=("positive",))

    receipt = instance.build_release_receipt()

    published = {entry["test_case_id"] for entry in receipt["results"]
                 if entry["status"] not in {"not_applicable"}}
    assert "CERT-CONN-01" not in published
    assert [entry["test_case_id"] for entry in receipt["unsubstantiated"]] == ["CERT-CONN-01"]


def test_an_observation_without_exercised_facets_is_omitted() -> None:
    instance = collector()
    record_substantiated(instance)
    instance.record(
        "CERT-CONN-01", "pass", request_url="https://candidate.test/")

    receipt = instance.build_release_receipt()

    assert [entry["test_case_id"] for entry in receipt["unsubstantiated"]] == ["CERT-CONN-01"]


def test_a_credentialled_request_url_is_not_publishable() -> None:
    instance = collector()
    record_substantiated(instance)
    instance.record(
        "CERT-CONN-01", "pass", request_url="https://user:secret@candidate.test/",
        exercised_capabilities=("positive",))

    receipt = instance.build_release_receipt()

    assert [entry["test_case_id"] for entry in receipt["unsubstantiated"]] == ["CERT-CONN-01"]


def test_duplicate_facets_are_not_publishable() -> None:
    instance = collector()
    record_substantiated(instance)
    instance.record(
        "CERT-CONN-01", "pass", request_url="https://candidate.test/",
        exercised_capabilities=("positive", "positive"))

    receipt = instance.build_release_receipt()

    assert [entry["test_case_id"] for entry in receipt["unsubstantiated"]] == ["CERT-CONN-01"]


def test_a_run_that_substantiates_nothing_refuses_to_emit() -> None:
    instance = collector()
    instance.record("CERT-DISC-01", "pass")

    with pytest.raises(ValueError, match="substantiated no executable observation"):
        instance.build_release_receipt()


# ---------------------------------------------------------------------------
# the nightly envelope is unchanged
# ---------------------------------------------------------------------------

def test_the_nightly_envelope_keeps_its_hyphenated_vocabulary() -> None:
    # The baseline diff, the committed baselines and the matrix documentation all
    # read the hyphenated token. Release-tier translation must not leak into it.
    instance = collector()
    record_substantiated(instance)

    envelope = instance.build_envelope()

    assert {entry["status"] for entry in envelope["results"]} == {"pass", "skip", "not-applicable"}
    assert "image_digest" not in envelope
    assert "client_id" not in envelope


def test_a_lane_without_release_bindings_still_emits_a_nightly_envelope() -> None:
    instance = collector(
        runtime=runtime(image_digest=None, producer_source_sha=None, auth_policy_revision=None),
        client_id=None, protocol_profile=None)
    record_substantiated(instance)

    envelope = instance.build_envelope()

    assert envelope["client_lane"] == "py-owslib"
    assert envelope["summary"]["passed"] == 1


def test_write_release_receipt_round_trips(tmp_path: Path) -> None:
    instance = collector()
    record_substantiated(instance)

    target = tmp_path / "nested" / "py-owslib-ogc-features.cert.json"
    instance.write_release_receipt(target)

    written = json.loads(target.read_text(encoding="utf-8"))
    # `run_id` and `run_date` are stamped per call, so compare everything else.
    volatile = {"run_id", "run_date"}
    rebuilt = instance.build_release_receipt()
    assert {k: v for k, v in written.items() if k not in volatile} == {
        k: v for k, v in rebuilt.items() if k not in volatile}
    assert written["run_date"].endswith("+00:00")
    assert written["run_id"]
