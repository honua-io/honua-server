#!/usr/bin/env python3
# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""Turn one soak run's raw outputs into the receipt payload the release gate evaluates.

Inputs are the two things the run actually produced — the NBomber load statistics
(`--stats`, written by tests/dotnet/Honua.LoadTests with `--stats-out`) and the driver's
observations (`--observations`, written by scripts/soak/drive_soak.py) — plus the frozen
lock the receipt binds to.

Every signal is derived from one of those files. A figure that is absent from its source is
NOT defaulted: the signal is emitted `unobserved`, the receipt's status becomes `incomplete`,
and the self-check against the lock fails the run before anything is published.
"""

from __future__ import annotations

import argparse
import json
import math
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parent))
from soak_contract import (  # noqa: E402
    STATUS_FAILED,
    STATUS_OBSERVED,
    Signal,
    build_receipt,
    canonical_json,
    evaluate,
    lock_digest,
)


def _finite(value: Any) -> float | None:
    if value is None or isinstance(value, bool):
        return None
    try:
        number = float(value)
    except (TypeError, ValueError):
        return None
    return number if math.isfinite(number) else None


def _unobserved(name: str, revision: str, method: str, unit: str, window: tuple[str, str], detail: str) -> Signal:
    return Signal(
        name=name,
        value=None,
        revision=revision,
        method=method,
        unit=unit,
        window_start=window[0],
        window_end=window[1],
        sample_count=0,
        status=STATUS_FAILED,
        detail=detail,
    )


def build_signals(
    *,
    stats: dict[str, Any],
    observations: dict[str, Any],
    revision: str,
) -> dict[str, Signal]:
    steady = (observations.get("steadyStart"), observations.get("steadyEnd"))
    if not steady[0] or not steady[1]:
        raise SystemExit("observations carry no steady-state window")
    load_window = (stats.get("generatedAt", steady[0]), stats.get("generatedAt", steady[1]))
    signals: dict[str, Signal] = {}

    # availability — independent 1 Hz prober over the steady-state window.
    availability = observations.get("availability") or {}
    samples = int(availability.get("samples") or 0)
    ok = int(availability.get("ok") or 0)
    method_availability = (
        "served/attempted ratio of an independent 1 Hz FeatureServer query probe, sampled across the "
        "steady-state window (separate from the load harness's own counters)"
    )
    if samples > 0:
        signals["availability"] = Signal(
            name="availability",
            value=ok / samples,
            revision=revision,
            method=method_availability,
            unit="ratio",
            window_start=steady[0],
            window_end=steady[1],
            sample_count=samples,
            evidence={"okProbes": ok, "probes": samples},
        )
    else:
        signals["availability"] = _unobserved(
            "availability", revision, method_availability, "ratio", steady, "no availability probes were taken"
        )

    # errorRate / throughputRps / latency — the load harness's own aggregate statistics.
    total = _finite(stats.get("allRequestCount"))
    failed = _finite(stats.get("allFailCount"))
    ok_requests = _finite(stats.get("allOkCount"))
    duration = _finite(stats.get("durationSeconds"))
    scenarios = stats.get("scenarios") or []

    method_error = "failed/total requests reported by the NBomber soak run across every scenario"
    if total and total > 0 and failed is not None:
        signals["errorRate"] = Signal(
            name="errorRate",
            value=failed / total,
            revision=revision,
            method=method_error,
            unit="ratio",
            window_start=load_window[0],
            window_end=load_window[1],
            sample_count=int(total),
            evidence={"failedRequests": int(failed), "totalRequests": int(total)},
        )
    else:
        signals["errorRate"] = _unobserved(
            "errorRate", revision, method_error, "ratio", steady, "load statistics carry no request counts"
        )

    method_throughput = (
        "successful requests divided by the full run duration (ramp-up + steady state + ramp-down), the "
        "same aggregate the frozen throughput floor was derived from in docs/CAPACITY-ENVELOPE-2026.1.md"
    )
    if ok_requests is not None and duration and duration > 0:
        signals["throughputRps"] = Signal(
            name="throughputRps",
            value=ok_requests / duration,
            revision=revision,
            method=method_throughput,
            unit="requests/second",
            window_start=load_window[0],
            window_end=load_window[1],
            sample_count=int(ok_requests),
            evidence={"okRequests": int(ok_requests), "runSeconds": duration},
        )
    else:
        signals["throughputRps"] = _unobserved(
            "throughputRps", revision, method_throughput, "requests/second", steady,
            "load statistics carry no successful-request count or run duration",
        )

    for name, key in (("p95LatencyMs", "p95Ms"), ("p99LatencyMs", "p99Ms")):
        percentile = key[1:-2]
        method = (
            f"worst-scenario p{percentile} latency across the soak profile's scenarios — the same "
            "worst-scenario reading the frozen limit was taken from"
        )
        per_scenario = {
            scenario.get("name"): _finite(scenario.get(key))
            for scenario in scenarios
            if isinstance(scenario, dict)
        }
        usable = {scenario: value for scenario, value in per_scenario.items() if value is not None}
        if usable and len(usable) == len(per_scenario):
            worst = max(usable.values())
            signals[name] = Signal(
                name=name,
                value=worst,
                revision=revision,
                method=method,
                unit="milliseconds",
                window_start=load_window[0],
                window_end=load_window[1],
                sample_count=len(usable),
                evidence={"perScenario": usable, "worstScenario": max(usable, key=usable.get)},
            )
        else:
            signals[name] = _unobserved(
                name, revision, method, "milliseconds", steady,
                "at least one scenario reported no percentile latency",
            )

    # queueAgeSeconds — oldest geoprocessing job wait observed during steady state.
    gp = observations.get("gpQueue") or {}
    queue_age = _finite(gp.get("maxOldestQueueAgeSeconds"))
    method_queue = (
        "maximum age reached by a queued geoprocessing job before the declared single worker started "
        "it, measured client-side once per second across the steady-state window"
    )
    if queue_age is not None and int(gp.get("observedJobs") or 0) > 0:
        signals["queueAgeSeconds"] = Signal(
            name="queueAgeSeconds",
            value=queue_age,
            revision=revision,
            method=method_queue,
            unit="seconds",
            window_start=steady[0],
            window_end=steady[1],
            sample_count=int(gp.get("samples") or 0),
            evidence={
                "observedJobs": gp.get("observedJobs"),
                "maxQueueDepth": gp.get("maxQueueDepth"),
                "admissionRejections": gp.get("admissionRejections"),
            },
        )
    else:
        signals["queueAgeSeconds"] = _unobserved(
            "queueAgeSeconds", revision, method_queue, "seconds", steady,
            "no geoprocessing job completed during the steady-state window",
        )

    # saturationRatio — the server's own connection-pool utilisation.
    saturation = observations.get("saturation") or {}
    peak = _finite(saturation.get("peakUtilization"))
    sample_count = int(saturation.get("samples") or 0)
    with_data = int(saturation.get("withData") or 0)
    method_saturation = (
        "peak connection-pool utilisation reported by the server's own /monitoring/metrics/connection-pool "
        "snapshot during the steady-state window; a sample without utilisation data invalidates the signal"
    )
    if peak is not None and sample_count > 0 and with_data == sample_count:
        signals["saturationRatio"] = Signal(
            name="saturationRatio",
            value=peak,
            revision=revision,
            method=method_saturation,
            unit="ratio",
            window_start=steady[0],
            window_end=steady[1],
            sample_count=sample_count,
            evidence={"meanUtilization": saturation.get("meanUtilization"), "samplesWithData": with_data},
        )
    else:
        signals["saturationRatio"] = _unobserved(
            "saturationRatio", revision, method_saturation, "ratio", steady,
            f"{sample_count - with_data} of {sample_count} saturation samples carried no utilisation data",
        )

    # recoveryTimeSeconds — the post-steady-state fault drill.
    recovery = observations.get("recovery") or {}
    recovery_time = _finite(recovery.get("recoveryTimeSeconds"))
    method_recovery = (
        "seconds from injecting a server-process fault (container restart) until the deployment served a "
        "feature query again; run after the steady-state window so the frozen availability budget is not "
        "spent on a deliberate outage"
    )
    if recovery.get("status") == "observed" and recovery_time is not None:
        signals["recoveryTimeSeconds"] = Signal(
            name="recoveryTimeSeconds",
            value=recovery_time,
            revision=revision,
            method=method_recovery,
            unit="seconds",
            window_start=recovery.get("injectedAt", steady[1]),
            window_end=recovery.get("recoveredAt", steady[1]),
            sample_count=int(recovery.get("probeAttempts") or 0),
            evidence={"fault": recovery.get("fault")},
        )
    else:
        signals["recoveryTimeSeconds"] = _unobserved(
            "recoveryTimeSeconds", revision, method_recovery, "seconds", steady,
            str(recovery.get("reason") or "the recovery drill did not run"),
        )

    return signals


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--lock", required=True, type=Path)
    parser.add_argument("--stats", required=True, type=Path)
    parser.add_argument("--observations", required=True, type=Path)
    parser.add_argument("--candidate-sha", required=True)
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--substrate", required=True, type=Path, help="JSON describing the substrate under test")
    parser.add_argument("--producer", required=True, type=Path, help="JSON describing the producing workflow run")
    parser.add_argument("--steady-state-seconds", type=int, required=True)
    args = parser.parse_args()

    lock = json.loads(args.lock.read_text(encoding="utf-8"))
    stats = json.loads(args.stats.read_text(encoding="utf-8"))
    observations = json.loads(args.observations.read_text(encoding="utf-8"))

    observed_revision = (observations.get("deployment") or {}).get("observedRevision")
    signals = build_signals(stats=stats, observations=observations, revision=args.candidate_sha)

    receipt = build_receipt(
        lock=lock,
        lock_sha256=lock_digest(args.lock),
        candidate_revision=args.candidate_sha,
        observed_revision=observed_revision,
        observed_revision_source=(observations.get("deployment") or {}).get("observedRevisionSource"),
        started_at=observations["steadyStart"],
        completed_at=datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        steady_state_seconds=args.steady_state_seconds,
        signals=signals,
        envelope_verification=observations["envelopeVerification"],
        substrate=json.loads(args.substrate.read_text(encoding="utf-8")),
        producer=json.loads(args.producer.read_text(encoding="utf-8")),
        measurement={
            "loadHarness": "tests/dotnet/Honua.LoadTests (NBomber) via scripts/scale/run-load-soak-tests.sh",
            "loadProfile": stats.get("profile"),
            "loadRunSeconds": stats.get("durationSeconds"),
            "loadScenarios": stats.get("scenarios"),
            "driver": observations.get("driver"),
            "deployment": observations.get("deployment"),
            "recovery": observations.get("recovery"),
            "steadyStateWindow": {"start": observations.get("steadyStart"), "end": observations.get("steadyEnd")},
        },
    )

    args.out.write_text(json.dumps(receipt, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(f"receipt payload: {args.out} ({len(canonical_json(receipt))} canonical bytes)")

    # Self-check against the very rules honua-release's gate applies, so a receipt that would
    # be rejected there is never signed, published or advertised from here.
    failures = evaluate(
        lock,
        {**receipt, "signature": "self-check", "signingIdentity": "self-check"},
        lock_digest(args.lock),
        args.candidate_sha,
    )
    for name, signal in sorted(signals.items()):
        marker = "OK " if signal.status == STATUS_OBSERVED else "BAD"
        print(f"  {marker} {name}: {signal.value if signal.status == STATUS_OBSERVED else signal.detail}")
    if failures:
        print("capacity-soak self-check: FAIL", file=sys.stderr)
        for failure in failures:
            print(f"- {failure}", file=sys.stderr)
        return 1
    print("capacity-soak self-check: PASS")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
