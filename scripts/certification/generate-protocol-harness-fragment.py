#!/usr/bin/env python3
"""Generate fail-closed protocol harness certification evidence from VSTest TRX."""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
import re
import xml.etree.ElementTree as ET
from datetime import datetime
from pathlib import Path

SHA_RE = re.compile(r"^[0-9a-f]{40}$")
DIGEST_RE = re.compile(r"^sha256:[0-9a-f]{64}$")
RESULTS = {"Passed": "pass", "Failed": "fail"}
PROJECT_BY_CLASS = {
    "VectorTileServerEndpointTests": "Honua.Protocols.GeoServices.Tests",
    "FeatureServerTemporalExtentEndpointTests": "Honua.Protocols.GeoServices.Tests",
    "FeatureServerTemporalTests": "Honua.Protocols.GeoServices.Tests",
    "FeatureServerQueryDateBinsTests": "Honua.Protocols.GeoServices.Tests",
    "EdrEndpointsTests": "Honua.Protocols.OgcApi.Tests",
    "SensorThingsReadEndpointsTests": "Honua.Protocols.SensorThings.Tests",
    "SensorThingsIngestEndpointsTests": "Honua.Protocols.SensorThings.Tests",
}
DEFAULT_PROJECT = "Honua.Server.Tests"


def timestamp(value: str) -> datetime:
    parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if parsed.tzinfo is None:
        raise ValueError("timestamps must be timezone-aware")
    return parsed


def canonical_bytes(value: object) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")


def load_contract(path: Path) -> dict:
    value = json.loads(path.read_text(encoding="utf-8"))
    if value.get("schema") != "honua.server-protocol-harness-assignments/v1":
        raise ValueError("unsupported protocol harness assignment schema")
    assignments = value.get("assignments")
    if not isinstance(assignments, list) or not assignments:
        raise ValueError("assignments must be a non-empty array")
    for index, assignment in enumerate(assignments):
        test_ids = assignment.get("test_ids")
        if not isinstance(test_ids, list) or not test_ids or len(test_ids) != len(set(test_ids)):
            raise ValueError(f"assignments[{index}].test_ids must be non-empty and unique")
    return value


def test_project(test_id: str) -> str:
    return PROJECT_BY_CLASS.get(test_id.split(".", 1)[0], DEFAULT_PROJECT)


def test_filter(contract: dict, project: str | None = None) -> str:
    test_ids = sorted({
        test_id
        for row in contract["assignments"]
        for test_id in row["test_ids"]
        if project is None or test_project(test_id) == project
    })
    if not test_ids:
        raise ValueError(f"no governed tests belong to project {project}")
    return "|".join(f"FullyQualifiedName~{test_id}" for test_id in test_ids)


