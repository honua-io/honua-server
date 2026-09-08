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
from urllib.parse import parse_qsl, urlparse

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
    "denominator-ambiguous",
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
    "ambiguous-resolution",
    "unresolved-result",
    "deployment-target-mismatch",
    "receipt-rejected",
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


# Query parameters that commonly carry a secret. A receipt is an uploaded artifact,
# so a URL bearing one of these must never be published -- and a client that
# authenticates this way leaves `urlparse().username` unset, so the userinfo check
# alone does not catch it.
CREDENTIAL_QUERY_KEYS = frozenset({
    "access_token", "api_key", "apikey", "auth", "authorization", "code",
    "id_token", "key", "password", "pwd", "refresh_token", "secret", "session",
    "sig", "signature", "token", "x-api-key",
})


def is_publishable_url(value: object) -> bool:
    """An absolute HTTP(S) URL that carries no credential in userinfo or query."""
    if not isinstance(value, str):
        return False
    try:
        parsed = urlparse(value)
    except ValueError:
        return False
    if parsed.scheme not in {"http", "https"} or not parsed.netloc:
        return False
    if parsed.username is not None or parsed.password is not None:
        return False
    return not any(
        key.strip().lower() in CREDENTIAL_QUERY_KEYS
        for key, _ in parse_qsl(parsed.query, keep_blank_values=True)
    )


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
    collisions = builder.ambiguous_test_ids(requirements["requirements"])

    verdicts = []
    for requirement in requirements["requirements"]:
        recomputed_producer = builder.classify_producer(requirement, pairs)
        recomputed_join = builder.classify_denominator_join(requirement, collisions)
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
                "denominator-ambiguous"
                if recomputed_join["reasonCode"] == "denominator-ambiguous-test-ids"
                else "denominator-unjoinable",
                recomputed_join["reason"], "honua-io/honua-release"))

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
    required = [
        *contract["requiredEnvelopeFields"],
        *contract.get("honuaAdditionalReleaseFields", []),
    ]
    missing = [field for field in required if field not in envelope]
    if missing:
        defects.append(f"receipt-field-missing: envelope omits {sorted(missing)}")
        return defects

    if envelope["schema_version"] != contract["envelopeSchemaVersion"]:
        defects.append(
            f"malformed-envelope: schema_version {envelope['schema_version']!r} is not "
            f"{contract['envelopeSchemaVersion']!r}")
    if not isinstance(envelope["results"], list):
        defects.append("malformed-envelope: results is not an array")
    if not isinstance(envelope.get("extensions", []), list):
        defects.append("malformed-envelope: extensions is not an array")

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
    elif not is_publishable_url(request_url):
        defects.append(
            "provenance-missing: request_url must be an absolute URL carrying no credentials "
            "in userinfo or query parameters")

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
    if (result["status"] == "pass" and "tls" in requirement["scenario_facets"]
            and is_publishable_url(request_url) and urlparse(request_url).scheme != "https"):
        defects.append("provenance-missing: a TLS facet requires an HTTPS request_url")

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


def admit_receipts(
    requirements: dict, receipts: list[tuple[str, dict]], candidate: dict,
) -> tuple[list[tuple[str, dict]], dict[str, list[str]]]:
    """Split receipts into admitted and rejected, whole receipts at a time.

    The governed consumer rejects an *entire* receipt the moment any one of its
    results is malformed or unresolvable -- it never quietly keeps the good rows.
    Admission therefore has to happen before any cell is joined; validating only
    the result a cell happens to match would let a receipt carrying one valid row
    and one malformed row certify that cell.
    """
    contract = requirements["receiptContract"]
    admitted: list[tuple[str, dict]] = []
    rejected: dict[str, list[str]] = {}

    for name, envelope in receipts:
        defects = _envelope_defects(envelope, contract, candidate)
        if not any(defect.startswith(("receipt-field-missing", "malformed-envelope"))
                   for defect in defects):
            defects += _receipt_result_defects(envelope, requirements, contract, candidate)
        if defects:
            rejected[name] = defects
        else:
            admitted.append((name, envelope))
    return admitted, rejected


def _receipt_result_defects(
    envelope: dict, requirements: dict, contract: dict, candidate: dict,
) -> list[str]:
    """Validate every result in one envelope against the rows it claims."""
    defects: list[str] = []
    rows = [
        row for row in requirements["requirements"]
        if row["client_lane"] == envelope.get("runner_lane")
        and row["client_version"] == envelope.get("client_version")
        and row["surface"] == envelope.get("protocol")
    ]
    for index, result in enumerate(
        [*envelope.get("results", []), *envelope.get("extensions", [])]
    ):
        if not isinstance(result, dict):
            defects.append(f"malformed-envelope: result {index} is not an object")
            continue
        if not isinstance(result.get("test_case_id"), str) or not result["test_case_id"].strip():
            defects.append(f"malformed-envelope: result {index} has no non-empty test_case_id")
            continue
        if result.get("status") not in contract["resultStatusVocabulary"]:
            defects.append(
                f"status-not-governed: result {result.get('test_case_id')!r} has status "
                f"{result.get('status')!r}, which is not one of "
                f"{contract['resultStatusVocabulary']}; the governed consumer rejects the whole "
                "receipt rather than reinterpreting it")
            continue
        if result["status"] == "not_applicable":
            continue
        claimed = [row for row in rows if result.get("test_case_id") in (row.get("test_ids") or ())]
        if not claimed:
            # Absence from this mirror is not proof that some other governed row
            # admits the observation. The consumer rejects unresolved executable
            # results wholesale; accepting the other rows here would manufacture
            # a local pass for evidence the release join cannot consume. Producers
            # must publish a bounded receipt, or supply the applicable denominator.
            defects.append(
                f"unresolved-result: result {result['test_case_id']!r} resolves to no "
                "governed requirement for this lane, version and surface")
            continue
        if len(claimed) > 1:
            defects.append(
                f"ambiguous-resolution: result {result['test_case_id']!r} resolves to "
                f"{len(claimed)} governed requirements "
                f"({sorted(row['operation'] for row in claimed)}); the consumer requires exactly one")
            continue
        defects += _revision_defects(envelope, claimed[0], candidate)
        defects += _result_defects(result, claimed[0], envelope, contract)
    return list(dict.fromkeys(defects))


