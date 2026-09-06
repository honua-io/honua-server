#!/usr/bin/env python3
"""Validate exact-candidate realtime Preview evidence and emit its ledger."""

from __future__ import annotations

import argparse
import json
import re
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

EVIDENCE_FORMAT = "honua.realtime-preview-evidence.v2"
LEDGER_FORMAT = "honua.realtime-preview-qualification.v2"
SHA = re.compile(r"^[a-f0-9]{40}$")
DIGEST = re.compile(r"^sha256:[a-f0-9]{64}$")

# The 2026.1 Preview floor. Operational-graduation scenarios (ordering,
# duplicates, HA, proxy/Redis failover, broker outage, saturation/backpressure,
# and soak) intentionally do not appear in this denominator.
PREVIEW_ROWS = tuple(
    ("feature-stream", transport, scenario)
    for transport in ("sse", "websocket", "odata")
    for scenario in (
        "baseline-completion",
        "resume-gap-detection",
        "reconnect-under-partition",
        "token-expiry",
        "token-revocation",
        "tenant-isolation",
        "tenant-scope-change",
    )
) + (
    ("feature-stream", "odata", "lossless-state-convergence"),
    ("sensorthings", "sse", "explicit-loss-recovery"),
    ("sensorthings", "sse", "token-expiry"),
    ("sensorthings", "sse", "token-revocation"),
    ("sensorthings", "sse", "tenant-isolation"),
    ("sensorthings", "sse", "tenant-scope-change"),
    ("sensorthings", "websocket", "explicit-loss-recovery"),
    ("sensorthings", "websocket", "token-expiry"),
    ("sensorthings", "websocket", "token-revocation"),
    ("sensorthings", "websocket", "tenant-isolation"),
    ("sensorthings", "websocket", "tenant-scope-change"),
)

AUTH_SCENARIOS = {"token-expiry", "token-revocation", "tenant-isolation", "tenant-scope-change"}


def _authorization_diagnostics(row: dict, workflow: dict) -> list[str]:
    """Require the live authorization transcript, not just a projected green cell."""
    reasons: list[str] = []
    proof = row.get("authorization")
    if not isinstance(proof, dict):
        return ["authorization transcript is missing"]
    if not DIGEST.fullmatch(str(proof.get("issuerFingerprint", ""))):
        reasons.append("authorization issuer/configuration fingerprint is missing")
    tenants = proof.get("tenantIds")
    if (not isinstance(tenants, list) or len(tenants) != 2
            or any(not isinstance(tenant, str) or not tenant.strip() for tenant in tenants)
            or tenants[0] == tenants[1]):
        reasons.append("authorization proof must name two distinct isolated tenants")
    resources = proof.get("resourceIds")
    if (not isinstance(resources, list) or len(resources) < 2
            or any(not isinstance(item, str) or not item.strip() for item in resources)
            or len(set(resources)) != len(resources)):
        reasons.append("authorization proof must name distinct tenant-qualified protected resources")
    mutations = proof.get("mutationIds")
    if (not isinstance(mutations, list) or len(mutations) < 2
            or any(not isinstance(item, str) or not item.strip() for item in mutations)
            or len(set(mutations)) != len(mutations)):
        reasons.append("authorization proof must retain distinct injected mutation identifiers")

    issued = _timestamp(proof.get("issuedAt"), "authorization.issuedAt", reasons)
    expires = _timestamp(proof.get("expiresAt"), "authorization.expiresAt", reasons)
    if issued is not None and expires is not None and issued >= expires:
        reasons.append("authorization token lifetime is invalid")

    observations = proof.get("observations")
    observation_times = []
    started = _timestamp(workflow.get("startedAt"), "workflow.startedAt", reasons)
    completed = _timestamp(workflow.get("completedAt"), "workflow.completedAt", reasons)
    if not isinstance(observations, list) or len(observations) < 2:
        reasons.append("authorization raw transport observations are missing")
    else:
        for observation in observations:
            if not isinstance(observation, dict) or not isinstance(observation.get("raw"), str) or not observation["raw"].strip():
                reasons.append("authorization observation lacks raw frame/HTTP response bytes")
                continue
            at = _timestamp(observation.get("at"), "authorization.observation.at", reasons)
            if at is not None:
                observation_times.append(at)
                if started is not None and completed is not None and not started <= at <= completed:
                    reasons.append("authorization observation falls outside its claimed live workflow")

    assertions = row.get("assertions")
    assertion_ids = {
        assertion.get("id") for assertion in (assertions if isinstance(assertions, list) else [])
        if isinstance(assertion, dict) and assertion.get("passed") is True
    }
    required_assertions = {"no-cross-tenant-payload", "invalid-credentials-rejected"}
    if row["scenario"] in {"token-expiry", "token-revocation"}:
        required_assertions |= {"old-credential-terminated", "replacement-resume"}
        boundary = expires
        if row["scenario"] == "token-revocation":
            boundary = _timestamp(proof.get("revokedAt"), "authorization.revokedAt", reasons)
        terminated = _timestamp(proof.get("terminatedAt"), "authorization.terminatedAt", reasons)
        bound = proof.get("enforcementBoundMilliseconds")
        if isinstance(bound, bool) or not isinstance(bound, int) or not 0 < bound <= 5000:
            reasons.append("authorization enforcement bound must be positive and at most 5000 ms")
        elif boundary is not None and terminated is not None and not boundary <= terminated <= boundary + timedelta(milliseconds=bound):
            reasons.append("authorization termination exceeded the declared enforcement bound")
        if boundary is not None and (not any(at < boundary for at in observation_times)
                                     or not any(at >= boundary for at in observation_times)):
            reasons.append("authorization transcript must observe both sides of expiry/revocation")
        expected_reason = "unauthorized" if row["transport"] == "odata" else "authorization-ended"
        if proof.get("terminationReason") != expected_reason:
            reasons.append("authorization termination reason is missing or not machine-detectable")
    if row["scenario"] == "tenant-scope-change":
        required_assertions.add("changed-scope-rejected")
    if required_assertions - assertion_ids:
        reasons.append("authorization assertion receipts missing: " + ", ".join(sorted(required_assertions - assertion_ids)))
    return reasons


