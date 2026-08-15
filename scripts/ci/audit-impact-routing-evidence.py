#!/usr/bin/env python3
"""Build a bounded, fail-closed ledger for CI impact-routing observations."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import stat
import zipfile
from collections import defaultdict
from datetime import datetime, timedelta, timezone
from pathlib import Path, PurePosixPath
from typing import Any


POLICY_CONTRACT = "honua.impact-routing-promotion-policy/v1"
INDEX_CONTRACT = "honua.impact-routing-evidence-index/v1"
LEDGER_CONTRACT = "honua.impact-routing-evidence-ledger/v1"
PR_GATE_CONTRACT = "honua.pr-gate-impact-observation/v3"
NATIVE_CONTRACT = "honua.ci.native-image-impact-observation/v2"
REPOSITORY = "honua-io/honua-server"
DEFAULT_BRANCH = "trunk"
PR_GATE_STREAM = "pr_gate"
NATIVE_STREAM = "native"
PR_GATE_WORKFLOW = ".github/workflows/pr-gate-impact-observe.yml"
NATIVE_WORKFLOW = ".github/workflows/native-image-impact-observe.yml"
SERVING_WORKFLOW = ".github/workflows/serving-image-boundary.yml"
WORKER_WORKFLOW = ".github/workflows/worker-gdal-image.yml"
PR_GATE_ARTIFACT = re.compile(
    r"^pr-gate-impact-docs-only-v3-attempt-(?P<attempt>[1-9][0-9]*)$"
)
NATIVE_ARTIFACT = re.compile(
    r"^native-image-impact-observation-v2-attempt-(?P<attempt>[1-9][0-9]*)$"
)
PR_GATE_RECEIPT = "pr-gate-impact-observation.json"
NATIVE_RECEIPT = "native-image-impact-observation.json"
NATIVE_SUMMARY = "native-image-impact-summary.md"
SHA = re.compile(r"^[0-9a-f]{40}$")
DIGEST = re.compile(r"^[0-9a-f]{64}$")
MAX_ARCHIVE_BYTES = 20 * 1024 * 1024
MAX_RECEIPT_BYTES = 2 * 1024 * 1024
ALLOWED_OBSERVER_EVENTS = {"workflow_run", "workflow_dispatch"}
TERMINAL_CONCLUSIONS = {
    "success", "failure", "cancelled", "timed_out", "action_required",
    "neutral", "skipped", "stale", "startup_failure",
}


def _pairs(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    value: dict[str, Any] = {}
    for key, item in pairs:
        if key in value:
            raise ValueError(f"duplicate JSON key: {key}")
        value[key] = item
    return value


def _constant(value: str) -> None:
    raise ValueError(f"non-finite JSON value is forbidden: {value}")


def load_json(path: Path) -> object:
    with path.open(encoding="utf-8") as handle:
        return json.load(handle, object_pairs_hook=_pairs, parse_constant=_constant)


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(value, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
        newline="\n",
    )


def positive_int(value: object, label: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value < 1:
        raise ValueError(f"{label} must be a positive integer")
    return value


def exact_sha(value: object, label: str) -> str:
    if not isinstance(value, str) or SHA.fullmatch(value) is None:
        raise ValueError(f"{label} must be a full lowercase SHA")
    return value


def exact_digest(value: object, label: str) -> str:
    if not isinstance(value, str) or DIGEST.fullmatch(value) is None:
        raise ValueError(f"{label} must be a lowercase SHA-256 digest")
    return value


def parse_time(value: object, label: str) -> datetime:
    if not isinstance(value, str):
        raise ValueError(f"{label} must be a timestamp")
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as error:
        raise ValueError(f"{label} must be a timestamp") from error
    if parsed.tzinfo is None:
        raise ValueError(f"{label} must include a timezone")
    return parsed.astimezone(timezone.utc)


def load_policy(value: object) -> dict[str, Any]:
    if not isinstance(value, dict) or value.get("contract") != POLICY_CONTRACT:
        raise ValueError("impact-routing promotion policy contract is invalid")
    parse_time(value.get("observation_started_at"), "observation start")
    days = positive_int(value.get("receipt_retention_days"), "receipt retention days")
    if days > 90:
        raise ValueError("receipt retention days exceeds GitHub's policy bound")
    lookback = positive_int(
        value.get("image_outcome_lookback_hours"), "image outcome lookback hours"
    )
    if lookback > 48:
        raise ValueError("image outcome lookback exceeds the policy bound")
    pages = positive_int(value.get("maximum_pages_per_query"), "maximum pages per query")
    downloads = positive_int(value.get("maximum_receipt_downloads"), "maximum downloads")
    if pages > 10 or downloads > 500:
        raise ValueError("GitHub query or download bound is unsafe")
    for field in (
        "minimum_docs_only_heads",
        "minimum_native_heads",
        "minimum_serving_impacted_heads",
        "minimum_worker_impacted_heads",
        "minimum_serving_narrowed_heads",
        "minimum_worker_avoided_heads",
    ):
        positive_int(value.get(field), field.replace("_", " "))
    for field in (
        "require_zero_integrity_failures",
        "require_zero_docs_only_gate_failures",
        "require_successful_authoritative_image_outcomes",
    ):
        if value.get(field) is not True:
            raise ValueError(f"{field} must remain fail closed")
    return dict(value)


def receipt_cutoff(policy: dict[str, Any], now: datetime | None = None) -> datetime:
    current = now or datetime.now(timezone.utc)
    retention = current - timedelta(days=policy["receipt_retention_days"])
    start = parse_time(policy["observation_started_at"], "observation start")
    return max(retention, start)


def git_blob_sha(path: Path) -> str:
    content = path.read_bytes()
    return hashlib.sha1(
        b"blob " + str(len(content)).encode("ascii") + b"\0" + content,
        usedforsecurity=False,
    ).hexdigest()


def current_blobs(root: Path) -> dict[str, str]:
    paths = {
        "pr_gate_observer": PR_GATE_WORKFLOW,
        "pr_gate_classifier": "scripts/ci/classify-pr-gate-impact.py",
        "native_observer": NATIVE_WORKFLOW,
        "native_classifier": "scripts/ci/native-image-impact.py",
        "native_routing_policy": ".github/native-image-impact.json",
        "serving_workflow": SERVING_WORKFLOW,
        "trusted_run_resolver": "scripts/ci/trusted-pr-workflow-run.js",
    }
    blobs = {key: git_blob_sha(root / value) for key, value in paths.items()}
    manifest = [
        {"path": "scripts/ci/native-image-impact.py", "blob_sha": blobs["native_classifier"]},
        {"path": ".github/native-image-impact.json", "blob_sha": blobs["native_routing_policy"]},
        {"path": SERVING_WORKFLOW, "blob_sha": blobs["serving_workflow"]},
        {"path": "scripts/ci/trusted-pr-workflow-run.js", "blob_sha": blobs["trusted_run_resolver"]},
        {"path": NATIVE_WORKFLOW, "blob_sha": blobs["native_observer"]},
    ]
    blobs["native_policy_inputs_sha256"] = hashlib.sha256(
        json.dumps(manifest, separators=(",", ":")).encode("utf-8")
    ).hexdigest()
    return blobs


def flatten_pages(root: Path, collection: str) -> list[dict[str, Any]]:
    files = sorted(root.glob("*.json"))
    if not files:
        raise ValueError(f"{collection} query pages are missing")
    expected_total: int | None = None
    items: list[dict[str, Any]] = []
    for file in files:
        page = load_json(file)
        if not isinstance(page, dict) or not isinstance(page.get("total_count"), int):
            raise ValueError(f"{collection} query page is invalid")
        values = page.get(collection)
        if not isinstance(values, list):
            raise ValueError(f"{collection} query collection is invalid")
        if expected_total is None:
            expected_total = page["total_count"]
        elif expected_total != page["total_count"]:
            raise ValueError(f"{collection} query total changed during pagination")
        if not all(isinstance(item, dict) for item in values):
            raise ValueError(f"{collection} query item is invalid")
        items.extend(values)
    if expected_total is None or len(items) != expected_total:
        raise ValueError(f"{collection} query is truncated")
    identifiers = [item.get("id") for item in items]
    if any(isinstance(item, bool) or not isinstance(item, int) for item in identifiers):
        raise ValueError(f"{collection} query identity is invalid")
    if len(set(identifiers)) != len(identifiers):
        raise ValueError(f"{collection} query contains duplicate identities")
    return items


def _valid_observer_run(run: dict[str, Any], workflow: str, cutoff: datetime) -> bool:
    return (
        isinstance(run.get("id"), int)
        and run["id"] > 0
        and isinstance(run.get("run_attempt"), int)
        and run["run_attempt"] > 0
        and run.get("event") in ALLOWED_OBSERVER_EVENTS
        and run.get("status") == "completed"
        and run.get("conclusion") in TERMINAL_CONCLUSIONS
        and run.get("path") == workflow
        and run.get("head_branch") == DEFAULT_BRANCH
        and SHA.fullmatch(str(run.get("head_sha", ""))) is not None
        and parse_time(run.get("created_at"), "observer run creation")
        >= cutoff - timedelta(hours=1)
        and parse_time(run.get("updated_at"), "observer run update")
        >= parse_time(run.get("created_at"), "observer run creation")
    )


def flatten_artifact_catalogs(root: Path) -> list[dict[str, Any]]:
    items: list[dict[str, Any]] = []
    identifiers: set[int] = set()
    for path in sorted(root.glob("*.json")):
        if not path.stem.isdigit() or int(path.stem) <= 0:
            raise ValueError("artifact catalog filename is invalid")
        run_id = int(path.stem)
        page = load_json(path)
        if not isinstance(page, dict):
            raise ValueError("artifact catalog is invalid")
        values = page.get("artifacts")
        total = page.get("total_count")
        if (
            isinstance(total, bool)
            or not isinstance(total, int)
            or total < 0
            or not isinstance(values, list)
            or len(values) != total
            or total > 100
        ):
            raise ValueError("artifact catalog is truncated")
        for artifact in values:
            if not isinstance(artifact, dict):
                raise ValueError("artifact catalog item is invalid")
            producer = artifact.get("workflow_run")
            if not isinstance(producer, dict) or producer.get("id") != run_id:
                raise ValueError("artifact catalog producer identity is invalid")
            artifact_id = artifact.get("id")
            if (
                isinstance(artifact_id, bool)
                or not isinstance(artifact_id, int)
                or artifact_id <= 0
                or artifact_id in identifiers
            ):
                raise ValueError("artifact catalog identity is invalid")
            identifiers.add(artifact_id)
            items.append(artifact)
    return items


def _discover_stream(
    stream: str,
    runs: list[dict[str, Any]],
    artifacts: list[dict[str, Any]],
    workflow: str,
    artifact_pattern: re.Pattern[str],
    cutoff: datetime,
) -> tuple[list[dict[str, Any]], list[dict[str, Any]], list[dict[str, Any]]]:
    entries: list[dict[str, Any]] = []
    exclusions: list[dict[str, Any]] = []
    failures: list[dict[str, Any]] = []
    by_run: dict[int, list[dict[str, Any]]] = defaultdict(list)
    for artifact in artifacts:
        run_id = artifact.get("workflow_run", {}).get("id")
        if isinstance(run_id, int):
            by_run[run_id].append(artifact)
    seen_runs: set[int] = set()
    for run in runs:
        run_id = run.get("id")
        if not isinstance(run_id, int):
            failures.append({"stream": stream, "reason": "observer-run-id-invalid"})
            continue
        seen_runs.add(run_id)
        try:
            if not _valid_observer_run(run, workflow, cutoff):
                raise ValueError("observer workflow run is invalid")
        except (TypeError, ValueError) as error:
            failures.append({"stream": stream, "producer_run_id": run_id, "reason": str(error)})
            continue
        if run["conclusion"] != "success":
            exclusions.append({
                "stream": stream,
                "producer_run_id": run_id,
                "reason": f"observer-run-{run['conclusion']}",
            })
            continue
        matches = []
        for artifact in by_run.get(run_id, []):
            match = artifact_pattern.fullmatch(str(artifact.get("name", "")))
            if (
                match
                and int(match.group("attempt")) == run["run_attempt"]
                and artifact.get("expired") is False
                and parse_time(artifact.get("created_at"), "artifact creation") >= cutoff
            ):
                matches.append(artifact)
        if not matches:
            exclusions.append({
                "stream": stream,
                "producer_run_id": run_id,
                "reason": "observation-receipt-not-emitted",
            })
            continue
        if len(matches) != 1:
            failures.append({
                "stream": stream,
                "producer_run_id": run_id,
                "reason": "observation-artifact-ambiguous",
            })
            continue
        artifact = matches[0]
        try:
            artifact_id = positive_int(artifact.get("id"), "artifact id")
            size = positive_int(artifact.get("size_in_bytes"), "artifact size")
            if size > MAX_ARCHIVE_BYTES:
                raise ValueError("artifact archive is oversized")
            created = parse_time(artifact.get("created_at"), "artifact creation")
            run_created = parse_time(run["created_at"], "observer run creation")
            run_updated = parse_time(run["updated_at"], "observer run update")
            if created < run_created or created > run_updated + timedelta(minutes=5):
                raise ValueError("artifact creation is outside its producer run")
            if artifact.get("workflow_run", {}).get("head_sha") != run["head_sha"]:
                raise ValueError("artifact workflow head differs from producer")
        except (TypeError, ValueError) as error:
            failures.append({"stream": stream, "producer_run_id": run_id, "reason": str(error)})
            continue
        entries.append({
            "stream": stream,
            "artifact_id": artifact_id,
            "artifact_name": artifact["name"],
            "artifact_created_at": artifact["created_at"],
            "artifact_size_bytes": size,
            "producer_run_id": run_id,
            "producer_run_attempt": run["run_attempt"],
            "producer_event": run["event"],
            "producer_head_sha": run["head_sha"],
            "producer_created_at": run["created_at"],
            "producer_updated_at": run["updated_at"],
            "producer_workflow": workflow,
        })
    for run_id, values in by_run.items():
        if run_id not in seen_runs and any(
            artifact_pattern.fullmatch(str(item.get("name", "")))
            and parse_time(item.get("created_at"), "artifact creation") >= cutoff
            for item in values
        ):
            failures.append({
                "stream": stream,
                "producer_run_id": run_id,
                "reason": "observation-artifact-producer-not-in-bounded-run-catalog",
            })
    return entries, exclusions, failures


def discover(
    pr_gate_runs: Path,
    native_runs: Path,
    pr_gate_artifacts: Path,
    native_artifacts: Path,
    policy_value: object,
    now: datetime | None = None,
) -> dict[str, Any]:
    policy = load_policy(policy_value)
    cutoff = receipt_cutoff(policy, now)
    streams = (
        (PR_GATE_STREAM, pr_gate_runs, pr_gate_artifacts, PR_GATE_WORKFLOW, PR_GATE_ARTIFACT),
        (NATIVE_STREAM, native_runs, native_artifacts, NATIVE_WORKFLOW, NATIVE_ARTIFACT),
    )
    entries: list[dict[str, Any]] = []
    exclusions: list[dict[str, Any]] = []
    failures: list[dict[str, Any]] = []
    for stream, runs_root, artifacts_root, workflow, artifact_pattern in streams:
        found, omitted, invalid = _discover_stream(
            stream,
            flatten_pages(runs_root, "workflow_runs"),
            flatten_artifact_catalogs(artifacts_root),
            workflow,
            artifact_pattern,
            cutoff,
        )
        entries.extend(found)
        exclusions.extend(omitted)
        failures.extend(invalid)
    entries.sort(key=lambda item: (item["stream"], item["producer_run_id"]))
    if len({item["artifact_id"] for item in entries}) != len(entries):
        failures.append({"reason": "artifact identity is duplicated across streams"})
    return {
        "contract": INDEX_CONTRACT,
        "repository": REPOSITORY,
        "cutoff": cutoff.isoformat().replace("+00:00", "Z"),
        "artifacts": entries,
        "exclusions": exclusions,
        "integrity_failures": failures,
    }


def _archive_json(entry: dict[str, Any], root: Path) -> object:
    path = root / f"{entry['artifact_id']}.zip"
    if not path.is_file() or path.stat().st_size < 1 or path.stat().st_size > MAX_ARCHIVE_BYTES:
        raise ValueError("receipt archive is missing or oversized")
    expected = (
        {PR_GATE_RECEIPT}
        if entry["stream"] == PR_GATE_STREAM
        else {NATIVE_RECEIPT, NATIVE_SUMMARY}
    )
    with zipfile.ZipFile(path) as archive:
        members = archive.infolist()
        names = {member.filename for member in members}
        if len(names) != len(members) or names != expected:
            raise ValueError("receipt archive member set is invalid")
        for member in members:
            name = PurePosixPath(member.filename)
            mode = member.external_attr >> 16
            if (
                name.is_absolute()
                or ".." in name.parts
                or "\\" in member.filename
                or member.is_dir()
                or stat.S_ISLNK(mode)
                or member.file_size > MAX_RECEIPT_BYTES
            ):
                raise ValueError("receipt archive contains an unsafe member")
        receipt_name = PR_GATE_RECEIPT if entry["stream"] == PR_GATE_STREAM else NATIVE_RECEIPT
        with archive.open(receipt_name) as stream:
            content = stream.read(MAX_RECEIPT_BYTES + 1)
        if len(content) > MAX_RECEIPT_BYTES:
            raise ValueError("receipt file is oversized")
    return json.loads(
        content.decode("utf-8"),
        object_pairs_hook=_pairs,
        parse_constant=_constant,
    )


def _entry_identity(entry: dict[str, Any], expected_stream: str) -> None:
    if entry.get("stream") != expected_stream:
        raise ValueError("receipt stream differs from index")
    positive_int(entry.get("artifact_id"), "index artifact id")
    positive_int(entry.get("producer_run_id"), "producer run id")
    attempt = positive_int(entry.get("producer_run_attempt"), "producer run attempt")
    pattern = PR_GATE_ARTIFACT if expected_stream == PR_GATE_STREAM else NATIVE_ARTIFACT
    artifact_match = pattern.fullmatch(str(entry.get("artifact_name", "")))
    if not artifact_match or int(artifact_match.group("attempt")) != attempt:
        raise ValueError("index artifact name differs from producer attempt")
    exact_sha(entry.get("producer_head_sha"), "producer head")
    parse_time(entry.get("artifact_created_at"), "artifact creation")


def _validate_pr_gate(entry: dict[str, Any], value: object, blobs: dict[str, str]) -> dict[str, Any]:
    _entry_identity(entry, PR_GATE_STREAM)
    if not isinstance(value, dict) or value.get("contract") != PR_GATE_CONTRACT:
        raise ValueError("PR Gate receipt contract is invalid")
    if (
        value.get("repository") != REPOSITORY
        or value.get("rollout") != "observe"
        or value.get("authoritative_gate") != "full"
        or value.get("trusted_execution") != "default-branch-workflow-run/v1"
        or value.get("gate_workflow_path") != ".github/workflows/pr-gate.yml"
    ):
        raise ValueError("PR Gate receipt trust boundary is invalid")
    if value.get("policy_sha") != entry["producer_head_sha"]:
        raise ValueError("PR Gate receipt policy head differs from producer")
    if (
        value.get("policy_blob_sha") != blobs["pr_gate_classifier"]
        or value.get("resolver_blob_sha") != blobs["trusted_run_resolver"]
        or value.get("observer_workflow_blob_sha") != blobs["pr_gate_observer"]
    ):
        raise ValueError("PR Gate receipt policy inputs are not current")
    pull_request = positive_int(value.get("pull_request"), "PR Gate pull request")
    head = exact_sha(value.get("head_sha"), "PR Gate head")
    exact_sha(value.get("base_sha"), "PR Gate base")
    exact_digest(value.get("files_sha256"), "PR Gate files digest")
    positive_int(value.get("gate_run_id"), "PR Gate run id")
    positive_int(value.get("gate_run_attempt"), "PR Gate run attempt")
    if value.get("gate_run_head_sha") != head or value.get("gate_run_conclusion") not in TERMINAL_CONCLUSIONS:
        raise ValueError("PR Gate authoritative result identity is invalid")
    mode = value.get("mode")
    if mode != "docs-only" or value.get("reason") != "internal-markdown-only":
        raise ValueError("PR Gate docs-only reason is invalid")
    return {
        "stream": PR_GATE_STREAM,
        "pull_request": pull_request,
        "head_sha": head,
        "mode": mode,
        "gate_conclusion": value["gate_run_conclusion"],
        "producer_run_id": entry["producer_run_id"],
        "artifact_id": entry["artifact_id"],
    }


def _validate_native(entry: dict[str, Any], value: object, blobs: dict[str, str]) -> dict[str, Any]:
    _entry_identity(entry, NATIVE_STREAM)
    if not isinstance(value, dict) or value.get("schema") != NATIVE_CONTRACT:
        raise ValueError("native-image receipt contract is invalid")
    if (
        value.get("repository") != REPOSITORY
        or value.get("mode") != "observe"
        or value.get("mutation") != "none"
        or value.get("trusted_execution") != "default-branch-workflow-run/v1"
        or value.get("gate_workflow_path") != ".github/workflows/pr-gate.yml"
    ):
        raise ValueError("native-image receipt trust boundary is invalid")
    if value.get("policy_sha") != entry["producer_head_sha"]:
        raise ValueError("native-image receipt policy head differs from producer")
    expected_blobs = {
        "policy_blob_sha": blobs["native_classifier"],
        "routing_policy_blob_sha": blobs["native_routing_policy"],
        "serving_workflow_blob_sha": blobs["serving_workflow"],
        "resolver_blob_sha": blobs["trusted_run_resolver"],
        "observer_workflow_blob_sha": blobs["native_observer"],
    }
    if any(value.get(field) != expected for field, expected in expected_blobs.items()):
        raise ValueError("native-image receipt policy inputs are not current")
    if value.get("policy_inputs_sha256") != blobs["native_policy_inputs_sha256"]:
        raise ValueError("native-image policy manifest digest is not current")
    pull_request = positive_int(value.get("pull_request"), "native-image pull request")
    head = exact_sha(value.get("head_sha"), "native-image head")
    base = exact_sha(value.get("base_sha"), "native-image base")
    paths = value.get("changed_paths")
    if not isinstance(paths, list) or not paths or not all(isinstance(item, str) for item in paths):
        raise ValueError("native-image changed paths are invalid")
    if len(set(paths)) != len(paths) or any(
        not item
        or "\\" in item
        or PurePosixPath(item).is_absolute()
        or ".." in PurePosixPath(item).parts
        or any(ord(character) < 32 or ord(character) == 127 for character in item)
        for item in paths
    ):
        raise ValueError("native-image changed paths are unsafe")
    paths_digest = exact_digest(value.get("changed_paths_sha256"), "native-image paths digest")
    expected_paths_digest = hashlib.sha256(
        json.dumps(paths, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    ).hexdigest()
    if paths_digest != expected_paths_digest:
        raise ValueError("native-image changed paths digest does not replay")
    positive_int(value.get("gate_run_id"), "native-image gate run id")
    positive_int(value.get("gate_run_attempt"), "native-image gate run attempt")
    if value.get("gate_run_head_sha") != head or value.get("gate_run_conclusion") not in TERMINAL_CONCLUSIONS:
        raise ValueError("native-image gate result identity is invalid")
    legacy = value.get("legacy")
    candidate = value.get("candidate")
    comparison = value.get("comparison")
    if not isinstance(legacy, dict) or not isinstance(candidate, dict) or not isinstance(comparison, dict):
        raise ValueError("native-image routing decision is invalid")
    serving = candidate.get("serving_variants")
    legacy_serving_variants = legacy.get("serving_variants")
    variant_names = {"generic", "lambda", "functions"}
    if not isinstance(serving, dict) or set(serving) != variant_names:
        raise ValueError("native-image serving decision is invalid")
    if (
        not isinstance(legacy_serving_variants, dict)
        or set(legacy_serving_variants) != variant_names
    ):
        raise ValueError("native-image legacy serving decision is invalid")
    values = [
        legacy.get("serving_trigger"),
        legacy.get("worker_trigger"),
        candidate.get("worker_build"),
        *serving.values(),
        *legacy_serving_variants.values(),
    ]
    if not all(isinstance(item, bool) for item in values):
        raise ValueError("native-image decision contains a non-boolean value")
    candidate_serving = any(serving.values())
    legacy_serving = any(legacy_serving_variants.values())
    if legacy["serving_trigger"] != legacy_serving:
        raise ValueError("native-image legacy serving trigger does not replay")
    expected_comparison = {
        "serving_candidate_only": candidate_serving and not legacy["serving_trigger"],
        "serving_legacy_only": legacy["serving_trigger"] and not candidate_serving,
        "worker_candidate_only": candidate["worker_build"] and not legacy["worker_trigger"],
        "worker_legacy_only": legacy["worker_trigger"] and not candidate["worker_build"],
    }
    if comparison != expected_comparison:
        raise ValueError("native-image comparison does not replay")
    return {
        "stream": NATIVE_STREAM,
        "pull_request": pull_request,
        "base_sha": base,
        "head_sha": head,
        "gate_conclusion": value["gate_run_conclusion"],
        "legacy_serving": legacy_serving,
        "legacy_serving_count": sum(
            1 for item in legacy_serving_variants.values() if item
        ),
        "legacy_worker": legacy["worker_trigger"],
        "candidate_serving": candidate_serving,
        "candidate_serving_count": sum(1 for item in serving.values() if item),
        "candidate_worker": candidate["worker_build"],
        "producer_run_id": entry["producer_run_id"],
        "artifact_id": entry["artifact_id"],
    }


def _image_outcome(
    runs: list[dict[str, Any]], observation: dict[str, Any], workflow: str
) -> dict[str, Any]:
    same_head = [
        run for run in runs
        if run.get("head_sha") == observation["head_sha"]
        and run.get("path") == workflow
        and run.get("event") == "pull_request"
    ]
    matches = []
    rejected_ids = []
    for run in same_head:
        pulls = run.get("pull_requests")
        associations = pulls if isinstance(pulls, list) else []
        identity_matches = [
            pull
            for pull in associations
            if isinstance(pull, dict)
            and pull.get("number") == observation["pull_request"]
            and isinstance(pull.get("base"), dict)
            and pull["base"].get("sha") == observation["base_sha"]
            and isinstance(pull.get("head"), dict)
            and pull["head"].get("sha") == observation["head_sha"]
        ]
        if len(associations) == 1 and len(identity_matches) == 1:
            matches.append(run)
        elif isinstance(run.get("id"), int):
            rejected_ids.append(run["id"])
    success = [run for run in matches if run.get("status") == "completed" and run.get("conclusion") == "success"]
    return {
        "success": bool(success),
        "run_ids": sorted(run.get("id") for run in matches if isinstance(run.get("id"), int)),
        "conclusions": sorted({str(run.get("conclusion")) for run in matches}),
        "identity_mismatch_run_ids": sorted(rejected_ids),
    }


def _deduplicate(observations: list[dict[str, Any]], failures: list[dict[str, Any]]) -> list[dict[str, Any]]:
    grouped: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for observation in observations:
        grouped[observation["head_sha"]].append(observation)
    result: list[dict[str, Any]] = []
    for head, values in grouped.items():
        if len({item["pull_request"] for item in values}) != 1:
            failures.append({"head_sha": head, "reason": "exact head maps to multiple pull requests"})
            continue
        result.append(max(values, key=lambda item: (item["producer_run_id"], item["artifact_id"])))
    return sorted(result, key=lambda item: item["head_sha"])


def summarize(
    index_value: object,
    archives: Path,
    serving_runs: Path,
    worker_runs: Path,
    policy_value: object,
    repository_root: Path,
) -> dict[str, Any]:
    policy = load_policy(policy_value)
    if not isinstance(index_value, dict) or index_value.get("contract") != INDEX_CONTRACT:
        raise ValueError("impact-routing evidence index is invalid")
    entries = index_value.get("artifacts")
    if not isinstance(entries, list):
        raise ValueError("impact-routing evidence index artifacts are invalid")
    failures = list(index_value.get("integrity_failures", []))
    observations: dict[str, list[dict[str, Any]]] = {PR_GATE_STREAM: [], NATIVE_STREAM: []}
    blobs = current_blobs(repository_root)
    for entry in entries:
        run_id = entry.get("producer_run_id") if isinstance(entry, dict) else None
        try:
            if not isinstance(entry, dict):
                raise ValueError("evidence index entry is invalid")
            receipt = _archive_json(entry, archives)
            observation = (
                _validate_pr_gate(entry, receipt, blobs)
                if entry.get("stream") == PR_GATE_STREAM
                else _validate_native(entry, receipt, blobs)
            )
            observations[observation["stream"]].append(observation)
        except (
            KeyError,
            OSError,
            RuntimeError,
            UnicodeDecodeError,
            ValueError,
            zipfile.BadZipFile,
        ) as error:
            failures.append({"producer_run_id": run_id, "reason": str(error)})
    pr_gate = _deduplicate(observations[PR_GATE_STREAM], failures)
    native = _deduplicate(observations[NATIVE_STREAM], failures)
    docs_only = [item for item in pr_gate if item["mode"] == "docs-only"]
    docs_failures = [item for item in docs_only if item["gate_conclusion"] != "success"]
    docs_success = [item for item in docs_only if item["gate_conclusion"] == "success"]

    serving_catalog = flatten_pages(serving_runs, "workflow_runs")
    worker_catalog = flatten_pages(worker_runs, "workflow_runs")
    native_countable: list[dict[str, Any]] = []
    image_failures: list[dict[str, Any]] = []
    for item in native:
        if item["gate_conclusion"] != "success":
            continue
        serving = _image_outcome(serving_catalog, item, SERVING_WORKFLOW)
        worker = _image_outcome(worker_catalog, item, WORKER_WORKFLOW)
        serving_required = item["legacy_serving"] or item["candidate_serving"]
        worker_required = item["legacy_worker"] or item["candidate_worker"]
        missing: list[str] = []
        if serving_required and not serving["success"]:
            missing.append("serving")
        if worker_required and not worker["success"]:
            missing.append("worker")
        if missing:
            image_failures.append({
                "head_sha": item["head_sha"],
                "pull_request": item["pull_request"],
                "missing_successful_outcomes": missing,
                "serving": serving,
                "worker": worker,
            })
            continue
        native_countable.append({**item, "serving_outcome": serving, "worker_outcome": worker})

    serving_impacted = [item for item in native_countable if item["candidate_serving"]]
    worker_impacted = [item for item in native_countable if item["candidate_worker"]]
    serving_narrowed = [
        item for item in native_countable
        if item["candidate_serving_count"] < item["legacy_serving_count"]
    ]
    worker_avoided = [
        item for item in native_countable
        if item["legacy_worker"] and not item["candidate_worker"]
    ]
    gates = {
        "integrity_clean": not failures,
        "docs_only_sample_ready": len(docs_success) >= policy["minimum_docs_only_heads"],
        "docs_only_gate_failures_zero": not docs_failures,
        "native_sample_ready": len(native_countable) >= policy["minimum_native_heads"],
        "serving_impacted_sample_ready": len(serving_impacted) >= policy["minimum_serving_impacted_heads"],
        "worker_impacted_sample_ready": len(worker_impacted) >= policy["minimum_worker_impacted_heads"],
        "serving_narrowed_sample_ready": len(serving_narrowed) >= policy["minimum_serving_narrowed_heads"],
        "worker_avoided_sample_ready": len(worker_avoided) >= policy["minimum_worker_avoided_heads"],
        "authoritative_image_outcomes_clean": not image_failures,
    }
    eligible = all(gates.values())
    return {
        "contract": LEDGER_CONTRACT,
        "mode": "report-only",
        "mutation": "none",
        "promotion_authority": "none",
        "recommendation": "eligible-for-human-promotion-review" if eligible else "observe-more",
        "policy": policy,
        "current_policy_blobs": blobs,
        "counts": {
            "discovered_artifacts": len(entries),
            "validated_pr_gate_receipts": len(pr_gate),
            "validated_native_receipts": len(native),
            "docs_only_success_heads": len(docs_success),
            "docs_only_failure_heads": len(docs_failures),
            "native_countable_heads": len(native_countable),
            "serving_impacted_heads": len(serving_impacted),
            "worker_impacted_heads": len(worker_impacted),
            "serving_narrowed_heads": len(serving_narrowed),
            "worker_avoided_heads": len(worker_avoided),
            "authoritative_image_outcome_failures": len(image_failures),
            "integrity_failures": len(failures),
        },
        "gates": gates,
        "docs_only_failures": docs_failures,
        "image_outcome_failures": image_failures,
        "integrity_failures": failures,
        "countable": {
            "docs_only": docs_success,
            "native": native_countable,
        },
        "exclusions": index_value.get("exclusions", []),
    }


def markdown(ledger: dict[str, Any]) -> str:
    counts = ledger["counts"]
    gates = ledger["gates"]
    policy = ledger["policy"]
    rows = "\n".join(f"| `{name}` | `{str(value).lower()}` |" for name, value in gates.items())
    return "\n".join([
        "## CI impact-routing evidence ledger",
        "",
        f"Recommendation: **{ledger['recommendation']}** (report-only; no promotion authority)",
        "",
        f"- Docs-only exact heads: `{counts['docs_only_success_heads']}` / `{policy['minimum_docs_only_heads']}`",
        f"- Native exact heads: `{counts['native_countable_heads']}` / `{policy['minimum_native_heads']}`",
        f"- Serving impacted/narrowed heads: `{counts['serving_impacted_heads']}` / `{counts['serving_narrowed_heads']}`",
        f"- Worker impacted/avoided heads: `{counts['worker_impacted_heads']}` / `{counts['worker_avoided_heads']}`",
        f"- Docs-only gate failures: `{counts['docs_only_failure_heads']}`",
        f"- Native authoritative outcome failures: `{counts['authoritative_image_outcome_failures']}`",
        f"- Receipt integrity failures: `{counts['integrity_failures']}`",
        "",
        "| Gate | Passed |",
        "|---|---|",
        rows,
        "",
        "A successful observer shell without one exact stable-name receipt is never counted. ",
        "Native decisions are countable only when every required exact-head legacy image workflow has a successful outcome.",
        "",
    ])


def main() -> int:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    policy_parser = subparsers.add_parser("policy")
    policy_parser.add_argument("--policy", type=Path, required=True)
    policy_parser.add_argument("--github-output", type=Path)

    discover_parser = subparsers.add_parser("discover")
    discover_parser.add_argument("--policy", type=Path, required=True)
    discover_parser.add_argument("--pr-gate-runs", type=Path, required=True)
    discover_parser.add_argument("--native-runs", type=Path, required=True)
    discover_parser.add_argument("--pr-gate-artifacts", type=Path, required=True)
    discover_parser.add_argument("--native-artifacts", type=Path, required=True)
    discover_parser.add_argument("--output", type=Path, required=True)

    summary_parser = subparsers.add_parser("summarize")
    summary_parser.add_argument("--policy", type=Path, required=True)
    summary_parser.add_argument("--index", type=Path, required=True)
    summary_parser.add_argument("--archives", type=Path, required=True)
    summary_parser.add_argument("--serving-runs", type=Path, required=True)
    summary_parser.add_argument("--worker-runs", type=Path, required=True)
    summary_parser.add_argument("--repository-root", type=Path, required=True)
    summary_parser.add_argument("--output", type=Path, required=True)
    summary_parser.add_argument("--markdown", type=Path, required=True)
    args = parser.parse_args()

    policy = load_policy(load_json(args.policy))
    if args.command == "policy":
        cutoff_value = receipt_cutoff(policy)
        cutoff = cutoff_value.isoformat().replace("+00:00", "Z")
        image_cutoff = (
            cutoff_value - timedelta(hours=policy["image_outcome_lookback_hours"])
        ).isoformat().replace("+00:00", "Z")
        values = {
            "receipt_cutoff": cutoff,
            "producer_run_cutoff": (
                cutoff_value - timedelta(hours=1)
            ).isoformat().replace("+00:00", "Z"),
            "image_run_cutoff": image_cutoff,
            "maximum_pages_per_query": policy["maximum_pages_per_query"],
            "maximum_receipt_downloads": policy["maximum_receipt_downloads"],
        }
        if args.github_output:
            with args.github_output.open("a", encoding="utf-8", newline="\n") as handle:
                for key, value in values.items():
                    handle.write(f"{key}={value}\n")
        print(json.dumps(values, sort_keys=True))
        return 0
    if args.command == "discover":
        result = discover(
            args.pr_gate_runs,
            args.native_runs,
            args.pr_gate_artifacts,
            args.native_artifacts,
            policy,
        )
        write_json(args.output, result)
        return 1 if result["integrity_failures"] else 0
    ledger = summarize(
        load_json(args.index),
        args.archives,
        args.serving_runs,
        args.worker_runs,
        policy,
        args.repository_root,
    )
    write_json(args.output, ledger)
    args.markdown.write_text(markdown(ledger), encoding="utf-8", newline="\n")
    print(markdown(ledger))
    return 1 if not ledger["gates"]["integrity_clean"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
