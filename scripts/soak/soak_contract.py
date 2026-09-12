#!/usr/bin/env python3
# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""Receipt contract for the candidate capacity soak (honua-io/honua-release#235).

The consumer of what this produces is honua-release's `capacity-soak.yml` gate, which
fetches the published receipt over HTTPS, proves `honua-io/honua-server` signed it with
a GitHub artifact attestation, and then runs
`tools/check_capacity_soak.py --lock certification/capacity-envelope.v1.json
--expected-revision <manifest-pinned server SHA>`.

`evaluate()` below is a deliberate MIRROR of that checker so this producer fails in its
own run rather than publishing a receipt the release train will reject. It is never the
authority: the frozen lock and honua-release's checker are. Keeping the mirror honest is
the job of tests/python/unit/test_capacity_soak_receipt.py, which encodes the same rules
the checker encodes; if the two ever diverge the release gate wins and this file is the
one that must change.

Nothing here loosens a threshold or invents a value. A signal that was not observed has
no defaulted value: it is recorded with a non-`observed` status and the run fails.
"""

from __future__ import annotations

import hashlib
import json
import math
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

RECEIPT_SCHEMA = "honua-server.capacity-soak-receipt/v1"

#: Receipt members that carry the signature itself and are therefore excluded from the
#: signed payload. The payload is what the attestation binds; the two members below are
#: added afterwards from that attestation (see scripts/soak/sign_receipt.py).
SIGNATURE_MEMBERS = ("signature", "signingIdentity", "signatureFormat", "signingIdentitySource")

#: Signal status values. Only OBSERVED can pass: a skipped, failed or unattempted
#: measurement keeps its own status and reds the run, per the capacity envelope doc
#: ("a skipped, null, non-finite, stale, or revision-mismatched signal fails").
#: Envelope-dimension coverage. `verified` means the soak established the dimension on the
#: deployment under test AND re-observed it there. `not-exercised` means the lock declares the
#: dimension but this run deliberately did not drive it — the receipt says so out loud, in
#: `envelopeCoverage.declaredNotExercised`, rather than letting a verbatim copy of the lock's
#: envelope block imply coverage that did not happen. There is no third, silent state: a
#: dimension with neither record fails `build_receipt`.
COVERAGE_VERIFIED = "verified"
COVERAGE_NOT_EXERCISED = "not-exercised"

STATUS_OBSERVED = "observed"
STATUS_UNOBSERVED = "unobserved"
STATUS_FAILED = "failed"


class ContractError(ValueError):
    """A receipt or lock that cannot be read as the contract requires."""


@dataclass(frozen=True)
class Signal:
    """One measured signal, with the provenance of the measurement attached."""

    name: str
    value: float | None
    revision: str
    method: str
    unit: str
    window_start: str
    window_end: str
    sample_count: int
    status: str = STATUS_OBSERVED
    detail: str | None = None
    evidence: dict[str, Any] = field(default_factory=dict)

    def to_json(self) -> dict[str, Any]:
        body: dict[str, Any] = {
            "status": self.status,
            "revision": self.revision,
            "method": self.method,
            "unit": self.unit,
            "windowStart": self.window_start,
            "windowEnd": self.window_end,
            "sampleCount": self.sample_count,
        }
        # A non-observed signal carries no value at all rather than a zero/null that a
        # careless reader could mistake for a measurement.
        if self.status == STATUS_OBSERVED:
            if self.value is None or isinstance(self.value, bool) or not math.isfinite(float(self.value)):
                raise ContractError(f"{self.name}: observed signal without a finite value")
            body["value"] = float(self.value)
        if self.detail:
            body["detail"] = self.detail
        if self.evidence:
            body["evidence"] = self.evidence
        return body


def canonical_json(document: Any) -> bytes:
    """Canonical bytes for signing/digesting: sorted keys, no insignificant whitespace.

    Verifiers re-derive these bytes from the published receipt by dropping the signature
    members, so the encoding has to be reproducible rather than merely valid.
    """
    return json.dumps(document, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode("utf-8")


def payload_of(receipt: dict[str, Any]) -> dict[str, Any]:
    """The signed payload of a receipt: everything except the signature members."""
    return {key: value for key, value in receipt.items() if key not in SIGNATURE_MEMBERS}


def payload_digest(receipt_or_payload: dict[str, Any]) -> str:
    return hashlib.sha256(canonical_json(payload_of(receipt_or_payload))).hexdigest()


def lock_digest(path: Path) -> str:
    """SHA-256 of the exact lock bytes, the same way honua-release's checker computes it."""
    return hashlib.sha256(path.read_bytes()).hexdigest()


