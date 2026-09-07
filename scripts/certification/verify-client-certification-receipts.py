#!/usr/bin/env python3
"""Fail-closed verdict for the bounded 2026.1 external-client certification roster.

honua-io/honua-server#3434 requires that "missing, skipped, stale, mismatched, or
source-built required cells fail the release gate". This is the check that decides
that, cell by cell, for the bounded roster frozen in
``certification/client-protocol-requirements.v1.json``.

It is deliberately a *reimplementation of the consumer's rules*, not a new contract.
The governed consumer is the ``client-interop-cert-v1`` normalizer in
``honua-io/honua-evidence/scripts/fetch-certification-producers.py``, which rejects a
whole receipt the moment any single result cannot be resolved. Running the same rules
here, in the repository that owns the producers, turns "the ledger shows 59 skips"
into a named defect per cell that a producer change can retire one at a time.

Two modes, both fail-closed:

``--mode contract`` (default, PR tier)
    No candidate exists yet, so no receipt can be admitted. The check reports, for
    every governed row, whether a honua-server producer *could* emit a joining
    receipt, using the frozen mirror's ``receiptBinding``. It re-derives that binding
    from the same repository authorities the generator used, so a producer that
    lands without refreshing the mirror is caught rather than assumed.

``--mode release``
    Binds the receipts under ``--receipts`` to an exact candidate
    (``--source-sha``/``--image-digest``/``--cut-at``/``--producer-source-sha``) and
    applies the full join. Every governed row must resolve to exactly one ``pass``.

Exit status is 0 only when every required cell is ``pass``. Anything else -- a
missing envelope, a skip, a stale or mismatched digest, a source-built server, an
unresolvable test ID -- exits 1 with the reason named.

Usage::

    python3 scripts/certification/verify-client-certification-receipts.py
    python3 scripts/certification/verify-client-certification-receipts.py \
        --mode release --receipts docker/client-compat/output \
        --source-sha <40-hex> --image-digest sha256:<64-hex> \
        --cut-at 2026-09-01T00:00:00Z --producer-source-sha <40-hex>
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import urlparse

ROOT_MARKER = "Honua.sln"
REQUIREMENTS_RELATIVE_PATH = "certification/client-protocol-requirements.v1.json"
SCHEMA = "honua.client-certification-requirements/v1"

SHA_RE = re.compile(r"^[0-9a-f]{40}$")
DIGEST_RE = re.compile(r"^sha256:[0-9a-f]{64}$")

# Reason codes are the vocabulary the release gate reports. They are closed: a new
# failure mode gets a new code rather than being folded into an existing one, so a
# retired defect is visibly retired.
REASON_CODES = (
    # contract tier
    "producer-lane-not-emitted",
    "producer-surface-not-emitted",
    "producer-client-version-mismatch",
    "producer-lane-not-baselined",
    "denominator-unjoinable",
    "mirror-stale",
    "no-candidate",
    # release tier
    "missing-envelope",
    "malformed-envelope",
    "receipt-field-missing",
    "status-not-governed",
    "candidate-sha-mismatch",
    "candidate-digest-mismatch",
    "source-built-candidate",
    "producer-sha-mismatch",
    "revision-mismatch",
    "stale-observation",
    "ambiguous-cell",
    "cell-skipped",
    "cell-failed",
    "provenance-missing",
    "facets-not-exercised",
)


def repository_root(start: Path) -> Path:
    for candidate in [start, *start.parents]:
        if (candidate / ROOT_MARKER).is_file():
            return candidate
    raise SystemExit(f"could not locate {ROOT_MARKER} above {start}")


def load_json(path: Path) -> dict:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError(f"{path} must contain a JSON object")
    return value


def cell_id(requirement: dict) -> str:
    """The governed cell identity: surface, operation, client, version, target."""
    return "|".join((
        requirement["surface"], requirement["operation"], requirement["canonical_client"],
        requirement["client_version"], requirement["deployment_target"],
    ))


def parse_timestamp(value: str, field: str) -> datetime:
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as error:
        raise ValueError(f"{field} is not an RFC 3339 timestamp: {value!r}") from error
    if parsed.tzinfo is None:
        raise ValueError(f"{field} must carry a timezone: {value!r}")
    return parsed.astimezone(timezone.utc)


# ---------------------------------------------------------------------------
# contract tier
# ---------------------------------------------------------------------------

def verify_contract(requirements: dict, root: Path) -> list[dict]:
    """Report, per governed row, whether a joining receipt could be produced today.

    The mirror's stored ``receiptBinding`` is not trusted on its own: the producer
    half is recomputed from ``expected-pairs.json`` and the committed baselines, so
    a lane that starts (or stops) emitting a governed pair is reported here even if
    nobody refreshed the mirror.
    """
    builder = _load_builder(root)
    pairs = builder.emitted_pairs(root)

    verdicts = []
    for requirement in requirements["requirements"]:
        recomputed_producer = builder.classify_producer(requirement, pairs)
        recomputed_join = builder.classify_denominator_join(requirement)
        stored = requirement["receiptBinding"]

        drift = []
        if stored["producerBinding"]["reasonCode"] != recomputed_producer["reasonCode"]:
            drift.append(
                f"producerBinding.reasonCode is {stored['producerBinding']['reasonCode']!r} "
                f"but the repository says {recomputed_producer['reasonCode']!r}")
        if stored["denominatorJoin"]["reasonCode"] != recomputed_join["reasonCode"]:
            drift.append(
                f"denominatorJoin.reasonCode is {stored['denominatorJoin']['reasonCode']!r} "
                f"but the denominator says {recomputed_join['reasonCode']!r}")

        if drift:
            verdicts.append(_verdict(requirement, "fail", blockers=[_blocker(
                "mirror-stale",
                "The frozen mirror disagrees with the repository: " + "; ".join(drift) +
                ". Re-run scripts/certification/build-client-protocol-requirements.py.",
                "honua-io/honua-server")]))
            continue

        # Both halves are reported, not just the first one hit. A cell blocked on
        # both a missing producer and an unjoinable denominator needs both fixed,
        # and each is owned by a different repository, so collapsing them into one
        # reason would hide half the work.
        blockers = []
        if recomputed_producer["status"] == "absent":
            blockers.append(_blocker(
                f"producer-{recomputed_producer['reasonCode']}",
                recomputed_producer["reason"], "honua-io/honua-server"))
        if recomputed_join["status"] == "unjoinable":
            blockers.append(_blocker(
                "denominator-unjoinable", recomputed_join["reason"], "honua-io/honua-release"))

        if not blockers:
            blockers.append(_blocker(
                "no-candidate",
                "A producer and a denominator join both exist, but no exact candidate image was "
                "supplied, so no receipt has been admitted. Re-run with --mode release.",
                "honua-io/honua-release"))
        verdicts.append(_verdict(requirement, "skip", blockers=blockers))
    return verdicts


def _load_builder(root: Path):
    """Import the mirror generator so both halves share one classification."""
    import importlib.util

    script = root / "scripts" / "certification" / "build-client-protocol-requirements.py"
    spec = importlib.util.spec_from_file_location("build_client_protocol_requirements", script)
    if spec is None or spec.loader is None:
        raise SystemExit(f"could not load {script}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _blocker(reason_code: str, reason: str, owner: str) -> dict:
    if reason_code not in REASON_CODES:
        raise ValueError(f"unknown reason code {reason_code!r}")
    return {"reason_code": reason_code, "reason": reason, "owner": owner}


def _verdict(requirement: dict, result: str, blockers: list[dict] | None = None) -> dict:
    blockers = blockers or []
    return {
        "cell": cell_id(requirement),
        "capability_key": requirement["capability_key"],
        "surface": requirement["surface"],
        "operation": requirement["operation"],
        "canonical_client": requirement["canonical_client"],
        "client_lane": requirement["client_lane"],
        "client_version": requirement["client_version"],
        "deployment_target": requirement["deployment_target"],
        "required_tier": requirement["required_tier"],
        "result": result,
        "reason_code": blockers[0]["reason_code"] if blockers else None,
        "reason": blockers[0]["reason"] if blockers else None,
        "blockers": blockers,
    }


# ---------------------------------------------------------------------------
# release tier
# ---------------------------------------------------------------------------

def _envelope_defects(envelope: dict, contract: dict, candidate: dict) -> list[str]:
    """Every reason this envelope may not be admitted, as ``code: detail`` strings."""
    defects: list[str] = []
    missing = [field for field in contract["requiredEnvelopeFields"] if field not in envelope]
    if missing:
        defects.append(f"receipt-field-missing: envelope omits {sorted(missing)}")
        return defects

    if envelope["schema_version"] != contract["envelopeSchemaVersion"]:
        defects.append(
            f"malformed-envelope: schema_version {envelope['schema_version']!r} is not "
            f"{contract['envelopeSchemaVersion']!r}")
    if not isinstance(envelope["results"], list):
        defects.append("malformed-envelope: results is not an array")

    if not SHA_RE.fullmatch(str(envelope["server_commit"])):
        defects.append(
            "source-built-candidate: server_commit is not an exact 40-character commit, so the "
            "receipt cannot name the build it certified")
    elif envelope["server_commit"] != candidate["source_sha"]:
        defects.append("candidate-sha-mismatch: server_commit is not the candidate SHA")

    if not DIGEST_RE.fullmatch(str(envelope["image_digest"])):
        defects.append(
            "source-built-candidate: image_digest is not a sha256 registry digest, so the run "
            "did not execute against the immutable candidate image")
    elif envelope["image_digest"] != candidate["image_digest"]:
        defects.append("candidate-digest-mismatch: image_digest is not the candidate digest")

    if envelope["producer_source_sha"] != candidate["producer_source_sha"]:
        defects.append("producer-sha-mismatch: producer_source_sha is not the trusted run SHA")

    for field in ("client_id", "runner_lane", "protocol_version", "protocol_profile"):
        if not isinstance(envelope[field], str) or not envelope[field].strip():
            defects.append(f"malformed-envelope: {field} must be a non-empty string")

    try:
        observed_at = parse_timestamp(str(envelope["run_date"]), "run_date")
        if observed_at < candidate["cut_at"]:
            defects.append(
                "stale-observation: run_date precedes the frozen candidate cut, so this receipt "
                "observed an earlier build")
    except ValueError as error:
        defects.append(f"malformed-envelope: {error}")

    return defects


def _result_defects(result: dict, requirement: dict, envelope: dict, contract: dict) -> list[str]:
    defects: list[str] = []
    missing = [field for field in contract["requiredResultFields"] if field not in result]
    if missing:
        defects.append(f"provenance-missing: result omits {sorted(missing)}")
        return defects

    if result["status"] not in contract["resultStatusVocabulary"]:
        defects.append(
            f"status-not-governed: status {result['status']!r} is not one of "
            f"{contract['resultStatusVocabulary']}; the governed consumer rejects the whole "
            "receipt rather than reinterpreting it")
        return defects

    performed_by = result["performed_by"]
    if not isinstance(performed_by, str) or performed_by != envelope["client_id"]:
        defects.append(
            "provenance-missing: performed_by must name the application client that made the "
            "request and equal client_id")
    if envelope["client_id"] != requirement["canonical_client"]:
        defects.append(
            f"provenance-missing: client_id {envelope['client_id']!r} is not the governed client "
            f"{requirement['canonical_client']!r}")

    request_url = result["request_url"]
    if result["status"] == "skip" and request_url is None:
        pass
    else:
        parsed = urlparse(request_url) if isinstance(request_url, str) else None
        if (
            parsed is None or parsed.scheme not in {"http", "https"} or not parsed.netloc
            or parsed.username is not None or parsed.password is not None
        ):
            defects.append("provenance-missing: request_url must be an absolute credential-free URL")

    exercised = result["exercised_capabilities"]
    if not (
        isinstance(exercised, list) and exercised
        and all(isinstance(value, str) and value for value in exercised)
        and len(exercised) == len(set(exercised))
    ):
        defects.append("provenance-missing: exercised_capabilities must be a unique non-empty string array")
    elif result["status"] == "pass" and not set(requirement["scenario_facets"]).issubset(exercised):
        missing_facets = sorted(set(requirement["scenario_facets"]) - set(exercised))
        defects.append(
            f"facets-not-exercised: the governed row requires {missing_facets} which this pass "
            "did not exercise")

    return defects


def _revision_defects(envelope: dict, requirement: dict, candidate: dict) -> list[str]:
    expected_fixture = requirement["fixture_revision"].replace("{source_sha}", candidate["source_sha"])
    defects = []
    for field, expected in (
        ("fixture_revision", expected_fixture),
        ("server_config_revision", requirement["contract_revision"]),
        ("auth_policy_revision", requirement["auth_policy_revision"]),
    ):
        if envelope[field] != expected:
            defects.append(
                f"revision-mismatch: {field} is {envelope[field]!r} but the governed row requires "
                f"{expected!r}")
    return defects


def verify_release(requirements: dict, receipts: list[tuple[str, dict]], candidate: dict) -> list[dict]:
    """Apply the governed join to real receipts and emit one verdict per governed row."""
    contract = requirements["receiptContract"]
    verdicts: list[dict] = []

    for requirement in requirements["requirements"]:
        test_ids = set(requirement.get("test_ids") or ())
        if not test_ids:
            verdicts.append(_verdict(requirement, "skip", blockers=[_blocker(
                "denominator-unjoinable",
                requirement["receiptBinding"]["denominatorJoin"]["reason"],
                "honua-io/honua-release")]))
            continue

        matches = [
            (name, envelope, result)
            for name, envelope in receipts
            if envelope.get("runner_lane") == requirement["client_lane"]
            and envelope.get("client_version") == requirement["client_version"]
            and envelope.get("protocol") == requirement["surface"]
            for result in envelope.get("results", []) + envelope.get("extensions", [])
            if isinstance(result, dict) and result.get("test_case_id") in test_ids
            and result.get("status") != "not_applicable"
        ]

        if not matches:
            verdicts.append(_verdict(requirement, "skip", blockers=[_blocker(
                "missing-envelope",
                "No admitted receipt carries a governed test ID for lane "
                f"{requirement['client_lane']!r} / surface {requirement['surface']!r} at "
                f"client_version {requirement['client_version']!r}.",
                "honua-io/honua-server")]))
            continue
        if len({name for name, _, _ in matches}) > 1:
            verdicts.append(_verdict(requirement, "fail", blockers=[_blocker(
                "ambiguous-cell",
                "More than one producer receipt claims this cell: "
                + ", ".join(sorted({name for name, _, _ in matches})),
                "honua-io/honua-server")]))
            continue

        name, envelope, result = matches[0]
        defects = _envelope_defects(envelope, contract, candidate)
        # A structurally broken envelope stops here -- the later checks read fields
        # it does not have. Everything else accumulates, so one run names every
        # defect instead of forcing a fix-one-see-the-next loop.
        if not any(defect.startswith(("receipt-field-missing", "malformed-envelope"))
                   for defect in defects):
            defects += _revision_defects(envelope, requirement, candidate)
            defects += _result_defects(result, requirement, envelope, contract)

        if defects:
            # Every defect is reported: a receipt that is both off-candidate and
            # missing provenance needs both fixed before it can be admitted.
            verdicts.append(_verdict(requirement, "fail", blockers=[
                _blocker(code, f"{name}: {detail}", "honua-io/honua-server")
                for code, _, detail in (defect.partition(": ") for defect in defects)]))
            continue

        if result["status"] == "skip":
            verdicts.append(_verdict(requirement, "skip", blockers=[_blocker(
                "cell-skipped",
                f"{name}: the governed cell was skipped, which the release gate fails closed on.",
                "honua-io/honua-server")]))
        elif result["status"] == "fail":
            verdicts.append(_verdict(requirement, "fail", blockers=[_blocker(
                "cell-failed", f"{name}: {result.get('notes') or 'reported fail'}",
                "honua-io/honua-server")]))
        else:
            verdicts.append(_verdict(requirement, "pass"))

    return verdicts


def read_receipts(directory: Path) -> list[tuple[str, dict]]:
    receipts = []
    for path in sorted(directory.rglob("*.cert.json")):
        try:
            receipts.append((path.name, load_json(path)))
        except (json.JSONDecodeError, ValueError) as error:
            raise SystemExit(f"malformed receipt {path}: {error}")
    return receipts


def summarize(verdicts: list[dict]) -> dict:
    counts: dict[str, int] = {"pass": 0, "fail": 0, "skip": 0}
    reasons: dict[str, int] = {}
    owners: dict[str, int] = {}
    for verdict in verdicts:
        counts[verdict["result"]] = counts.get(verdict["result"], 0) + 1
        for blocker in verdict["blockers"]:
            reasons[blocker["reason_code"]] = reasons.get(blocker["reason_code"], 0) + 1
            owners[blocker["owner"]] = owners.get(blocker["owner"], 0) + 1
    return {
        "requiredCells": len(verdicts),
        "byResult": counts,
        "byBlockerReasonCode": dict(sorted(reasons.items())),
        "byBlockerOwner": dict(sorted(owners.items())),
        "green": counts["pass"] == len(verdicts) and bool(verdicts),
    }


def build_report(requirements: dict, verdicts: list[dict], mode: str, candidate: dict | None) -> dict:
    return {
        "schema": "honua.client-certification-verdict/v1",
        "mode": mode,
        "requirementsRevision": requirements["revision"],
        "requirementsSourceRevision": requirements["requirements_source_revision"],
        "denominatorRevision": requirements["requirements_source_denominator_revision"],
        "candidate": None if candidate is None else {
            "source_sha": candidate["source_sha"],
            "image_digest": candidate["image_digest"],
            "cut_at": candidate["cut_at"].isoformat().replace("+00:00", "Z"),
            "producer_source_sha": candidate["producer_source_sha"],
        },
        "summary": summarize(verdicts),
        "cells": verdicts,
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Verify the bounded client-certification roster.")
    parser.add_argument("--mode", choices=("contract", "release"), default="contract")
    parser.add_argument("--root", type=Path, default=None)
    parser.add_argument("--requirements", type=Path, default=None)
    parser.add_argument("--receipts", type=Path, default=None)
    parser.add_argument("--source-sha")
    parser.add_argument("--image-digest")
    parser.add_argument("--cut-at")
    parser.add_argument("--producer-source-sha")
    parser.add_argument("--output", type=Path, default=None, help="write the verdict JSON here")
    args = parser.parse_args(argv)

    root = args.root or repository_root(Path(__file__).resolve().parent)
    requirements = load_json(args.requirements or (root / REQUIREMENTS_RELATIVE_PATH))
    if requirements.get("schema") != SCHEMA:
        raise SystemExit(f"requirements schema must be {SCHEMA}")

    candidate = None
    if args.mode == "release":
        for flag, value in (
            ("--receipts", args.receipts), ("--source-sha", args.source_sha),
            ("--image-digest", args.image_digest), ("--cut-at", args.cut_at),
            ("--producer-source-sha", args.producer_source_sha),
        ):
            if not value:
                raise SystemExit(f"{flag} is required in release mode; the gate never assumes a candidate")
        if not SHA_RE.fullmatch(args.source_sha) or not SHA_RE.fullmatch(args.producer_source_sha):
            raise SystemExit("candidate and producer SHAs must be lowercase 40-character commits")
        if not DIGEST_RE.fullmatch(args.image_digest):
            raise SystemExit("--image-digest must be a lowercase sha256 registry digest")
        candidate = {
            "source_sha": args.source_sha,
            "image_digest": args.image_digest,
            "cut_at": parse_timestamp(args.cut_at, "--cut-at"),
            "producer_source_sha": args.producer_source_sha,
        }
        verdicts = verify_release(requirements, read_receipts(args.receipts), candidate)
    else:
        verdicts = verify_contract(requirements, root)

    report = build_report(requirements, verdicts, args.mode, candidate)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")

    summary = report["summary"]
    print(f"mode={args.mode} requiredCells={summary['requiredCells']} {summary['byResult']}")
    for code, count in summary["byBlockerReasonCode"].items():
        print(f"  {code}: {count}")
    for owner, count in summary["byBlockerOwner"].items():
        print(f"  owner {owner}: {count}")
    if summary["green"]:
        print("PASS: every required bounded-roster cell has a joined, candidate-bound pass.")
        return 0
    print(
        "FAIL: the bounded 2026.1 external-client roster is not certified. Missing, skipped, "
        "stale, mismatched and source-built required cells fail closed.")
    return 1


if __name__ == "__main__":
    sys.exit(main())
