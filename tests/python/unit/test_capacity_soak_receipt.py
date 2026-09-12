# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""Contract tests for the candidate capacity-soak receipt (honua-io/honua-release#235).

The authority these assert against lives in another repository: honua-release's
``tools/check_capacity_soak.py`` evaluates the published receipt against the frozen
``certification/capacity-envelope.v1.json``. ``scripts/soak/soak_contract.py`` mirrors those
rules so a receipt that the release gate would reject is never signed or published from here,
and these tests are what keep the mirror honest — each case below encodes a rule the real
checker enforces, including the ones whose whole purpose is to refuse a receipt (skipped
signals, revision mismatches, a soak that started before the freeze).
"""

from __future__ import annotations

import copy
import importlib.util
import json
import sys
from pathlib import Path

import pytest

REPOSITORY_ROOT = Path(__file__).parents[3]
SOAK_SCRIPTS = REPOSITORY_ROOT / "scripts" / "soak"


def _load(module_name: str):
    spec = importlib.util.spec_from_file_location(module_name, SOAK_SCRIPTS / f"{module_name}.py")
    module = importlib.util.module_from_spec(spec)
    sys.modules[module_name] = module
    spec.loader.exec_module(module)
    return module


soak_contract = _load("soak_contract")
build_receipt_module = _load("build_receipt")

CANDIDATE = "a" * 40
LOCK_DIGEST = "b" * 64
FROZEN_AT = "2026-09-01T10:05:00Z"
STEADY_START = "2026-09-12T01:00:00Z"
STEADY_END = "2026-09-12T02:00:00Z"

LOCK = {
    "release": "2026.1.0-rc.1",
    "frozenAt": FROZEN_AT,
    "supportedEnvelope": {
        "tenants": 1,
        "services": 1,
        "layersPerService": 4,
        "featuresPerLayer": 10000,
        "concurrentVirtualUsers": 170,
    },
    "soak": {
        "profile": "soak",
        "minimumSteadyStateSeconds": 3600,
        "requiredSignals": ["availability", "errorRate", "p95LatencyMs"],
    },
    "thresholds": {
        "availability": {"operator": ">=", "value": 0.9999},
        "errorRate": {"operator": "<=", "value": 0.0001},
        "p95LatencyMs": {"operator": "<=", "value": 610.0},
    },
}

VERIFIED_ENVELOPE = {
    dimension: {
        "declared": value,
        "observed": value,
        "coverage": soak_contract.COVERAGE_VERIFIED,
        "verified": True,
        "method": "test",
    }
    for dimension, value in LOCK["supportedEnvelope"].items()
}


def signal(name: str, value: float | None, *, status: str = soak_contract.STATUS_OBSERVED) -> soak_contract.Signal:
    return soak_contract.Signal(
        name=name,
        value=value,
        revision=CANDIDATE,
        method="test",
        unit="ratio",
        window_start=STEADY_START,
        window_end=STEADY_END,
        sample_count=3600,
        status=status,
    )


def passing_signals() -> dict[str, soak_contract.Signal]:
    return {
        "availability": signal("availability", 1.0),
        "errorRate": signal("errorRate", 0.0),
        "p95LatencyMs": signal("p95LatencyMs", 412.5),
    }


def receipt(**overrides):
    document = soak_contract.build_receipt(
        lock=LOCK,
        lock_sha256=LOCK_DIGEST,
        candidate_revision=CANDIDATE,
        observed_revision=CANDIDATE,
        observed_revision_source="commit-sha",
        started_at=STEADY_START,
        completed_at=STEADY_END,
        steady_state_seconds=3600,
        signals=overrides.pop("signals", passing_signals()),
        envelope_verification=overrides.pop("envelope_verification", copy.deepcopy(VERIFIED_ENVELOPE)),
        substrate={"kind": "local-docker"},
        producer={"repository": "honua-io/honua-server"},
        measurement={"loadProfile": "soak"},
    )
    document["signature"] = "signature-bytes"
    document["signingIdentity"] = "https://github.com/honua-io/honua-server/.github/workflows/capacity-soak-candidate.yml@refs/heads/trunk"
    document.update(overrides)
    return document


def evaluate(document) -> list[str]:
    return soak_contract.evaluate(LOCK, document, LOCK_DIGEST, CANDIDATE)


class TestReceiptAcceptance:
    def test_complete_receipt_passes_every_rule(self):
        assert evaluate(receipt()) == []

    def test_receipt_copies_the_declared_envelope_verbatim(self):
        # The gate compares for exact equality; anything else is a rejected receipt.
        assert receipt()["envelope"] == LOCK["supportedEnvelope"]

    def test_coverage_summary_separates_verified_from_declared_only(self):
        envelope = copy.deepcopy(VERIFIED_ENVELOPE)
        envelope["tenants"] = {
            "declared": 1,
            "coverage": soak_contract.COVERAGE_NOT_EXERCISED,
            "verified": False,
            "reason": "not driven by this run",
        }
        document = receipt(envelope_verification=envelope)
        assert document["envelopeCoverage"]["declaredNotExercised"] == ["tenants"]
        assert "tenants" not in document["envelopeCoverage"]["verified"]
        # A declared-not-exercised dimension is disclosed, not hidden, and the receipt is still
        # a valid one: the gate's own rules do not read the coverage block.
        assert evaluate(document) == []


class TestUnmetEnvelope:
    """A dimension the deployment failed to hold is evidence, not a reason to lose the run."""

    def unmet_receipt(self):
        envelope = copy.deepcopy(VERIFIED_ENVELOPE)
        envelope["concurrentVirtualUsers"] = {
            "declared": 170,
            "observed": 41,
            "coverage": soak_contract.COVERAGE_NOT_MET,
            "verified": False,
            "method": "driven and measured",
        }
        return receipt(envelope_verification=envelope)

    def test_a_dimension_that_was_not_held_is_recorded_not_dropped(self):
        document = self.unmet_receipt()
        assert document["envelopeCoverage"]["notMet"] == ["concurrentVirtualUsers"]
        assert document["envelopeVerification"]["concurrentVirtualUsers"]["observed"] == 41
        assert "concurrentVirtualUsers" not in document["envelopeCoverage"]["verified"]

    def test_an_unmet_dimension_makes_the_receipt_incomplete_and_the_gate_refuses_it(self):
        document = self.unmet_receipt()
        assert document["status"] == "incomplete"
        assert any("status must be completed" in failure for failure in evaluate(document))


class TestReceiptConstructionRefusals:
    def test_unverified_dimension_refuses_to_build(self):
        envelope = copy.deepcopy(VERIFIED_ENVELOPE)
        envelope["featuresPerLayer"]["verified"] = False
        with pytest.raises(soak_contract.ContractError, match="featuresPerLayer"):
            receipt(envelope_verification=envelope)

    def test_an_unknown_coverage_state_refuses_to_build(self):
        envelope = copy.deepcopy(VERIFIED_ENVELOPE)
        envelope["services"]["coverage"] = "probably-fine"
        with pytest.raises(soak_contract.ContractError, match="services"):
            receipt(envelope_verification=envelope)

    def test_missing_dimension_record_refuses_to_build(self):
        envelope = copy.deepcopy(VERIFIED_ENVELOPE)
        del envelope["concurrentVirtualUsers"]
        with pytest.raises(soak_contract.ContractError, match="concurrentVirtualUsers"):
            receipt(envelope_verification=envelope)

    def test_missing_required_signal_refuses_to_build(self):
        signals = passing_signals()
        del signals["errorRate"]
        with pytest.raises(soak_contract.ContractError, match="errorRate"):
            receipt(signals=signals)

    def test_observed_signal_without_a_value_refuses_to_serialise(self):
        with pytest.raises(soak_contract.ContractError, match="availability"):
            receipt(signals={**passing_signals(), "availability": signal("availability", None)})


class TestFrozenRuleMirror:
    def test_unobserved_signal_makes_the_receipt_incomplete_and_fails(self):
        signals = {**passing_signals(), "p95LatencyMs": signal("p95LatencyMs", None, status=soak_contract.STATUS_FAILED)}
        document = receipt(signals=signals)
        assert document["status"] == "incomplete"
        assert document["unobservedSignals"] == ["p95LatencyMs"]
        failures = evaluate(document)
        assert any("completed" in failure for failure in failures)
        assert any("p95LatencyMs" in failure for failure in failures)

    def test_threshold_violation_is_named_with_its_frozen_requirement(self):
        document = receipt(signals={**passing_signals(), "p95LatencyMs": signal("p95LatencyMs", 611.0)})
        assert evaluate(document) == ["p95LatencyMs: 611.0 violates frozen requirement <= 610.0"]

    def test_regression_allowance_never_widens_a_frozen_threshold(self):
        lock = copy.deepcopy(LOCK)
        lock["thresholds"]["p95LatencyMs"]["regressionAllowance"] = 0.10
        document = receipt(signals={**passing_signals(), "p95LatencyMs": signal("p95LatencyMs", 650.0)})
        assert soak_contract.evaluate(lock, document, LOCK_DIGEST, CANDIDATE)

    @pytest.mark.parametrize(
        ("mutation", "expected"),
        [
            ({"candidateRevision": "c" * 40}, "candidate revision"),
            ({"observedRevision": "c" * 40}, "observed revision"),
            ({"lockSha256": "f" * 64}, "exact committed threshold lock"),
            ({"startedAt": "2026-08-31T00:00:00Z"}, "after the threshold freeze"),
            ({"signature": ""}, "identity/signature"),
            ({"signingIdentity": ""}, "identity/signature"),
            ({"profile": "nightly"}, "soak profile"),
            ({"steadyStateSeconds": 3599}, "steady-state duration"),
            ({"envelope": {"tenants": 1}}, "capacity envelope"),
            ({"status": "partial"}, "status must be completed"),
        ],
    )
    def test_each_rule_rejects_its_own_mutation(self, mutation, expected):
        assert any(expected in failure for failure in evaluate(receipt(**mutation)))

    def test_receipt_stripped_of_its_signals_is_rejected(self):
        document = receipt()
        document["signals"] = {}
        failures = evaluate(document)
        assert len(failures) == len(LOCK["soak"]["requiredSignals"])
        assert all("missing, skipped, or unobserved" in failure for failure in failures)

    def test_signal_bound_to_another_revision_is_rejected(self):
        document = receipt()
        document["signals"]["errorRate"]["revision"] = "c" * 40
        assert any("revision mismatch" in failure for failure in evaluate(document))

    def test_non_finite_signal_value_is_rejected(self):
        document = receipt()
        document["signals"]["errorRate"]["value"] = float("inf")
        assert any("non-finite" in failure for failure in evaluate(document))

    def test_boolean_is_not_a_measurement(self):
        document = receipt()
        document["signals"]["errorRate"]["value"] = True
        assert any("errorRate" in failure for failure in evaluate(document))


class TestSignedPayload:
    def test_payload_excludes_only_the_signature_members(self):
        document = receipt()
        payload = soak_contract.payload_of(document)
        assert set(document) - set(payload) == {"signature", "signingIdentity"}

    def test_canonical_bytes_are_stable_under_key_order(self):
        document = receipt()
        shuffled = dict(reversed(list(document.items())))
        assert soak_contract.canonical_json(document) == soak_contract.canonical_json(shuffled)
        assert soak_contract.payload_digest(document) == soak_contract.payload_digest(shuffled)


STATS = {
    "generatedAt": STEADY_END,
    "profile": "soak",
    "durationSeconds": 4020.0,
    "allRequestCount": 1000000.0,
    "allOkCount": 999999.0,
    "allFailCount": 1.0,
    "scenarios": [
        {"name": "feature_query_load", "p95Ms": 401.0, "p99Ms": 502.0},
        {"name": "tiles_load", "p95Ms": 550.0, "p99Ms": 601.0},
    ],
}

OBSERVATIONS = {
    "steadyStart": STEADY_START,
    "steadyEnd": STEADY_END,
    "availability": {"samples": 3600, "ok": 3600},
    "saturation": {"samples": 720, "withData": 720, "peakUtilization": 0.42, "meanUtilization": 0.2},
    "gpQueue": {"samples": 3600, "maxOldestQueueAgeSeconds": 2.5, "observedJobs": 700, "maxQueueDepth": 2},
    "recovery": {"status": "observed", "recoveryTimeSeconds": 18.0, "probeAttempts": 18,
                 "injectedAt": STEADY_END, "recoveredAt": STEADY_END, "fault": "container restart"},
}


class TestSignalDerivation:
    def signals(self, *, stats=None, observations=None):
        return build_receipt_module.build_signals(
            stats=copy.deepcopy(stats or STATS),
            observations=copy.deepcopy(observations or OBSERVATIONS),
            revision=CANDIDATE,
        )

    def test_every_signal_is_derived_from_a_real_observation(self):
        signals = self.signals()
        assert signals["availability"].value == pytest.approx(1.0)
        assert signals["errorRate"].value == pytest.approx(1e-6)
        assert signals["throughputRps"].value == pytest.approx(999999.0 / 4020.0)
        assert signals["queueAgeSeconds"].value == pytest.approx(2.5)
        assert signals["saturationRatio"].value == pytest.approx(0.42)
        assert signals["recoveryTimeSeconds"].value == pytest.approx(18.0)
        assert all(s.status == soak_contract.STATUS_OBSERVED for s in signals.values())

    def test_latency_takes_the_worst_scenario_not_the_average(self):
        signals = self.signals()
        assert signals["p95LatencyMs"].value == pytest.approx(550.0)
        assert signals["p99LatencyMs"].value == pytest.approx(601.0)
        assert signals["p95LatencyMs"].evidence["worstScenario"] == "tiles_load"

    def test_a_scenario_without_percentiles_invalidates_the_latency_signal(self):
        stats = copy.deepcopy(STATS)
        stats["scenarios"][1]["p95Ms"] = None
        signals = self.signals(stats=stats)
        assert signals["p95LatencyMs"].status != soak_contract.STATUS_OBSERVED
        assert signals["p95LatencyMs"].value is None

    def test_saturation_samples_without_data_invalidate_the_signal(self):
        observations = copy.deepcopy(OBSERVATIONS)
        observations["saturation"]["withData"] = 719
        signals = self.signals(observations=observations)
        assert signals["saturationRatio"].status != soak_contract.STATUS_OBSERVED
        assert "utilisation data" in signals["saturationRatio"].detail

    def test_a_queue_that_ran_no_jobs_is_not_a_zero_second_queue_age(self):
        observations = copy.deepcopy(OBSERVATIONS)
        observations["gpQueue"]["observedJobs"] = 0
        signals = self.signals(observations=observations)
        assert signals["queueAgeSeconds"].status != soak_contract.STATUS_OBSERVED
        assert signals["queueAgeSeconds"].value is None

    def test_a_recovery_drill_that_did_not_run_is_not_a_zero_second_recovery(self):
        observations = copy.deepcopy(OBSERVATIONS)
        observations["recovery"] = {"status": "failed", "reason": "compose restart failed"}
        signals = self.signals(observations=observations)
        assert signals["recoveryTimeSeconds"].status != soak_contract.STATUS_OBSERVED
        assert "compose restart failed" in signals["recoveryTimeSeconds"].detail

    def test_missing_load_statistics_do_not_become_zeros(self):
        stats = copy.deepcopy(STATS)
        stats["allOkCount"] = None
        stats["allRequestCount"] = None
        signals = self.signals(stats=stats)
        assert signals["throughputRps"].value is None
        assert signals["errorRate"].value is None
        assert signals["throughputRps"].status != soak_contract.STATUS_OBSERVED