def _timestamp(value: object, field: str, diagnostics: list[str]) -> datetime | None:
    if not isinstance(value, str):
        diagnostics.append(f"{field} is missing")
        return None
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        diagnostics.append(f"{field} is not an RFC3339 timestamp")
        return None
    if parsed.tzinfo is None:
        diagnostics.append(f"{field} must include a timezone")
        return None
    return parsed.astimezone(timezone.utc)


def _identity_diagnostics(evidence: dict, expected: dict, now: datetime, max_age: timedelta) -> list[str]:
    diagnostics: list[str] = []
    if evidence.get("format") != EVIDENCE_FORMAT or evidence.get("schemaVersion") != 2:
        diagnostics.append(f"receipt schema must be {EVIDENCE_FORMAT} version 2")
    if evidence.get("lane") != "live":
        diagnostics.append("receipt lane must be live; fixture or synthetic evidence is inadmissible")

    candidate = evidence.get("candidate") if isinstance(evidence.get("candidate"), dict) else {}
    server = evidence.get("server") if isinstance(evidence.get("server"), dict) else {}
    sdk = evidence.get("sdk") if isinstance(evidence.get("sdk"), dict) else {}
    workflow = evidence.get("workflow") if isinstance(evidence.get("workflow"), dict) else {}
    checks = (
        (server.get("revision"), expected["serverRevision"], "server revision"),
        (candidate.get("environment"), expected["environment"], "candidate environment"),
        (sdk.get("revision"), expected["sdkRevision"], "SDK revision"),
        (sdk.get("package"), expected["sdkPackage"], "SDK package"),
        (workflow.get("repository"), expected["workflowRepository"], "workflow repository"),
        (workflow.get("name"), expected["workflowName"], "workflow name"),
        (str(workflow.get("runId", "")), str(expected["runId"]), "workflow run id"),
        (str(workflow.get("runAttempt", "")), str(expected["runAttempt"]), "workflow run attempt"),
        (str(workflow.get("artifactId", "")), str(expected["artifactId"]), "workflow artifact id"),
        (workflow.get("artifactUrl"), expected["sourceArtifactUrl"], "source artifact URL"),
    )
    for actual, wanted, label in checks:
        if actual != wanted:
            diagnostics.append(f"{label} {actual!r} does not match exact candidate {wanted!r}")

    actual_image = server.get("image")
    expected_image = expected["serverImage"]
    if expected_image:
        if actual_image != expected_image:
            diagnostics.append(
                f"immutable server image {actual_image!r} does not match exact candidate {expected_image!r}"
            )
    else:
        # The source SDK receipt can be collected before the release candidate exists. It
        # remains useful for all of the independent admissibility checks below, but it must
        # never qualify a release without a post-candidate receipt bound to the emitted digest.
        diagnostics.append(
            "candidate digest binding is unavailable: post-candidate receipt required "
            "(honua-release#269)"
        )

    if not SHA.fullmatch(str(server.get("revision", ""))):
        diagnostics.append("server revision must be an immutable 40-character commit SHA")
    if not DIGEST.fullmatch(str(server.get("image", ""))):
        diagnostics.append("server image must be an immutable sha256 digest, not a mutable tag")
    if not SHA.fullmatch(str(sdk.get("revision", ""))):
        diagnostics.append("SDK revision must be an immutable 40-character commit SHA")
    if not isinstance(sdk.get("version"), str) or not sdk["version"].strip():
        diagnostics.append("SDK package version is missing")
    if workflow.get("conclusion") != "success":
        diagnostics.append("source workflow conclusion must be success")
    if not isinstance(workflow.get("artifactUrl"), str) or not workflow["artifactUrl"].startswith("https://"):
        diagnostics.append("immutable source artifact URL is missing")

    generated = _timestamp(evidence.get("generatedAt"), "generatedAt", diagnostics)
    started = _timestamp(workflow.get("startedAt"), "workflow.startedAt", diagnostics)
    completed = _timestamp(workflow.get("completedAt"), "workflow.completedAt", diagnostics)
    if generated is not None:
        if generated > now + timedelta(minutes=5):
            diagnostics.append("receipt generation time is in the future")
        if now - generated > max_age:
            diagnostics.append("receipt is stale for the configured freshness window")
        if started is not None and generated < started:
            diagnostics.append("receipt predates its claimed workflow execution (replayed evidence)")
        if completed is not None and generated > completed + timedelta(minutes=5):
            diagnostics.append("receipt postdates its claimed workflow execution")
    if started is not None and completed is not None and started > completed:
        diagnostics.append("workflow time window is invalid")
    return diagnostics


