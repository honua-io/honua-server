# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE.
"""Join collected nightly observations without promoting diagnostic runs to release proof."""
from __future__ import annotations

import copy
import datetime as dt
import math
import re

MAX_AGE_DAYS = 14
INTEROP_PRODUCER_COUNT = 3

# Actual Honua.LoadTests scenario names and the endpoints they exercise.
# The GeoBench crosswalk uses different names; never claim those were measured.
LOAD_CAPABILITIES = {
    "serve.ogc-api-features": ["ogc_query_load", "cql_filter_load", "features_pagination_load"],
    "serve.geoservices-featureserver": ["feature_query_load", "spatial_query_load"],
}


def instant(value):
    try:
        parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
        return parsed.astimezone(dt.timezone.utc) if parsed.tzinfo else None
    except (ValueError, TypeError, AttributeError):
        return None


def provenance(producer: dict) -> dict:
    return {key: producer.get(key) for key in
            ("workflow", "runId", "runAttempt", "runUrl", "sourceSha", "conclusion", "artifacts")}


def freshness(observed, anchor, conclusion: str) -> dict:
    at, now = instant(observed), instant(anchor)
    age = (now - at).total_seconds() / 86400 if at and now else None
    state = "unknown" if age is None else "fresh" if conclusion == "success" and 0 <= age <= MAX_AGE_DAYS else "stale"
    return {"state": state, "ageDays": round(age, 3) if age is not None else None, "runDate": observed}


def trusted_document(producer, document, collected_at):
    kind = producer["kind"]
    observed = document.get("run_date") if kind == "interop" else document.get("generatedAt")
    at, end, collected = instant(observed), instant(producer.get("observedAt")), instant(collected_at)
    if not at or not end or not collected or not at <= end <= collected:
        return False
    sha = producer.get("sourceSha")
    if not isinstance(sha, str) or not re.fullmatch(r"[0-9a-f]{40}", sha):
        return False
    if kind == "interop":
        return document.get("server_commit") == sha
    if kind == "cite":
        git = document.get("git", {})
        return (git.get("producerSha") == sha
                and isinstance(git.get("sha"), str)
                and re.fullmatch(r"[0-9a-f]{40}", git["sha"]) is not None)
    return True


def interop_rows(snapshot, impact):
    envelopes = {}
    producers = [p for p in snapshot["producers"] if p["kind"] == "interop"]
    # A producer that failed before uploading cannot silently preserve another
    # workflow's older green result for an overlapping lane.
    complete = len(producers) == INTEROP_PRODUCER_COUNT and all(p.get("documents") for p in producers)
    for producer in sorted(producers, key=lambda p: p.get("observedAt", "")):
        for document in producer["documents"]:
            key = (document.get("client_lane") or document.get("clientLane"), document.get("protocol"))
            green = (complete and producer.get("conclusion") == "success"
                     and trusted_document(producer, document, snapshot["collectedAt"])
                     and impact.is_green(document))
            envelopes[key] = {"freshness": freshness(document.get("run_date"), snapshot["collectedAt"], "success" if green else "failure"),
                              "evidence": provenance(producer)}
    return envelopes


def cite_rows(snapshot):
    suites = {}
    for producer in snapshot["producers"]:
        if producer["kind"] != "cite":
            continue
        for document in producer["documents"]:
            trusted = trusted_document(producer, document, snapshot["collectedAt"])
            for suite in document.get("suites", []):
                total, passed = suite.get("totalTests", 0), suite.get("passed", 0)
                counters = [total, passed, *(suite.get(k) for k in ("failed", "skipped", "cantTell"))]
                valid = all(type(n) is int and n >= 0 for n in counters)
                green = (trusted and valid and total > 0 and passed == total
                         and counters[2:] == [0, 0, 0] and suite.get("status") == "passed"
                         and producer.get("conclusion") == "success")
                stamp = freshness(document.get("generatedAt"), snapshot["collectedAt"], "success" if green else "failure")
                suites[suite["name"]] = {"passed": passed, "total": total,
                                         "passRate": 100.0 if stamp["state"] == "fresh" else 0.0,
                                         "freshness": stamp, "evidence": provenance(producer),
                                         "testedSha": document.get("git", {}).get("sha")}
    return suites


def load_rows(snapshot):
    scenarios = {}
    for producer in snapshot["producers"]:
        if producer["kind"] != "geobench":
            continue
        for document in producer["documents"]:
            trusted = trusted_document(producer, document, snapshot["collectedAt"])
            for scenario in document.get("scenarios", []):
                ok, fail = scenario.get("okCount"), scenario.get("failCount")
                required = [scenario.get(key) for key in ("durationSeconds", "okRps", "meanMs", "maxMs", "p95Ms", "p99Ms")]
                valid = all(type(n) in (int, float) and math.isfinite(n) and n >= 0 for n in required)
                green = (trusted and valid and required[0] > 0 and type(ok) is int and ok > 0 and type(fail) is int and fail == 0
                         and producer.get("conclusion") == "success")
                metrics = {key: scenario.get(key) for key in
                           ("durationSeconds", "okCount", "failCount", "okRps", "meanMs", "maxMs", "p95Ms", "p99Ms")}
                scenarios[scenario["name"]] = {"scenario": scenario["name"], "metrics": metrics,
                    "freshness": freshness(document.get("generatedAt"), snapshot["collectedAt"], "success" if green else "failure"),
                    "evidence": provenance(producer)}
    return scenarios


def apply(base_matrix, snapshot, impact):
    matrix = copy.deepcopy(base_matrix)
    if snapshot.get("schemaVersion") != "1.0.0" or snapshot.get("repository") != "honua-io/honua-server":
        raise ValueError("unsupported nightly capability evidence snapshot")
    if not instant(snapshot.get("collectedAt")):
        raise ValueError("nightly evidence collectedAt must be a timezone-qualified timestamp")
    interop, cite, loads = interop_rows(snapshot, impact), cite_rows(snapshot), load_rows(snapshot)
    for capability in matrix["capabilities"]:
        for row in capability["interop"]:
            row.update(interop.get((row["clientLane"], row["protocol"]),
                                   {"freshness": freshness(None, snapshot["collectedAt"], "missing")}))
        for row in capability["cite"]:
            # No successful fallback to the reviewed table when the latest
            # nightly produced no report, omitted a suite, or failed.
            row.update(cite.get(row["suite"], {"passRate": 0.0,
                       "freshness": freshness(None, snapshot["collectedAt"], "missing")}))
        names = list(dict.fromkeys([*capability["geobench"], *LOAD_CAPABILITIES.get(capability["key"], [])]))
        capability["geobenchResults"] = [loads.get(name, {"scenario": name,
            "freshness": freshness(None, snapshot["collectedAt"], "missing")}) for name in names]
    matrix["schemaVersion"] = "1.3.0"
    matrix["sourceArtifacts"].append("docs/gis/data/nightly-capability-evidence.v1.json")
    matrix["evidenceFreshness"] = {"anchor": snapshot["collectedAt"], "maxAgeDays": MAX_AGE_DAYS,
        "basis": "Nightly collection time; consumers must also enforce snapshot age against their evaluation time."}
    matrix["nightlyEvidence"] = {"collectedAt": snapshot["collectedAt"], "producers": [
        provenance(p) for p in snapshot["producers"]], "scope": "diagnostic; not candidate release proof"}
    return matrix
