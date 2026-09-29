#!/usr/bin/env python3
"""Build a v2 receipt and bounded ZIP from measured capacity-observations/v1.

The ZIP, not a self-asserted signature field, is the SLSA attestation subject.
No aggregate NBomber statistic is expanded into invented request observations.
The release checker remains the independent authority for qualification.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import re
import zipfile
from datetime import datetime
from pathlib import Path


def digest(payload: bytes) -> str:
    return hashlib.sha256(payload).hexdigest()


def timestamp(value: str) -> datetime:
    if not value.endswith("Z"):
        raise ValueError("observation timestamps must be UTC (Z)")
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


def assert_candidate(candidate: str, source: str, checkout: str) -> None:
    if not re.fullmatch(r"[0-9a-f]{40}", candidate) or not candidate == source == checkout:
        raise ValueError("workflow source and checkout must both equal the manifest candidate commit")


def receipt(lock: dict, lock_hash: str, source: dict, payload: bytes, artifact_url: str) -> dict:
    """Compute values from joint histograms and sampled rows, retaining failing values."""
    if source.get("schema") != "honua.capacity-observations/v1":
        raise ValueError("expected measured honua.capacity-observations/v1, not legacy aggregates")
    if source["lockSha256"] != lock_hash:
        raise ValueError("observations do not bind the supplied lock bytes")
    candidate = source["candidateIdentity"]
    assert_candidate(candidate["serverRevision"], source["producer"]["sourceRevision"], source["observedRevision"])
    run_id = source["producer"]["runId"]
    if not re.fullmatch(rf"https://github.com/honua-io/honua-server/actions/runs/{run_id}/artifacts/[1-9][0-9]*", artifact_url):
        raise ValueError("raw artifact must belong to the producer Actions run")
    duration = (timestamp(source["window"]["endedAt"]) - timestamp(source["window"]["startedAt"])).total_seconds()
    if duration <= 0:
        raise ValueError("empty observation window")
    buckets = [bucket for interval in source["requests"] for bucket in interval["buckets"]]
    count = sum(bucket["count"] for bucket in buckets)
    if count <= 0 or source["requestCount"] != count:
        raise ValueError("empty or inconsistent request population")
    errors = sum(bucket["count"] for bucket in buckets if bucket["httpStatus"] >= 500 or bucket["inBandError"])

    def percentile(fraction):
        rank = math.ceil(count * fraction)
        for bucket in sorted(buckets, key=lambda b: b["durationMs"]):
            rank -= bucket["count"]
            if rank <= 0:
                return bucket["durationMs"]
        raise ValueError("empty latency population")

    metrics, workloads, recoveries = (source[name] for name in ("metrics", "workloads", "recoveries"))
    # Missing measurements are null, never a zero that could pass a threshold.
    def maximum(rows, key):
        values = [row.get(key) for row in rows]
        if not values or any(not isinstance(v, (float, int)) or isinstance(v, bool) or not math.isfinite(v) for v in values):
            return None
        return max(values)

    components = {name: maximum(metrics, name) for name in ("worker", "database", "redis")}
    recovery_seconds = [{"seconds": (timestamp(e["recoveredAt"]) - timestamp(e["injectedAt"])).total_seconds()} for e in recoveries]
    values = dict(availability=(count-errors)/count, errorRate=errors/count,
                  p95LatencyMs=percentile(.95), p99LatencyMs=percentile(.99), throughputRps=count/duration,
                  queueAgeSeconds=maximum(metrics, "queueAgeSeconds"),
                  saturationRatio=max(components.values()) if all(v is not None for v in components.values()) else None,
                  recoveryTimeSeconds=maximum(recovery_seconds, "seconds"))
    common = {key: source[key] for key in ("candidateIdentity", "window", "topology")}
    references = ["observations"]
    populations = {
        name: dict(kind="ratio", sampleCount=count, numerator=numerator, denominator=denominator)
        for name, numerator, denominator in (("availability", count-errors, count), ("errorRate", errors, count), ("throughputRps", count, duration))
    }
    for name in values.keys() - populations.keys():
        populations[name] = dict(kind="distribution" if "Latency" in name else "duration" if name == "recoveryTimeSeconds" else "gauge",
                                 sampleCount=count if "Latency" in name else len(recoveries) if name == "recoveryTimeSeconds" else len(metrics)*3 if name == "saturationRatio" else len(metrics))
    doc = f"https://github.com/honua-io/honua-server/blob/{candidate['serverRevision']}/docs/ops/capacity-soak-receipt.md"
    signals = {}
    for name, value in values.items():
        threshold = lock["thresholds"][name]
        passed = value is not None and (value <= threshold["value"] if threshold["operator"] == "<=" else value >= threshold["value"])
        signals[name] = dict(common, status="observed" if value is not None else "unobserved", value=value,
                             query=lock["queries"][name], owner=lock["owner"], alert=doc+"#failed-qualification", runbook=doc,
                             observationPopulation=populations[name], rawArtifactIds=references,
                             workloadDimensions=list(lock["supportedEnvelope"]),
                             thresholdVerdict=dict(operator=threshold["operator"], limit=threshold["value"], passed=passed))
    signals["saturationRatio"]["saturationComponents"] = {
        name: dict(value=value, sampleCount=len(metrics), rawArtifactIds=references) for name, value in components.items()}
    signals["recoveryTimeSeconds"]["recoveryEvidence"] = dict(events=recoveries, rawArtifactIds=references)
    exercised = {}
    for name, target in lock["supportedEnvelope"].items():
        observed = [row["dimensions"].get(name) for row in workloads]
        met = bool(observed) and all(value == target for value in observed)
        exercised[name] = dict(candidateIdentity=candidate, window=source["window"], query=lock["workloadQueries"][name],
                               status="exercised" if met else "not-exercised", target=target,
                               observed=target if met else next((v for v in observed if v != target), None),
                               executionMode="candidate-topology", proxy=False, sampleCount=len(workloads),
                               observationPopulation=dict(kind="ratio", numerator=sum(v == target for v in observed), denominator=len(observed), sampleCount=len(observed)),
                               rawArtifactIds=references)
    complete = not source["samplingFailures"] and all(v is not None for v in values.values()) and all(w["status"] == "exercised" for w in exercised.values())
    return dict(common, schemaVersion=2, status="completed" if complete else "incomplete", evidenceScope="single-tenant-ga", profile="soak",
                lockSha256=lock_hash, observedRevision=source["observedRevision"], steadyStateSeconds=duration,
                envelope=lock["supportedEnvelope"], producer=source["producer"], signals=signals, workloads=exercised,
                signingIdentity="https://github.com/"+source["producer"]["workflowRef"],
                signature="external:slsa-v1:capacity-evidence.zip",
                rawArtifacts=[dict(id="observations", kind="capacity-observations", path="capacity-observations.json",
                                   uri=artifact_url, sha256=digest(payload), observationCount=count)])


def write_bundle(output: Path, result: dict, payload: bytes) -> None:
    files = {"capacity-soak-receipt.json": (json.dumps(result, indent=2, allow_nan=False)+"\n").encode(),
             "capacity-observations.json": payload}
    if any(len(value) > 64*1024*1024 for value in files.values()):
        raise ValueError("capacity evidence file exceeds 64 MiB")
    if sum(map(len, files.values())) > 256*1024*1024 or len(files) > 64:
        raise ValueError("capacity evidence exceeds ZIP limits")
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for name, value in files.items():
            archive.writestr(name, value)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--lock", required=True, type=Path)
    parser.add_argument("--observations", required=True, type=Path)
    parser.add_argument("--artifact-url", required=True)
    parser.add_argument("--out", required=True, type=Path)
    args = parser.parse_args()
    payload = args.observations.read_bytes()
    result = receipt(json.loads(args.lock.read_bytes()), digest(args.lock.read_bytes()), json.loads(payload), payload, args.artifact_url)
    write_bundle(args.out, result, payload)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
