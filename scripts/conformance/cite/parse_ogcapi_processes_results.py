#!/usr/bin/env python3
"""Normalize OGC API Processes ETS TestNG output as provisional diagnostics."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
import xml.etree.ElementTree as ET
from collections import Counter, defaultdict
from dataclasses import asdict, dataclass
from pathlib import Path


ETS_COMMIT = "75abd1f37fc3aad95163fdce2e33e393b1ba5a88"
ETS_VERSION = "1.4-SNAPSHOT"
FIXTURE_REVISION = "ogcapi-processes-cite-profile-v4"
SHA = re.compile(r"^[0-9a-f]{40}$")
DIGEST = re.compile(r"^sha256:[0-9a-f]{64}$")

CLASS_MAPPINGS = {
    "org.opengis.cite.ogcapiprocesses10.SuitePreconditions": (
        "suite-preconditions", ("positive", "media-schema")
    ),
    "org.opengis.cite.ogcapiprocesses10.general.GeneralHttp": (
        "protocol-http", ("positive", "negative", "media-schema")
    ),
    "org.opengis.cite.ogcapiprocesses10.landingpage.LandingPage": (
        "landing-page", ("positive", "media-schema")
    ),
    "org.opengis.cite.ogcapiprocesses10.conformance.Conformance": (
        "conformance-declaration", ("positive", "media-schema")
    ),
    "org.opengis.cite.ogcapiprocesses10.processlist.ProcessList": (
        "process-list", ("positive", "pagination", "limit", "media-schema")
    ),
    "org.opengis.cite.ogcapiprocesses10.process.Process": (
        "process-description", ("positive", "media-schema")
    ),
    "org.opengis.cite.ogcapiprocesses10.ogcprocessdescription.OGCProcessDescription": (
        "process-description-schema", ("positive", "boundary", "media-schema")
    ),
    "org.opengis.cite.ogcapiprocesses10.jobs.Jobs": (
        "job-lifecycle", ("positive", "negative", "boundary", "media-schema", "recovery")
    ),
    "org.opengis.cite.ogcapiprocesses10.joblist.JobList": (
        "job-list", ("positive", "pagination", "limit", "media-schema")
    ),
}
SUITE_PRECONDITIONS_CLASS = "org.opengis.cite.ogcapiprocesses10.SuitePreconditions"
MANDATORY_ETS_CLASSES = frozenset(CLASS_MAPPINGS)
MANDATORY_VERDICT_CLASSES = MANDATORY_ETS_CLASSES - {SUITE_PRECONDITIONS_CLASS}


@dataclass
class Counts:
    total: int = 0
    passed: int = 0
    failed: int = 0
    skipped: int = 0
    canttell: int = 0

    def add(self, result: str) -> None:
        self.total += 1
        if result == "pass":
            self.passed += 1
        elif result == "fail":
            self.failed += 1
        elif result == "skip":
            self.skipped += 1
        else:
            self.canttell += 1


def _find_result(path: Path) -> Path:
    if path.is_file():
        return path
    matches = sorted(path.rglob("testng-results.xml")) if path.is_dir() else []
    if len(matches) != 1:
        raise ValueError(
            f"Expected exactly one testng-results.xml below {path}, found {len(matches)}"
        )
    return matches[0]


def _normalize_status(status: str) -> str:
    normalized = status.upper()
    if normalized in {"PASS", "PASSED", "SUCCESS"}:
        return "pass"
    if normalized in {"FAIL", "FAILED", "FAILURE"}:
        return "fail"
    if normalized in {"SKIP", "SKIPPED", "IGNORED"}:
        return "skip"
    return "canttell"


def _reason(method: ET.Element) -> str | None:
    for pattern in ("./exception/message", ".//reporter-output/line"):
        node = method.find(pattern)
        if node is not None:
            value = " ".join("".join(node.itertext()).split())
            if value:
                return value[:2000]
    return None


def _sha256(path: Path) -> str:
    return f"sha256:{hashlib.sha256(path.read_bytes()).hexdigest()}"


def _load_provenance(path: Path) -> tuple[dict, list[str]]:
    errors: list[str] = []
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise ValueError(f"Could not read server provenance: {exc}") from exc
    if not isinstance(value, dict):
        raise ValueError("Server provenance must be an object")

    source_sha = value.get("testedHonuaGitSha")
    if not isinstance(source_sha, str) or not SHA.fullmatch(source_sha):
        errors.append("server provenance lacks a full tested Honua git SHA")
    producer_source_sha = value.get("checkedOutHonuaGitSha")
    if not isinstance(producer_source_sha, str) or not SHA.fullmatch(producer_source_sha):
        errors.append("server provenance lacks a full checked-out Honua producer git SHA")
    image_id = value.get("serverImageId")
    if not isinstance(image_id, str) or not DIGEST.fullmatch(image_id):
        errors.append("server provenance lacks an immutable server image ID")
    requested = value.get("requestedServerImage")
    if requested is not None and not isinstance(requested, str):
        errors.append("server provenance requested image must be a string or null")
    return value, errors


def _candidate_image_digest(provenance: dict) -> str | None:
    requested = provenance.get("requestedServerImage")
    if isinstance(requested, str):
        match = re.search(r"@(?P<digest>sha256:[0-9a-f]{64})$", requested)
        if match:
            return match.group("digest")
    image_id = provenance.get("serverImageId")
    return image_id if isinstance(image_id, str) and DIGEST.fullmatch(image_id) else None


def parse_results(
    result_path: Path,
    provenance_path: Path,
    config_path: Path,
    *,
    ets_exit_code: int,
    started_at: str,
    completed_at: str,
    run_url: str,
) -> tuple[dict, int]:
    result_file = _find_result(result_path)
    try:
        root = ET.parse(result_file).getroot()
    except (OSError, ET.ParseError) as exc:
        raise ValueError(f"Could not parse TestNG results: {exc}") from exc
    if root.tag != "testng-results":
        raise ValueError(f"Unexpected TestNG root element {root.tag!r}")

    provenance, infrastructure_errors = _load_provenance(provenance_path)
    if not config_path.is_file():
        infrastructure_errors.append("CITE configuration file is missing")

    evidence_digest = _sha256(result_file)
    config_digest = _sha256(config_path) if config_path.is_file() else None
    source_sha = provenance.get("testedHonuaGitSha")
    producer_source_sha = provenance.get("checkedOutHonuaGitSha")
    image_digest = _candidate_image_digest(provenance)
    observations: list[dict] = []
    totals = Counts()
    class_totals: dict[str, Counts] = defaultdict(Counts)
    seen_ets_classes: set[str] = set()
    invocation_counts: Counter[tuple[str, str, str]] = Counter()

    for test in root.findall(".//test"):
        test_name = test.get("name", "")
        for class_node in test.findall("./class"):
            class_name = class_node.get("name", "")
            seen_ets_classes.add(class_name)
            mapping = CLASS_MAPPINGS.get(class_name)
            for method in class_node.findall("./test-method"):
                result = _normalize_status(method.get("status", "UNKNOWN"))
                if method.get("is-config", "false").lower() == "true":
                    if result != "pass":
                        infrastructure_errors.append(
                            f"configuration method {class_name}#{method.get('name', '')} was {result}"
                        )
                    continue
                if mapping is None:
                    infrastructure_errors.append(
                        f"unmapped ETS class {class_name or '<missing>'}"
                    )
                    operation = "unmapped"
                    facets: tuple[str, ...] = ("positive",)
                else:
                    operation, facets = mapping

                method_name = method.get("name", "")
                signature = method.get("signature", "")
                invocation_key = (class_name, method_name, signature)
                invocation_counts[invocation_key] += 1
                invocation = invocation_counts[invocation_key]
                base_test_id = f"{class_name}#{method_name}"
                test_id = base_test_id if invocation == 1 else f"{base_test_id}[{invocation}]"
                reason = _reason(method)
                observation = {
                    "capabilityKey": "process.ogc-api-processes",
                    "surface": "ogc-api-processes",
                    "operation": operation,
                    "canonicalClient": "OGC TEAM Engine / ets-ogcapi-processes10",
                    "clientVersion": ETS_VERSION,
                    "deploymentTarget": "local-docker-diagnostic",
                    "result": result,
                    "testId": test_id,
                    "testName": test_name,
                    "className": class_name,
                    "methodName": method_name,
                    "methodSignature": signature or None,
                    "scenarioFacets": list(facets),
                    "sourceSha": source_sha,
                    "producerSourceSha": producer_source_sha,
                    "imageDigest": image_digest,
                    "fixtureRevision": FIXTURE_REVISION,
                    "configRevision": config_digest,
                    "authPolicyRevision": "test-bypass-v1",
                    "etsSourceSha": ETS_COMMIT,
                    "evidenceDigest": evidence_digest,
                    "evidenceUri": run_url or None,
                    "startedAt": started_at,
                    "completedAt": completed_at,
                    "reason": reason,
                }
                observations.append(observation)
                totals.add(result)
                class_totals[class_name].add(result)

    raw_values: dict[str, int] = {}
    for attribute in ("total", "passed", "failed", "skipped", "ignored"):
        try:
            value = int(root.get(attribute, "0"))
        except ValueError:
            infrastructure_errors.append(f"TestNG root {attribute} count is not an integer")
            value = -1
        if value < 0:
            infrastructure_errors.append(f"TestNG root {attribute} count is negative")
        raw_values[attribute] = value
    raw_skipped = raw_values["skipped"] + raw_values["ignored"]
    raw_accounted = raw_values["passed"] + raw_values["failed"] + raw_skipped
    if raw_values["total"] != totals.total or raw_accounted != totals.total:
        infrastructure_errors.append(
            "TestNG root totals do not match the exact classified test methods: "
            f"root={raw_values}, observed={asdict(totals)}"
        )
    if ets_exit_code != 0:
        infrastructure_errors.append(f"ETS process exited with nonzero code {ets_exit_code}")
    if totals.total == 0:
        infrastructure_errors.append("ETS emitted zero test verdicts")
    if totals.passed + totals.failed == 0:
        infrastructure_errors.append("ETS emitted an all-skip/CantTell run")
    missing_ets_classes = sorted(MANDATORY_ETS_CLASSES - seen_ets_classes)
    if missing_ets_classes:
        infrastructure_errors.append(
            "ETS omitted mandatory classes: " + ", ".join(missing_ets_classes)
        )
    missing_verdict_classes = sorted(MANDATORY_VERDICT_CLASSES - class_totals.keys())
    if missing_verdict_classes:
        infrastructure_errors.append(
            "ETS omitted mandatory verdict classes: " + ", ".join(missing_verdict_classes)
        )

    complete = not infrastructure_errors
    green = complete and totals.failed == totals.skipped == totals.canttell == 0
    status = "incomplete" if not complete else ("diagnostic-green" if green else "diagnostic-red")
    payload = {
        "schema": "honua.cite-diagnostic/v1",
        "suite": {
            "name": "ets-ogcapi-processes10",
            "version": ETS_VERSION,
            "source": "https://github.com/opengeospatial/ets-ogcapi-processes10",
            "sourceCommit": ETS_COMMIT,
        },
        "status": status,
        "candidate": {
            "sourceSha": source_sha,
            "imageDigest": image_digest,
        },
        "fixture": {
            "revision": FIXTURE_REVISION,
            "configDigest": config_digest,
            "configFile": config_path.name,
        },
        "execution": {
            "startedAt": started_at,
            "completedAt": completed_at,
            "etsExitCode": ets_exit_code,
            "runUrl": run_url or None,
            "resultFile": str(result_file),
            "resultDigest": evidence_digest,
        },
        "totals": asdict(totals),
        "rawTotals": raw_values,
        "classTotals": {
            name: asdict(counts) for name, counts in sorted(class_totals.items())
        },
        "infrastructureErrors": infrastructure_errors,
        "observations": observations,
    }
    return payload, 0 if complete else 2


def write_outputs(payload: dict, summary_path: Path, json_path: Path) -> None:
    totals = payload["totals"]
    success_rate = (100 * totals["passed"] // totals["total"]) if totals["total"] else 0
    rows = []
    for class_name, counts in payload["classTotals"].items():
        rows.append(
            f"| `{class_name}` | {counts['total']} | {counts['passed']} | "
            f"{counts['failed']} | {counts['skipped']} | {counts['canttell']} |"
        )
    errors = payload["infrastructureErrors"]
    json_path.parent.mkdir(parents=True, exist_ok=True)
    json_path.write_text(json.dumps(payload, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    summary_path.write_text(
        "# OGC API Processes 1.0 CITE Diagnostic Results\n\n"
        "## Summary\n\n"
        f"- **Status**: {payload['status']}\n"
        f"- **Total Tests**: {totals['total']}\n"
        f"- **Executed Tests**: {totals['passed'] + totals['failed']}\n"
        f"- **Passed**: {totals['passed']}\n"
        f"- **Failed**: {totals['failed']}\n"
        f"- **Skipped**: {totals['skipped']}\n"
        f"- **CantTell**: {totals['canttell']}\n"
        f"- **Success Rate**: {success_rate}%\n\n"
        "This is a diagnostic lane. Conformance failures are retained as findings; "
        "missing output, accounting drift, runner errors, and all-skip runs fail the lane.\n\n"
        "## Exact class mapping\n\n"
        "| ETS class | Total | Passed | Failed | Skipped | CantTell |\n"
        "|---|---:|---:|---:|---:|---:|\n"
        + ("\n".join(rows) if rows else "| _none_ | 0 | 0 | 0 | 0 | 0 |")
        + "\n\n## Provenance\n\n"
        f"- ETS source commit: `{payload['suite']['sourceCommit']}`\n"
        f"- Server source SHA: `{payload['candidate']['sourceSha']}`\n"
        f"- Server image digest: `{payload['candidate']['imageDigest']}`\n"
        f"- Fixture revision: `{payload['fixture']['revision']}`\n"
        f"- Config digest: `{payload['fixture']['configDigest']}`\n"
        f"- Raw result digest: `{payload['execution']['resultDigest']}`\n\n"
        "## Infrastructure/accounting errors\n\n"
        + ("\n".join(f"- {error}" for error in errors) if errors else "- None")
        + "\n",
        encoding="utf-8",
    )


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--provenance", required=True, type=Path)
    parser.add_argument("--config", required=True, type=Path)
    parser.add_argument("--summary", required=True, type=Path)
    parser.add_argument("--json", required=True, type=Path)
    parser.add_argument("--ets-exit-code", required=True, type=int)
    parser.add_argument("--started-at", required=True)
    parser.add_argument("--completed-at", required=True)
    parser.add_argument("--run-url", default="")
    args = parser.parse_args(argv)
    try:
        payload, exit_code = parse_results(
            args.input,
            args.provenance,
            args.config,
            ets_exit_code=args.ets_exit_code,
            started_at=args.started_at,
            completed_at=args.completed_at,
            run_url=args.run_url,
        )
        write_outputs(payload, args.summary, args.json)
        return exit_code
    except ValueError as exc:
        print(f"OGC API Processes CITE parsing failed: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
