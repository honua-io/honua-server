#!/usr/bin/env python3
"""Fail closed when exact-candidate Processes evidence regresses from baseline."""

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path


DIGEST_REFERENCE = re.compile(r"@(?P<digest>sha256:[0-9a-f]{64})$")
SOURCE_SHA = re.compile(r"^[0-9a-f]{40}$")


def _read_object(path: Path, label: str) -> dict:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise ValueError(f"could not read {label}: {exc}") from exc
    if not isinstance(value, dict):
        raise ValueError(f"{label} must be a JSON object")
    return value


def evaluate(payload: dict, baseline: dict, expected_image: str, expected_source: str) -> dict:
    failures: list[str] = []
    match = DIGEST_REFERENCE.search(expected_image)
    if match is None:
        failures.append("expected image is not an immutable image@sha256 digest reference")
        expected_digest = None
    else:
        expected_digest = match.group("digest")
    if SOURCE_SHA.fullmatch(expected_source) is None:
        failures.append("expected source is not a full lowercase Git SHA")

    if payload.get("schema") != "honua.cite-diagnostic/v1":
        failures.append("normalized evidence schema is missing or unsupported")
    if payload.get("status") != "diagnostic-green":
        failures.append(f"normalized evidence is not complete green: {payload.get('status')!r}")
    infrastructure_errors = payload.get("infrastructureErrors")
    if infrastructure_errors != []:
        failures.append("normalized evidence contains infrastructure/accounting errors")

    suite = payload.get("suite") if isinstance(payload.get("suite"), dict) else {}
    fixture = payload.get("fixture") if isinstance(payload.get("fixture"), dict) else {}
    candidate = payload.get("candidate") if isinstance(payload.get("candidate"), dict) else {}
    for actual, key, label in (
        (suite.get("sourceCommit"), "etsSourceCommit", "ETS source commit"),
        (fixture.get("revision"), "fixtureRevision", "fixture revision"),
        (fixture.get("configDigest"), "configDigest", "configuration digest"),
    ):
        if actual != baseline.get(key):
            failures.append(f"{label} differs from the recorded baseline")
    if candidate.get("imageDigest") != expected_digest:
        failures.append("tested image digest does not match the requested candidate digest")
    if candidate.get("sourceSha") != expected_source:
        failures.append("tested source SHA does not match the requested candidate source")

    required = baseline.get("requiredTests")
    if not isinstance(required, list) or not required or not all(isinstance(v, str) for v in required):
        failures.append("recorded baseline requiredTests is missing or invalid")
        required = []
    observations = payload.get("observations")
    if not isinstance(observations, list):
        failures.append("normalized observations are missing")
        observations = []
    by_id: dict[str, list[dict]] = {}
    for observation in observations:
        if not isinstance(observation, dict) or not isinstance(observation.get("testId"), str):
            failures.append("normalized evidence contains an observation without a testId")
            continue
        by_id.setdefault(observation["testId"], []).append(observation)

    required_set = set(required)
    observed_set = set(by_id)
    missing = sorted(required_set - observed_set)
    unexpected = sorted(observed_set - required_set)
    duplicate = sorted(test_id for test_id, rows in by_id.items() if len(rows) != 1)
    regressions = sorted(
        test_id
        for test_id in required_set & observed_set
        if len(by_id[test_id]) == 1 and by_id[test_id][0].get("result") != "pass"
    )
    if missing:
        failures.append("required baseline tests missing: " + ", ".join(missing))
    if unexpected:
        failures.append("unrecorded tests changed the declared denominator: " + ", ".join(unexpected))
    if duplicate:
        failures.append("required tests have duplicate verdicts: " + ", ".join(duplicate))
    if regressions:
        failures.append("baseline pass requirements regressed: " + ", ".join(regressions))

    return {
        "schema": "honua.ogcapi-processes-release-gate/v1",
        "verdict": "pass" if not failures else "fail",
        "candidate": {"image": expected_image, "imageDigest": expected_digest, "sourceSha": expected_source},
        "baseline": {
            "version": baseline.get("version"),
            "requiredTestCount": len(required),
            "etsSourceCommit": baseline.get("etsSourceCommit"),
            "fixtureRevision": baseline.get("fixtureRevision"),
            "configDigest": baseline.get("configDigest"),
        },
        "observedTestCount": len(observations),
        "regressions": regressions,
        "failures": failures,
    }


def _write(report: dict, json_path: Path, summary_path: Path) -> None:
    json_path.parent.mkdir(parents=True, exist_ok=True)
    json_path.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    failures = report["failures"]
    summary_path.write_text(
        "# OGC API Processes release-gate verdict\n\n"
        f"- **Verdict**: {report['verdict']}\n"
        f"- **Candidate image**: `{report['candidate']['image']}`\n"
        f"- **Candidate source**: `{report['candidate']['sourceSha']}`\n"
        f"- **Required baseline tests**: {report['baseline']['requiredTestCount']}\n"
        f"- **Observed tests**: {report['observedTestCount']}\n\n"
        "## Fail-closed findings\n\n"
        + ("\n".join(f"- {failure}" for failure in failures) if failures else "- None")
        + "\n",
        encoding="utf-8",
    )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--baseline", required=True, type=Path)
    parser.add_argument("--expected-image", required=True)
    parser.add_argument("--expected-source", required=True)
    parser.add_argument("--json", required=True, type=Path)
    parser.add_argument("--summary", required=True, type=Path)
    args = parser.parse_args()
    try:
        report = evaluate(
            _read_object(args.input, "normalized evidence"),
            _read_object(args.baseline, "recorded baseline"),
            args.expected_image,
            args.expected_source,
        )
    except ValueError as exc:
        report = {
            "schema": "honua.ogcapi-processes-release-gate/v1",
            "verdict": "fail",
            "candidate": {"image": args.expected_image, "imageDigest": None, "sourceSha": args.expected_source},
            "baseline": {"version": None, "requiredTestCount": 0, "etsSourceCommit": None, "fixtureRevision": None, "configDigest": None},
            "observedTestCount": 0,
            "regressions": [],
            "failures": [str(exc)],
        }
    _write(report, args.json, args.summary)
    return 0 if report["verdict"] == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