def parse_trx(path: Path, expected_ids: set[str]) -> dict[str, str]:
    root = ET.parse(path).getroot()
    summaries = [node for node in root.iter() if node.tag.endswith("ResultSummary")]
    counters = [node for node in root.iter() if node.tag.endswith("Counters")]
    if len(summaries) != 1 or len(counters) != 1:
        raise ValueError("TRX must contain exactly one ResultSummary and Counters element")
    if summaries[0].attrib.get("outcome") not in {"Completed", "Failed"}:
        raise ValueError("TRX ResultSummary outcome is not complete")
    counts = counters[0].attrib
    try:
        total = int(counts["total"])
        executed = int(counts["executed"])
        passed = int(counts["passed"])
        failed = int(counts["failed"])
        not_executed = int(counts.get("notExecuted", "0"))
    except (KeyError, ValueError) as error:
        raise ValueError("TRX Counters are incomplete") from error
    if total <= 0 or executed != total or not_executed != 0 or passed + failed != executed:
        raise ValueError("TRX records incomplete test execution")
    result_nodes = [node for node in root.iter() if node.tag.endswith("UnitTestResult")]
    if len(result_nodes) != total:
        raise ValueError("TRX Counters do not match result count")

    definitions: dict[str, str] = {}
    for node in root.iter():
        if not node.tag.endswith("UnitTest"):
            continue
        trx_id = node.attrib.get("id", "")
        methods = [child for child in node.iter() if child.tag.endswith("TestMethod")]
        if not trx_id or len(methods) != 1:
            raise ValueError("TRX contains a malformed test definition")
        class_name = methods[0].attrib.get("className", "").rsplit(".", 1)[-1]
        method_name = methods[0].attrib.get("name", "")
        if not class_name or not method_name:
            raise ValueError(f"TRX test definition {trx_id} has no canonical method identity")
        if trx_id in definitions:
            raise ValueError(f"TRX contains duplicate test definition id {trx_id}")
        definitions[trx_id] = f"{class_name}.{method_name}"

    matched: dict[str, str] = {}
    for node in result_nodes:
        trx_id = node.attrib.get("testId", "")
        if not trx_id or trx_id not in definitions:
            raise ValueError(f"TRX result has no matching test definition: {trx_id or '<missing>'}")
        test_id = definitions[trx_id]
        if test_id not in expected_ids:
            raise ValueError(f"TRX contains ungoverned selected test result {test_id}")
        if test_id in matched:
            raise ValueError(f"duplicate governed test result {test_id}")
        outcome = node.attrib.get("outcome")
        if outcome not in RESULTS:
            raise ValueError(f"governed test {test_id} has unsupported outcome {outcome!r}")
        matched[test_id] = RESULTS[outcome]
    if sum(value == "pass" for value in matched.values()) != passed or sum(value == "fail" for value in matched.values()) != failed:
        raise ValueError("TRX Counters do not match result outcomes")
    missing = sorted(expected_ids - set(matched))
    if missing:
        raise ValueError(f"missing governed test results: {', '.join(missing)}")
    return matched