def verify_release(requirements: dict, receipts: list[tuple[str, dict]], candidate: dict) -> list[dict]:
    """Apply the governed join to real receipts and emit one verdict per governed row."""
    verdicts: list[dict] = []
    admitted, rejected = admit_receipts(requirements, receipts, candidate)

    for requirement in requirements["requirements"]:
        binding = requirement["receiptBinding"]["denominatorJoin"]
        test_ids = set(requirement.get("test_ids") or ())
        if not test_ids or binding["status"] == "unjoinable":
            verdicts.append(_verdict(requirement, "skip", blockers=[_blocker(
                "denominator-ambiguous"
                if binding.get("reasonCode") == "denominator-ambiguous-test-ids"
                else "denominator-unjoinable",
                binding["reason"], "honua-io/honua-release")]))
            continue

        # A rejected receipt that claims this cell is reported against the cell, so
        # a producer sees why its evidence was refused instead of a bare "missing".
        by_name = dict(receipts)
        claiming_rejects = {
            name: defects for name, defects in rejected.items()
            if by_name[name].get("runner_lane") == requirement["client_lane"]
            and by_name[name].get("protocol") == requirement["surface"]
        }

        matches = [
            (name, envelope, result)
            for name, envelope in admitted
            if envelope.get("runner_lane") == requirement["client_lane"]
            and envelope.get("client_version") == requirement["client_version"]
            and envelope.get("protocol") == requirement["surface"]
            and envelope.get("deployment_target") == requirement["deployment_target"]
            for result in envelope.get("results", []) + envelope.get("extensions", [])
            if isinstance(result, dict) and result.get("test_case_id") in test_ids
            and result.get("status") != "not_applicable"
        ]

        if not matches:
            if claiming_rejects:
                # The specific code survives: "this receipt was refused, and here is
                # each reason" is what a producer can act on. `receipt-rejected` is
                # only the fallback for a defect with no parsable code.
                verdicts.append(_verdict(requirement, "fail", blockers=[
                    _blocker(
                        code if code in REASON_CODES else "receipt-rejected",
                        f"{name}: the whole receipt was refused - {detail or defect}",
                        "honua-io/honua-server")
                    for name, defects in sorted(claiming_rejects.items())
                    for defect in defects
                    for code, _, detail in (defect.partition(": "),)]))
                continue
            target_mismatch = [
                name for name, envelope in admitted
                if envelope.get("runner_lane") == requirement["client_lane"]
                and envelope.get("client_version") == requirement["client_version"]
                and envelope.get("protocol") == requirement["surface"]
                and envelope.get("deployment_target") != requirement["deployment_target"]
            ]
            if target_mismatch:
                verdicts.append(_verdict(requirement, "fail", blockers=[_blocker(
                    "deployment-target-mismatch",
                    f"{name}: the receipt names deployment_target "
                    f"{dict(admitted)[name].get('deployment_target')!r}, but the governed cell "
                    f"requires {requirement['deployment_target']!r}",
                    "honua-io/honua-server") for name in sorted(target_mismatch)]))
                continue
            verdicts.append(_verdict(requirement, "skip", blockers=[_blocker(
                "missing-envelope",
                "No admitted receipt carries a governed test ID for lane "
                f"{requirement['client_lane']!r} / surface {requirement['surface']!r} at "
                f"client_version {requirement['client_version']!r} on "
                f"{requirement['deployment_target']!r}.",
                "honua-io/honua-server")]))
            continue
        if len(matches) > 1:
            verdicts.append(_verdict(requirement, "fail", blockers=[_blocker(
                "ambiguous-cell",
                f"{len(matches)} observations claim this cell (including results and extensions): "
                + ", ".join(sorted({name for name, _, _ in matches})),
                "honua-io/honua-server")]))
            continue

        name, envelope, result = matches[0]
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
    """Read every receipt under ``directory``, identified by its path.

    The identity is the path relative to ``directory``, not the bare filename:
    nested artifact directories routinely hold same-named receipts from different
    producers, and collapsing them would hide an ambiguous cell.
    """
    receipts = []
    for path in sorted(directory.rglob("*.cert.json")):
        try:
            receipts.append((path.relative_to(directory).as_posix(), load_json(path)))
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