def qualify(evidence: dict, expected: dict, *, now: datetime, max_age: timedelta) -> dict:
    diagnostics = _identity_diagnostics(evidence, expected, now, max_age)
    source_rows = evidence.get("rows") if isinstance(evidence.get("rows"), list) else []
    indexed: dict[tuple[object, object, object], list[dict]] = {}
    for item in source_rows:
        if not isinstance(item, dict):
            diagnostics.append("receipt contains a non-object row")
            continue
        key = (item.get("surface"), item.get("transport"), item.get("scenario"))
        indexed.setdefault(key, []).append(item)

    required = set(PREVIEW_ROWS)
    for key in indexed:
        if key not in required:
            diagnostics.append("unrelated scenario row cannot qualify Preview: " + "/".join(str(part) for part in key))

    rows = []
    for surface, transport, scenario in PREVIEW_ROWS:
        matches = indexed.get((surface, transport, scenario), [])
        reasons: list[str] = []
        if not matches:
            reasons.append("exact-candidate receipt does not contain this required Preview row")
        elif len(matches) > 1:
            reasons.append("receipt contains duplicate/replayed observations for this row")
        else:
            row = matches[0]
            if row.get("executed") is not True:
                reasons.append("row was not executed live; skip or fixture output is inadmissible")
            if row.get("result") != "passed":
                reasons.append(f"executed row result is {row.get('result')!r}, not 'passed'")
            assertions = row.get("assertions")
            if not isinstance(assertions, list) or not assertions:
                reasons.append("row has no executed assertion receipts")
            elif any(not isinstance(assertion, dict) or assertion.get("passed") is not True for assertion in assertions):
                reasons.append("one or more executed assertions did not pass")
            if scenario in AUTH_SCENARIOS:
                workflow = evidence.get("workflow")
                reasons.extend(_authorization_diagnostics(row, workflow if isinstance(workflow, dict) else {}))
            if row.get("serverRevision") != expected["serverRevision"]:
                reasons.append("row is not bound to the exact server revision")
            if expected["serverImage"]:
                if row.get("serverImage") != expected["serverImage"]:
                    reasons.append("row is not bound to the immutable server image")
            elif row.get("serverImage") != evidence.get("server", {}).get("image"):
                reasons.append("row is not internally bound to the immutable server image")
            if row.get("sdkRevision") != expected["sdkRevision"]:
                reasons.append("row is not bound to the exact SDK revision")
            if row.get("sdkPackage") != expected["sdkPackage"]:
                reasons.append("row is not bound to the exact SDK package")
            if row.get("environment") != expected["environment"]:
                reasons.append("row is not bound to the candidate environment")
            if str(row.get("runId", "")) != str(expected["runId"]) or str(row.get("runAttempt", "")) != str(expected["runAttempt"]):
                reasons.append("row is not bound to the exact workflow execution")
            if str(row.get("artifactId", "")) != str(expected["artifactId"]):
                reasons.append("row is not bound to the immutable source artifact")
        rows.append({
            "surface": surface,
            "transport": transport,
            "scenario": scenario,
            "state": "qualified" if not reasons and not diagnostics else "rejected",
            "reasons": reasons + (["receipt identity/admissibility validation failed"] if diagnostics else []),
        })

    qualified = not diagnostics and all(row["state"] == "qualified" for row in rows)
    return {
        "format": LEDGER_FORMAT,
        "generatedAt": now.isoformat().replace("+00:00", "Z"),
        "status": "qualified" if qualified else "rejected",
        "candidate": {
            "environment": expected["environment"],
            "serverRevision": expected["serverRevision"],
            "serverImage": expected["serverImage"] or evidence.get("server", {}).get("image"),
            "sdkPackage": expected["sdkPackage"],
            "sdkRevision": expected["sdkRevision"],
            "workflowRepository": expected["workflowRepository"],
            "workflowName": expected["workflowName"],
            "sourceArtifactUrl": expected["sourceArtifactUrl"],
            "qualificationRunUrl": expected["qualificationRunUrl"],
            "runId": str(expected["runId"]),
            "runAttempt": str(expected["runAttempt"]),
            "artifactId": str(expected["artifactId"]),
        },
        "diagnostics": diagnostics,
        "rows": rows,
        "graduationRowsRequired": False,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--evidence", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--candidate-revision", required=True)
    parser.add_argument("--candidate-image", required=True)
    parser.add_argument("--candidate-environment", required=True)
    parser.add_argument("--sdk-package", required=True)
    parser.add_argument("--sdk-revision", required=True)
    parser.add_argument("--workflow-repository", required=True)
    parser.add_argument("--workflow-name", required=True)
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--run-attempt", required=True)
    parser.add_argument("--artifact-id", required=True)
    parser.add_argument("--source-artifact-url", required=True)
    parser.add_argument("--qualification-run-url", required=True)
    parser.add_argument("--max-age-hours", type=int, default=24)
    parser.add_argument("--require-qualified", action="store_true")
    args = parser.parse_args()
    try:
        evidence = json.loads(args.evidence.read_text(encoding="utf-8"))
        if not isinstance(evidence, dict):
            raise ValueError("evidence root must be an object")
    except (OSError, json.JSONDecodeError, ValueError) as exc:
        print(f"realtime qualification refused: {exc}", file=sys.stderr)
        return 2

    expected = {
        "serverRevision": args.candidate_revision,
        "serverImage": args.candidate_image,
        "environment": args.candidate_environment,
        "sdkPackage": args.sdk_package,
        "sdkRevision": args.sdk_revision,
        "workflowRepository": args.workflow_repository,
        "workflowName": args.workflow_name,
        "runId": args.run_id,
        "runAttempt": args.run_attempt,
        "artifactId": args.artifact_id,
        "sourceArtifactUrl": args.source_artifact_url,
        "qualificationRunUrl": args.qualification_run_url,
    }
    receipt = qualify(evidence, expected, now=datetime.now(timezone.utc), max_age=timedelta(hours=args.max_age_hours))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print(f"realtime Preview qualification: {receipt['status']} ({args.output})")
    return 1 if args.require_qualified and receipt["status"] != "qualified" else 0


if __name__ == "__main__":
    raise SystemExit(main())