def build_fragment(contract: dict, outcomes: dict[str, str], args: argparse.Namespace) -> dict:
    for value, pattern, label in (
        (args.source_sha, SHA_RE, "source SHA"),
        (args.producer_source_sha, SHA_RE, "producer source SHA"),
        (args.image_digest, DIGEST_RE, "image digest"),
    ):
        if pattern.fullmatch(value) is None:
            raise ValueError(f"invalid {label}")
    started = timestamp(args.started_at)
    completed = timestamp(args.completed_at)
    generated = timestamp(args.generated_at)
    cut = timestamp(args.candidate_cut_at)
    if not cut <= started <= completed <= generated:
        raise ValueError("candidate/test/generation timestamps are not monotonic")
    execution_image_digest = (
        None if contract["deployment_target"] == "source-test-host" else args.image_digest
    )
    revision = contract["revision"]
    producer_sha = args.producer_source_sha
    client_version = f"source@{producer_sha}"
    fixture_revision = f"server-test-fixtures@{producer_sha}"
    contract_revision = f"server-protocol-harness@{revision}+{producer_sha}"
    observations = []
    for assignment in contract["assignments"]:
        test_ids = assignment["test_ids"]
        result = "pass" if all(outcomes[test_id] == "pass" for test_id in test_ids) else "fail"
        facets = {facet: result for facet in assignment["scenario_facets"]}
        identity = {
            "capability_key": assignment["capability_key"],
            "surface": assignment["surface"],
            "operation": assignment["operation"],
            "canonical_client": contract["canonical_client"],
            "client_version": client_version,
            "deployment_target": contract["deployment_target"],
            "source_sha": args.source_sha,
            "producer_source_sha": producer_sha,
            "image_digest": execution_image_digest,
            "fixture_revision": fixture_revision,
            "contract_revision": contract_revision,
            "auth_policy_revision": contract["auth_policy_revision"],
            "started_at": args.started_at,
            "completed_at": args.completed_at,
            "test_ids": test_ids,
        }
        payload = {
            "contract_revision": contract_revision,
            "test_outcomes": {test_id: outcomes[test_id] for test_id in test_ids},
        }
        receipt = {
            "schema": "honua.certification-evidence-receipt/v1",
            "identity": identity,
            "result": result,
            "facets": facets,
            "payload_base64": base64.b64encode(canonical_bytes(payload)).decode("ascii"),
        }
        digest = "sha256:" + hashlib.sha256(canonical_bytes(receipt)).hexdigest()
        observations.append({
            "surface": assignment["surface"],
            "operation": assignment["operation"],
            "canonical_client": contract["canonical_client"],
            "client_version": client_version,
            "deployment_target": contract["deployment_target"],
            "test_ids": test_ids,
            "result": result,
            "skip_reason": None,
            "source_sha": args.source_sha,
            "producer_source_sha": producer_sha,
            "image_digest": execution_image_digest,
            "fixture_revision": fixture_revision,
            "contract_revision": contract_revision,
            "auth_policy_revision": contract["auth_policy_revision"],
            "evidence_uri": f"https://evidence.honua.io/data/sha256/{digest[7:]}",
            "evidence_digest": digest,
            "evidence_receipt": receipt,
            "facet_results": {
                facet: {"result": facet_result, "evidence_digest": digest}
                for facet, facet_result in facets.items()
            },
            "started_at": args.started_at,
            "completed_at": args.completed_at,
            "budget_observations": None,
        })
    return {
        "schema": "honua.protocol-certification-fragment/v1",
        "producer": "server-protocol-harness",
        "generated_at": args.generated_at,
        "candidate": {
            "source_sha": args.source_sha,
            "image_digest": args.image_digest,
            "cut_at": args.candidate_cut_at,
        },
        "operation_scope": {
            "complete": True,
            "expected": len(contract["assignments"]),
            "observed": len(observations),
        },
        "observations": observations,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--contract", required=True, type=Path)
    parser.add_argument("--print-filter", action="store_true")
    parser.add_argument("--project")
    parser.add_argument("--trx", action="append", type=Path)
    parser.add_argument("--trx-exit-code", action="append", type=int)
    parser.add_argument("--source-sha")
    parser.add_argument("--producer-source-sha")
    parser.add_argument("--image-digest")
    parser.add_argument("--candidate-cut-at")
    parser.add_argument("--started-at")
    parser.add_argument("--completed-at")
    parser.add_argument("--generated-at")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    contract = load_contract(args.contract)
    if args.print_filter:
        print(test_filter(contract, args.project))
        return 0
    required = ("trx", "trx_exit_code", "source_sha", "producer_source_sha", "image_digest",
                "candidate_cut_at", "started_at", "completed_at", "generated_at", "output")
    missing = [name for name in required if getattr(args, name) is None]
    if missing:
        parser.error("evidence mode requires " + ", ".join(missing))
    expected = {test_id for row in contract["assignments"] for test_id in row["test_ids"]}
    projects = sorted({test_project(test_id) for test_id in expected})
    if len(args.trx) != len(projects) or len(args.trx_exit_code) != len(projects):
        raise ValueError("evidence mode requires exactly one TRX and exit code per governed project")
    outcomes = {}
    for project, trx_path, exit_code in zip(projects, args.trx, args.trx_exit_code, strict=True):
        project_ids = {test_id for test_id in expected if test_project(test_id) == project}
        project_outcomes = parse_trx(trx_path, project_ids)
        if exit_code not in {0, 1}:
            raise ValueError(f"{project}: dotnet test infrastructure exit code is not evidentiary")
        if exit_code == 0 and any(result == "fail" for result in project_outcomes.values()):
            raise ValueError(f"{project}: TRX failure conflicts with successful dotnet test exit")
        if exit_code == 1 and all(result == "pass" for result in project_outcomes.values()):
            raise ValueError(f"{project}: dotnet test failure is not explained by a governed assertion")
        outcomes.update(project_outcomes)
    fragment = build_fragment(contract, outcomes, args)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(fragment, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
