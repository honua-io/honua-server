#!/usr/bin/env python3
"""Normalize aggregate OGC CITE results into the release federation contract."""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import re
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import urljoin

SHA = re.compile(r"^[0-9a-f]{40}$")
DIGEST = re.compile(r"^sha256:[0-9a-f]{64}$")
FRAGMENT_SCHEMA = "honua.protocol-certification-fragment/v1"

# These are the catalog cells for which the current CITE inventory performs the
# governed operation and all governed facets. Broader catalog rows (notably the
# auth-bearing aggregate rows) are deliberately emitted as skips.
SUITE_BY_SURFACE = {
    "ogc-api-features-1-0": "ogcapi-features",
    "ogc-api-tiles-1-0": "ogcapi-tiles",
    "wfs-1-0": "wfs10",
    "wfs-1-1": "wfs11",
    "wfs-2-0": "wfs20",
    "wcs-2-0": "wcs20",
    "wms-1-3": "wms13",
    "wmts-1-0": "wmts10",
}

FIXTURE_BY_SUITE = {
    "ogcapi-features": "docker/cite/ogc-api-features/seed.sql",
    "ogcapi-tiles": "docker/cite/ogc-api-tiles/seed.sql",
    "wfs10": "docker/cite/shared/test-data",
    "wfs11": "docker/cite/shared/test-data",
    "wfs20": "docker/cite/shared/test-data",
    "wcs20": "docker/cite/wcs20/seed.sql",
    "wms13": "docker/cite/shared/seed/mapserver.sql",
    "wmts10": "docker/cite/shared/seed/mapserver.sql",
}


def timestamp(value: str) -> str:
    parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if parsed.tzinfo is None:
        raise ValueError(f"timestamp must include a timezone: {value}")
    return parsed.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")


def load_json(path: Path) -> dict:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError(f"{path} must contain a JSON object")
    return value


def receipt(
    observation: dict, facets: list[str], payload: dict, candidate_cut_at: str
) -> tuple[dict, str]:
    identity = {
        key: observation[key]
        for key in (
            "capability_key", "surface", "operation", "canonical_client",
            "client_version", "deployment_target", "source_sha",
            "producer_source_sha", "image_digest", "fixture_revision",
            "contract_revision", "auth_policy_revision", "started_at", "completed_at",
        )
    }
    identity["candidate_cut_at"] = candidate_cut_at
    value = {
        "schema": "honua.certification-evidence-receipt/v1",
        "identity": identity,
        "result": observation["result"],
        "facets": {facet: observation["result"] for facet in facets},
        "payload_base64": base64.b64encode(
            json.dumps(payload, sort_keys=True, separators=(",", ":")).encode()
        ).decode(),
    }
    encoded = json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()
    return value, f"sha256:{hashlib.sha256(encoded).hexdigest()}"