def parse_time(value: object, field_name: str) -> datetime:
    if not isinstance(value, str):
        raise ContractError(f"{field_name} is missing")
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as exc:  # pragma: no cover - message is the contract, not the path
        raise ContractError(f"{field_name} is not an ISO-8601 timestamp") from exc
    if parsed.tzinfo is None:
        raise ContractError(f"{field_name} must include a timezone")
    return parsed.astimezone(timezone.utc)


def required_signals(lock: dict[str, Any]) -> list[str]:
    signals = lock.get("soak", {}).get("requiredSignals", [])
    if not signals:
        raise ContractError("lock declares no required signals")
    return list(signals)


def threshold_holds(threshold: dict[str, Any], value: float) -> bool:
    """Frozen-threshold comparison, byte-for-byte the checker's rule.

    `regressionAllowance` is deliberately NOT applied: the allowance was consumed when
    the thresholds were derived from the baseline and frozen. Applying it again here
    would silently widen a frozen limit at evaluation time.
    """
    operator, limit = threshold.get("operator"), threshold.get("value")
    if operator == "<=":
        return value <= limit
    if operator == ">=":
        return value >= limit
    return False


def evaluate(lock: dict[str, Any], receipt: dict[str, Any], digest: str, expected_revision: str) -> list[str]:
    """Mirror of honua-release tools/check_capacity_soak.py. Returns failure strings."""
    failures: list[str] = []
    if not expected_revision:
        failures.append("expected manifest-pinned honua-server SHA is missing")
    if receipt.get("status") != "completed":
        failures.append("soak status must be completed (skipped/partial signals are failures)")
    if receipt.get("candidateRevision") != expected_revision:
        failures.append("candidate revision does not match the manifest-pinned honua-server SHA")
    if receipt.get("observedRevision") != expected_revision:
        failures.append("observed revision does not match the manifest-pinned honua-server SHA")
    if receipt.get("lockSha256") != digest:
        failures.append("receipt does not bind the exact committed threshold lock")
    try:
        if parse_time(receipt.get("startedAt"), "startedAt") <= parse_time(lock.get("frozenAt"), "frozenAt"):
            failures.append("soak did not start after the threshold freeze")
    except ContractError as exc:
        failures.append(str(exc))
    if not receipt.get("signingIdentity") or not receipt.get("signature"):
        failures.append("signed receipt identity/signature is missing")
    if receipt.get("profile") != lock.get("soak", {}).get("profile"):
        failures.append("soak profile does not match the lock")
    if receipt.get("steadyStateSeconds", 0) < lock.get("soak", {}).get("minimumSteadyStateSeconds", 0):
        failures.append("steady-state duration is below the locked minimum")
    if receipt.get("envelope") != lock.get("supportedEnvelope"):
        failures.append("tested capacity envelope does not exactly match the supported envelope")

    signals = receipt.get("signals")
    if not isinstance(signals, dict):
        failures.append("signals object is missing")
        signals = {}
    thresholds = lock.get("thresholds", {})
    for name in lock.get("soak", {}).get("requiredSignals", []):
        signal = signals.get(name)
        if not isinstance(signal, dict) or signal.get("status") != STATUS_OBSERVED:
            failures.append(f"{name}: missing, skipped, or unobserved")
            continue
        if signal.get("revision") != receipt.get("candidateRevision"):
            failures.append(f"{name}: signal revision mismatch")
            continue
        value = signal.get("value")
        if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
            failures.append(f"{name}: value is absent or non-finite")
            continue
        threshold = thresholds.get(name, {})
        if not threshold_holds(threshold, value):
            failures.append(
                f"{name}: {value} violates frozen requirement "
                f"{threshold.get('operator')} {threshold.get('value')}"
            )
    return failures


def build_receipt(
    *,
    lock: dict[str, Any],
    lock_sha256: str,
    candidate_revision: str,
    observed_revision: str,
    observed_revision_source: str,
    started_at: str,
    completed_at: str,
    steady_state_seconds: int,
    signals: dict[str, Signal],
    envelope_verification: dict[str, Any],
    substrate: dict[str, Any],
    producer: dict[str, Any],
    measurement: dict[str, Any],
) -> dict[str, Any]:
    """Assemble the receipt payload.

    `envelope` is copied from the lock verbatim only after every declared dimension has
    been established and verified against the running deployment; the caller proves that
    and passes the per-dimension evidence in `envelope_verification`. Copying the block
    is how the checker's equality test is satisfied, but the claim it makes is backed by
    `envelopeVerification`, not by the copy.
    """
    declared_dimensions = set((lock.get("supportedEnvelope") or {}).keys())
    recorded = set(envelope_verification)
    missing = declared_dimensions - recorded
    if missing:
        raise ContractError(
            "refusing to claim the declared envelope: no coverage record for " + ", ".join(sorted(missing))
        )

    bad_coverage = sorted(
        name
        for name, record in envelope_verification.items()
        if record.get("coverage") not in (COVERAGE_VERIFIED, COVERAGE_NOT_EXERCISED)
        or (record.get("coverage") == COVERAGE_VERIFIED and not record.get("verified"))
    )
    if bad_coverage:
        # A dimension that was meant to be verified and was not is a failed run, not a
        # footnote: the alternative is a receipt whose `envelope` block claims capacity the
        # soak never established.
        raise ContractError(
            "refusing to claim the declared envelope: unverified dimension(s): " + ", ".join(bad_coverage)
        )

    missing = [name for name in required_signals(lock) if name not in signals]
    if missing:
        raise ContractError("missing required signal(s): " + ", ".join(sorted(missing)))

    unobserved = sorted(name for name, signal in signals.items() if signal.status != STATUS_OBSERVED)
    status = "completed" if not unobserved else "incomplete"

    receipt: dict[str, Any] = {
        "schema": RECEIPT_SCHEMA,
        "status": status,
        "release": lock.get("release"),
        "candidateRevision": candidate_revision,
        "observedRevision": observed_revision,
        "observedRevisionSource": observed_revision_source,
        "lockSha256": lock_sha256,
        "lockFrozenAt": lock.get("frozenAt"),
        "profile": lock.get("soak", {}).get("profile"),
        "startedAt": started_at,
        "completedAt": completed_at,
        "steadyStateSeconds": steady_state_seconds,
        "envelope": lock.get("supportedEnvelope"),
        "envelopeVerification": envelope_verification,
        "signals": {name: signal.to_json() for name, signal in sorted(signals.items())},
        "measurement": measurement,
        "substrate": substrate,
        "producer": producer,
    }
    if unobserved:
        receipt["unobservedSignals"] = unobserved

    not_exercised = sorted(
        name for name, record in envelope_verification.items()
        if record.get("coverage") == COVERAGE_NOT_EXERCISED
    )
    receipt["envelopeCoverage"] = {
        "verified": sorted(set(envelope_verification) - set(not_exercised)),
        "declaredNotExercised": not_exercised,
    }
    return receipt


def load_json(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))