def build(args: argparse.Namespace) -> dict:
    if not SHA.fullmatch(args.source_sha) or not SHA.fullmatch(args.producer_source_sha):
        raise ValueError("source and producer SHAs must be lowercase full 40-character commits")
    if not DIGEST.fullmatch(args.image_digest):
        raise ValueError("image digest must be a lowercase sha256 digest")
    generated_at = timestamp(args.completed_at)
    started_at = timestamp(args.started_at)
    cut_at = timestamp(args.candidate_cut)
    if started_at < cut_at:
        raise ValueError("CITE execution must start at or after the frozen candidate cut")
    if generated_at < started_at:
        raise ValueError("CITE execution completed before it started")
    summary = load_json(args.summary)
    catalog = load_json(args.requirements)
    provenance = load_json(args.suite_provenance)
    suites = {suite["id"]: suite for suite in summary.get("suites", [])}
    suite_versions = provenance.get("suites", {})
    if not isinstance(catalog.get("requirements"), list):
        raise ValueError("requirements snapshot has no requirements array")

    observations = []
    for requirement in catalog["requirements"]:
        if requirement.get("canonical_client") != "OGC CITE":
            continue
        suite_id = SUITE_BY_SURFACE.get(requirement["surface"])
        suite = suites.get(suite_id) if suite_id else None
        suite_provenance = suite_versions.get(suite_id) if suite_id else None
        fixture = requirement["fixture_revision"].replace("{source_sha}", args.source_sha)
        actual_fixture = (
            f"{FIXTURE_BY_SUITE[suite_id]}@{args.producer_source_sha}"
            if suite_id in FIXTURE_BY_SUITE else None
        )
        executable = (
            suite is not None
            and suite_provenance is not None
            and suite.get("status") in {"passed", "failed"}
            and int(suite.get("totalTests", 0)) > 0
            and actual_fixture == fixture
        )
        complete_pass = executable and suite["status"] == "passed" and all((
            int(suite.get("passed", -1)) == int(suite["totalTests"]),
            int(suite.get("failed", -1)) == 0,
            int(suite.get("skipped", -1)) == 0,
            int(suite.get("cantTell", -1)) == 0,
        ))
        result = "pass" if complete_pass else "fail" if executable else "skip"
        skip_reason = None
        if not executable:
            if suite_id is None:
                skip_reason = "No current CITE suite maps truthfully to this governed operation and facet set."
            elif suite is None:
                skip_reason = f"Required CITE suite {suite_id} did not produce a normalized result."
            elif suite_provenance is None:
                skip_reason = f"Required CITE suite {suite_id} did not record exact ETS and TEAM Engine versions."
            elif actual_fixture != fixture:
                skip_reason = (
                    f"Required fixture {fixture} was not exercised; CITE used "
                    f"{actual_fixture}."
                )
            else:
                skip_reason = f"Required CITE suite {suite_id} produced no executed tests."

        observation = {
            "capability_key": requirement["capability_key"],
            "surface": requirement["surface"],
            "operation": requirement["operation"],
            "canonical_client": requirement["canonical_client"],
            "client_version": requirement["client_version"],
            "deployment_target": requirement["deployment_target"],
            "client_id": "OGC CITE",
            "runner_lane": requirement["client_lane"],
            "protocol_version": suite_provenance.get("protocol_version", "unexecuted") if suite_provenance else "unexecuted",
            "protocol_profile": suite_provenance.get("protocol_profile", "unexecuted") if suite_provenance else "unexecuted",
            "performed_by": "OGC CITE",
            "request_url": urljoin(args.target_base_url.rstrip("/") + "/", suite_provenance["request_path"].lstrip("/")) if executable else None,
            "exercised_capabilities": list(requirement["scenario_facets"]) if executable else [],
            "result": result,
            "skip_reason": skip_reason,
            "source_sha": args.source_sha,
            "producer_source_sha": args.producer_source_sha,
            "image_digest": args.image_digest,
            "fixture_revision": fixture,
            "contract_revision": requirement["contract_revision"],
            "auth_policy_revision": requirement["auth_policy_revision"],
            "evidence_uri": None,
            "evidence_digest": None,
            "evidence_receipt": None,
            "facet_results": None,
            "started_at": started_at,
            "completed_at": generated_at,
        }
        if "test_ids" in requirement:
            observation["test_ids"] = requirement["test_ids"]
        if result != "skip":
            payload = {
                "suite_id": suite_id,
                "suite_version": suite_provenance["suite_version"],
                "team_engine_version": suite_provenance["team_engine_version"],
                "profile": suite_provenance["protocol_profile"],
                "counts": {key: suite[key] for key in ("totalTests", "passed", "failed", "skipped", "cantTell")},
                "run_url": args.run_url,
            }
            evidence_receipt, evidence_digest = receipt(
                observation, requirement["scenario_facets"], payload, cut_at
            )
            observation["evidence_receipt"] = evidence_receipt
            observation["evidence_digest"] = evidence_digest
            observation["evidence_uri"] = f"https://evidence.honua.io/data/sha256/{evidence_digest.removeprefix('sha256:')}"
            observation["facet_results"] = {
                facet: {"result": result, "evidence_digest": evidence_digest}
                for facet in requirement["scenario_facets"]
            }
        observations.append(observation)

    if not observations:
        raise ValueError("requirements snapshot contains no OGC CITE rows")
    return {
        "schema": FRAGMENT_SCHEMA,
        "producer": "honua-server-cite",
        "generated_at": generated_at,
        "candidate": {
            "source_sha": args.source_sha,
            "image_digest": args.image_digest,
            "cut_at": cut_at,
        },
        "operation_scope": {
            "complete": True,
            "expected": len(observations),
            "observed": len(observations),
        },
        "observations": observations,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--summary", type=Path, required=True)
    parser.add_argument("--requirements", type=Path, required=True)
    parser.add_argument("--suite-provenance", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--producer-source-sha", required=True)
    parser.add_argument("--image-digest", required=True)
    parser.add_argument("--candidate-cut", required=True)
    parser.add_argument("--started-at", required=True)
    parser.add_argument("--completed-at", required=True)
    parser.add_argument("--run-url", required=True)
    parser.add_argument("--target-base-url", default="http://localhost:8080")
    args = parser.parse_args()
    fragment = build(args)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(fragment, indent=2) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
