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


POLICY_CONTRACT = "honua.impact-routing-promotion-policy/v3"
INDEX_CONTRACT = "honua.impact-routing-evidence-index/v2"
# v4 resets retained trend samples after candidate-only, unexecuted routes
# stopped being promotion-countable.  trend() accepts only the current contract,
# so a v3 ledger cannot preserve the earlier countability semantics.
LEDGER_CONTRACT = "honua.impact-routing-evidence-ledger/v4"
TOMBSTONE_CONTRACT = "honua.impact-routing-evidence-tombstones/v1"
TREND_CONTRACT = "honua.impact-routing-evidence-trend/v1"
PR_GATE_CONTRACT = "honua.pr-gate-impact-observation/v3"
NATIVE_CONTRACT = "honua.ci.native-image-impact-observation/v3"
REPOSITORY = "honua-io/honua-server"
DEFAULT_BRANCH = "trunk"
PR_GATE_STREAM = "pr_gate"
NATIVE_STREAM = "native"
IMAGE_INPUT_CLASSES = ("serving_generic", "serving_lambda", "serving_functions", "worker")
IMAGE_INPUT_TREES = ("merge", "head")
SERVING_VARIANTS = ("generic", "lambda", "functions")
PR_GATE_WORKFLOW = ".github/workflows/pr-gate-impact-observe.yml"
NATIVE_WORKFLOW = ".github/workflows/native-image-impact-observe.yml"
SERVING_WORKFLOW = ".github/workflows/serving-image-boundary.yml"
WORKER_WORKFLOW = ".github/workflows/worker-gdal-image.yml"
# The PR Gate observer names its receipt after the mode it classified:
# `pr-gate-impact-<mode>-v3-attempt-N` where mode is `docs-only` OR `full`
# (pr-gate-impact-observe.yml, "Upload trusted PR Gate impact observation").
# Matching only `docs-only` made every `full` observation — the overwhelming
# majority — invisible to the index, which then reported each one as
# `observation-receipt-not-emitted`. That is not receipt loss: the receipts were
# emitted and retained, the reader could not see them. Both modes are indexed;
# only docs-only heads feed the docs-only promotion sample.
PR_GATE_MODES = ("docs-only", "full")
PR_GATE_UNDIGESTED_REASONS = frozenset({
    "unbounded-file-count",
    "truncated-file-list",
    "invalid-file-record",
    "unsafe-file-record",
    "duplicate-file-record",
})
PR_GATE_ARTIFACT = re.compile(
    r"^pr-gate-impact-(?P<mode>docs-only|full)-v3-attempt-(?P<attempt>[1-9][0-9]*)$"
)
NATIVE_ARTIFACT = re.compile(
    r"^native-image-impact-observation-v3-attempt-(?P<attempt>[1-9][0-9]*)$"
)
# A successful observer that deliberately skipped a superseded source uploads a
# stable-name marker. Recognising it from the catalog alone (never downloading
# it) keeps "observation-receipt-not-emitted" meaning ONLY a real
# receipt-emission regression.
PR_GATE_SKIP_ARTIFACT = re.compile(
    r"^pr-gate-impact-skipped-(?P<code>[a-z][a-z0-9-]{0,47})-attempt-(?P<attempt>[1-9][0-9]*)$"
)
NATIVE_SKIP_ARTIFACT = re.compile(
    r"^native-image-impact-skipped-(?P<code>[a-z][a-z0-9-]{0,47})-attempt-(?P<attempt>[1-9][0-9]*)$"
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


class CohortDrift(Exception):
    """The receipt is well formed but describes a superseded policy generation.

    Every receipt pins the classifier, routing-policy, evidence-routing workflow,
    and resolver blob SHAs that define routing and collection behaviour. The
    serving/worker workflow blobs remain receipt provenance: their routing-relevant
    declarations are fail-closed against the classifier-owned policy before
    evidence is emitted.

    That is cohort drift, not a receipt-integrity violation: the receipt is
    intact, attributable and internally consistent, it simply attests to a
    policy the repository no longer runs. Reporting it as an integrity failure
    made the ledger fail closed on routine repository maintenance, which is how
    a permanently red integrity check gets trained away. Drifted receipts are
    excluded from the countable cohort and counted separately; only a receipt
    that CONTRADICTS its own declared policy head is an integrity failure.
    """


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


def ratio(value: object, label: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ValueError(f"{label} must be a number")
    if not 0 < float(value) <= 1:
        raise ValueError(f"{label} must be greater than 0 and at most 1")
    return float(value)


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
    # Listing one run's artifact catalog is a cheap paged GET; downloading a
    # receipt is a real archive transfer. Conflating the two made the download
    # bound the binding cap on how many observer runs could exist in the window.
    catalogs = positive_int(
        value.get("maximum_producer_run_catalogs"), "maximum producer run catalogs"
    )
    # The download bound covers BOTH streams. It was sized when the PR Gate
    # stream was (wrongly) contributing nothing, so restoring that stream's
    # receipts to the index needs roughly double the budget or the collector
    # fail-closes on its own cap.
    if pages > 10 or downloads > 1000 or catalogs > 1600:
        raise ValueError("GitHub query, catalog, or download bound is unsafe")
    if catalogs < downloads:
        raise ValueError("catalog bound must not be smaller than the download bound")
    for field in (
        "minimum_docs_only_heads",
        "minimum_native_heads",
        "minimum_serving_impacted_heads",
        "minimum_worker_impacted_heads",
        "minimum_serving_narrowed_heads",
        "minimum_worker_avoided_heads",
        "minimum_serving_reuse_heads",
        "minimum_worker_reuse_heads",
    ):
        positive_int(value.get(field), field.replace("_", " "))
    grace = positive_int(
        value.get("receipt_index_grace_minutes"), "receipt index grace minutes"
    )
    if grace > 360:
        raise ValueError("receipt index grace exceeds the policy bound")
    green_days = positive_int(value.get("promotion_green_days"), "promotion green days")
    if green_days > 30:
        raise ValueError("promotion green days exceeds the policy bound")
    loss = ratio(value.get("maximum_receipt_loss_ratio"), "maximum receipt loss ratio")
    # The promotion gate for every shadow optimisation is "green for
    # promotion_green_days consecutive days with receipt loss under the budget".
    # A budget looser than 5% would silently redefine that gate, so the policy
    # loader refuses it the same way it refuses disabling a fail-closed flag.
    if loss > 0.05:
        raise ValueError("maximum receipt loss ratio exceeds the promotion bound")
    for field in (
        "require_zero_integrity_failures",
        "require_zero_docs_only_gate_failures",
        "require_successful_authoritative_image_outcomes",
    ):
        if value.get(field) is not True:
            raise ValueError(f"{field} must remain fail closed")
    return dict(value)


def load_tombstones(value: object, now: datetime) -> list[dict[str, Any]]:
    """Explicit, expiring quarantine for evidence that can never be verified.

    A receipt whose producer run or artifact no longer exists, or an exact head
    whose authoritative image work was destroyed rather than merely superseded,
    is unrecoverable: no future run can turn it green. Failing on it forever
    teaches reviewers to ignore the ledger. Deleting the failing class hides it.

    A tombstone is the third option: name the exact evidence, say why it cannot
    be verified, link the issue that owns it, and set an expiry. The auditor
    fails closed on an EXPIRED tombstone, so a quarantine cannot become
    permanent by neglect.
    """
    if not isinstance(value, dict) or value.get("contract") != TOMBSTONE_CONTRACT:
        raise ValueError("impact-routing tombstone contract is invalid")
    entries = value.get("tombstones")
    if not isinstance(entries, list):
        raise ValueError("impact-routing tombstones are invalid")
    result: list[dict[str, Any]] = []
    for entry in entries:
        if not isinstance(entry, dict):
            raise ValueError("impact-routing tombstone entry is invalid")
        kind = entry.get("kind")
        if kind not in ("receipt", "image-outcome"):
            raise ValueError("impact-routing tombstone kind is invalid")
        if kind == "receipt":
            positive_int(entry.get("producer_run_id"), "tombstone producer run id")
        else:
            exact_sha(entry.get("head_sha"), "tombstone head")
        reason = entry.get("reason")
        issue = entry.get("issue")
        if not isinstance(reason, str) or not reason.strip():
            raise ValueError("impact-routing tombstone reason is invalid")
        if not isinstance(issue, str) or not issue.startswith(
            f"https://github.com/{REPOSITORY}/issues/"
        ):
            raise ValueError("impact-routing tombstone needs an owning issue link")
        expires = parse_time(entry.get("expires_at"), "tombstone expiry")
        result.append({**entry, "expired": expires <= now})
    return result


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
        "pr_gate_workflow": ".github/workflows/pr-gate.yml",
        "native_observer": NATIVE_WORKFLOW,
        "native_classifier": "scripts/ci/native-image-impact.py",
        "native_routing_policy": ".github/native-image-impact.json",
        "serving_workflow": SERVING_WORKFLOW,
        "worker_workflow": WORKER_WORKFLOW,
        "trusted_run_resolver": "scripts/ci/trusted-pr-workflow-run.js",
    }
    blobs = {key: git_blob_sha(root / value) for key, value in paths.items()}
    manifest = [
        {"path": "scripts/ci/native-image-impact.py", "blob_sha": blobs["native_classifier"]},
        {"path": ".github/native-image-impact.json", "blob_sha": blobs["native_routing_policy"]},
        {"path": ".github/workflows/pr-gate.yml", "blob_sha": blobs["pr_gate_workflow"]},
        {"path": SERVING_WORKFLOW, "blob_sha": blobs["serving_workflow"]},
        {"path": WORKER_WORKFLOW, "blob_sha": blobs["worker_workflow"]},
        {"path": "scripts/ci/trusted-pr-workflow-run.js", "blob_sha": blobs["trusted_run_resolver"]},
        {"path": NATIVE_WORKFLOW, "blob_sha": blobs["native_observer"]},
    ]
    blobs["native_policy_inputs_sha256"] = hashlib.sha256(
        json.dumps(manifest, separators=(",", ":")).encode("utf-8")
    ).hexdigest()
    # The semantic manifest contains only inputs that can change a routing
    # decision or alter evidence collection. The native policy owns the legacy
    # serving/worker workflow path/variant rules; native-image-impact.py validates
    # those declarations against the live workflows and fails closed before
    # emitting evidence. Those serving/worker blobs remain provenance, so their
    # operational edits (for example an actions/checkout bump) do not reset the cohort.
    semantic_manifest = [
        ["native_classifier", blobs["native_classifier"]],
        ["native_routing_policy", blobs["native_routing_policy"]],
        ["pr_gate_classifier", blobs["pr_gate_classifier"]],
        ["pr_gate_observer", blobs["pr_gate_observer"]],
        ["pr_gate_workflow", blobs["pr_gate_workflow"]],
        ["native_observer", blobs["native_observer"]],
        ["trusted_run_resolver", blobs["trusted_run_resolver"]],
    ]
    blobs["policy_generation_sha256"] = hashlib.sha256(
        json.dumps(semantic_manifest, separators=(",", ":")).encode("utf-8")
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
    skip_pattern: re.Pattern[str],
    now: datetime,
    grace: timedelta,
) -> tuple[
    list[dict[str, Any]], list[dict[str, Any]], list[dict[str, Any]], dict[str, int]
]:
    entries: list[dict[str, Any]] = []
    exclusions: list[dict[str, Any]] = []
    failures: list[dict[str, Any]] = []
    emission = {
        "observer_runs_successful": 0,
        "receipts_indexed": 0,
        "receipts_skipped": 0,
        "receipts_pending_index": 0,
        "receipts_missing": 0,
    }
    # Runs are collected from `cutoff - 1h`, so an artifact uploaded by a run
    # that started just inside that hour is created BEFORE `cutoff`. Filtering
    # artifacts at `cutoff` while admitting their producers at `cutoff - 1h`
    # discarded those receipts and then reported them as missing — a pure
    # boundary artefact of two different window edges. Align the two.
    artifact_cutoff = cutoff - timedelta(hours=1)
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
        emission["observer_runs_successful"] += 1
        matches = []
        for artifact in by_run.get(run_id, []):
            match = artifact_pattern.fullmatch(str(artifact.get("name", "")))
            if (
                match
                and int(match.group("attempt")) == run["run_attempt"]
                and artifact.get("expired") is False
                and parse_time(artifact.get("created_at"), "artifact creation")
                >= artifact_cutoff
            ):
                matches.append(artifact)
        if not matches:
            # A deliberate, recorded skip of a superseded source is not a
            # receipt-emission regression; classify it by its own code so the
            # remaining "not emitted" count keeps its diagnostic value.
            skip_codes = sorted({
                skip.group("code")
                for artifact in by_run.get(run_id, [])
                for skip in [skip_pattern.fullmatch(str(artifact.get("name", "")))]
                if skip
                and int(skip.group("attempt")) == run["run_attempt"]
                and artifact.get("expired") is False
            })
            if len(skip_codes) == 1:
                emission["receipts_skipped"] += 1
                exclusions.append({
                    "stream": stream,
                    "producer_run_id": run_id,
                    "reason": f"observation-skipped:{skip_codes[0]}",
                })
                continue
            if skip_codes:
                failures.append({
                    "stream": stream,
                    "producer_run_id": run_id,
                    "reason": "observation-skip-marker-ambiguous",
                })
                continue
            # "Missing" and "not indexed yet" are different facts and only one
            # of them is loss. GitHub finalises a run's artifact catalog after
            # the run completes, so an observer that finished inside the grace
            # window can legitimately have nothing listed yet. Counting those as
            # loss put a floor under the loss ratio that no producer fix could
            # ever remove.
            pending = (
                parse_time(run["updated_at"], "observer run update") >= now - grace
            )
            emission["receipts_pending_index" if pending else "receipts_missing"] += 1
            exclusions.append({
                "stream": stream,
                "producer_run_id": run_id,
                "reason": (
                    "observation-receipt-pending-index" if pending
                    else "observation-receipt-missing"
                ),
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
        emission["receipts_indexed"] += 1
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
            (artifact_pattern.fullmatch(str(item.get("name", "")))
             or skip_pattern.fullmatch(str(item.get("name", ""))))
            and parse_time(item.get("created_at"), "artifact creation") >= artifact_cutoff
            for item in values
        ):
            failures.append({
                "stream": stream,
                "producer_run_id": run_id,
                "reason": "observation-artifact-producer-not-in-bounded-run-catalog",
            })
    return entries, exclusions, failures, emission


def discover(
    pr_gate_runs: Path,
    native_runs: Path,
    pr_gate_artifacts: Path,
    native_artifacts: Path,
    policy_value: object,
    now: datetime | None = None,
    cutoff: datetime | None = None,
) -> dict[str, Any]:
    policy = load_policy(policy_value)
    current = now or datetime.now(timezone.utc)
    bound_cutoff = cutoff or receipt_cutoff(policy, current)
    if bound_cutoff.tzinfo is None:
        raise ValueError("receipt cutoff must include a timezone")
    grace = timedelta(minutes=policy["receipt_index_grace_minutes"])
    streams = (
        (
            PR_GATE_STREAM, pr_gate_runs, pr_gate_artifacts, PR_GATE_WORKFLOW,
            PR_GATE_ARTIFACT, PR_GATE_SKIP_ARTIFACT,
        ),
        (
            NATIVE_STREAM, native_runs, native_artifacts, NATIVE_WORKFLOW,
            NATIVE_ARTIFACT, NATIVE_SKIP_ARTIFACT,
        ),
    )
    entries: list[dict[str, Any]] = []
    exclusions: list[dict[str, Any]] = []
    failures: list[dict[str, Any]] = []
    emissions: dict[str, dict[str, int]] = {}
    for (
        stream, runs_root, artifacts_root, workflow, artifact_pattern, skip_pattern,
    ) in streams:
        found, omitted, invalid, emission = _discover_stream(
            stream,
            flatten_pages(runs_root, "workflow_runs"),
            flatten_artifact_catalogs(artifacts_root),
            workflow,
            artifact_pattern,
            bound_cutoff,
            skip_pattern,
            current,
            grace,
        )
        entries.extend(found)
        exclusions.extend(omitted)
        failures.extend(invalid)
        emissions[stream] = emission
    entries.sort(key=lambda item: (item["stream"], item["producer_run_id"]))
    if len({item["artifact_id"] for item in entries}) != len(entries):
        failures.append({"reason": "artifact identity is duplicated across streams"})
    return {
        "contract": INDEX_CONTRACT,
        "repository": REPOSITORY,
        "cutoff": bound_cutoff.isoformat().replace("+00:00", "Z"),
        "generated_at": current.isoformat().replace("+00:00", "Z"),
        "artifacts": entries,
        "exclusions": exclusions,
        "receipt_emission": receipt_emission(emissions),
        "integrity_failures": failures,
    }


def receipt_emission(emissions: dict[str, dict[str, int]]) -> dict[str, Any]:
    """Receipt loss, stated so it can only mean one thing.

    The denominator is every successful observer that OWED a receipt: a run that
    recorded a deliberate skip owed nothing, and a run still inside the indexing
    grace window has not yet failed to deliver. Both are removed rather than
    silently counted as delivered, so the ratio cannot be improved by
    reclassifying work away from it.
    """
    total = {
        key: sum(value[key] for value in emissions.values())
        for key in (
            "observer_runs_successful",
            "receipts_indexed",
            "receipts_skipped",
            "receipts_pending_index",
            "receipts_missing",
        )
    }
    per_stream = {}
    for name, value in list(emissions.items()) + [("all", total)]:
        owed = (
            value["observer_runs_successful"]
            - value["receipts_skipped"]
            - value["receipts_pending_index"]
        )
        per_stream[name] = {
            **value,
            "receipts_owed": owed,
            "loss_ratio": round(value["receipts_missing"] / owed, 6) if owed > 0 else 0.0,
            # An owed-count of zero is not evidence of health, so say so rather
            # than publishing a flattering 0.0 loss ratio with nothing behind it.
            "measured": owed > 0,
        }
    return per_stream


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


def _entry_identity(entry: dict[str, Any], expected_stream: str) -> str | None:
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
    return (
        artifact_match.group("mode") if expected_stream == PR_GATE_STREAM else None
    )


def _drift(label: str, value: dict[str, Any], policy_head_sha: str | None) -> None:
    """Raise for a receipt that does not describe the current policy generation.

    The receipt's `policy_sha` is the trunk commit the trusted observer executed
    from, and the blob SHAs are what that commit contained. When `policy_sha`
    equals the ledger's OWN checkout, the two are directly comparable and a
    mismatch is a genuine contradiction — the receipt claims blobs that its
    declared head does not contain. That is a real integrity failure and stays
    fail-closed. Otherwise the receipt simply predates a policy change.
    """
    if policy_head_sha is not None and value.get("policy_sha") == policy_head_sha:
        raise ValueError(f"{label} receipt contradicts its own declared policy head")
    raise CohortDrift(f"{label} receipt describes a superseded policy generation")


def _validate_pr_gate(
    entry: dict[str, Any],
    value: object,
    blobs: dict[str, str],
    policy_head_sha: str | None = None,
) -> dict[str, Any]:
    name_mode = _entry_identity(entry, PR_GATE_STREAM)
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
    pull_request = positive_int(value.get("pull_request"), "PR Gate pull request")
    head = exact_sha(value.get("head_sha"), "PR Gate head")
    exact_sha(value.get("base_sha"), "PR Gate base")
    positive_int(value.get("gate_run_id"), "PR Gate run id")
    positive_int(value.get("gate_run_attempt"), "PR Gate run attempt")
    if value.get("gate_run_head_sha") != head or value.get("gate_run_conclusion") not in TERMINAL_CONCLUSIONS:
        raise ValueError("PR Gate authoritative result identity is invalid")
    mode = value.get("mode")
    # The artifact name carries the mode, so the two must agree: a receipt whose
    # body disagrees with the name it was stored under is not addressable
    # evidence, whichever half is wrong.
    if mode not in PR_GATE_MODES or mode != name_mode:
        raise ValueError("PR Gate receipt mode differs from its artifact name")
    reason = value.get("reason")
    if not isinstance(reason, str) or not reason:
        raise ValueError("PR Gate receipt reason is invalid")
    if mode == "docs-only" and reason != "internal-markdown-only":
        raise ValueError("PR Gate docs-only reason is invalid")
    files_digest = value.get("files_sha256")
    if files_digest == "":
        # The classifier deliberately fails closed before it can normalize a
        # file list for these full-gate reasons. Such a receipt contains no
        # claim about file-list content, so an empty digest is the producer's
        # current contract rather than a malformed integrity assertion.
        if mode != "full" or reason not in PR_GATE_UNDIGESTED_REASONS:
            raise ValueError("PR Gate files digest is missing without a fail-closed reason")
    else:
        exact_digest(files_digest, "PR Gate files digest")
    for field in (
        "policy_blob_sha",
        "gate_workflow_blob_sha",
        "resolver_blob_sha",
        "observer_workflow_blob_sha",
    ):
        exact_sha(value.get(field), f"PR Gate {field}")
    # Currency is checked last: everything above is what makes the receipt
    # trustworthy, and all of it must hold before drift can be the verdict.
    semantic_drift = value.get("policy_blob_sha") != blobs["pr_gate_classifier"]
    provenance_drift = any((
        value.get("gate_workflow_blob_sha") != blobs["pr_gate_workflow"],
        value.get("resolver_blob_sha") != blobs["trusted_run_resolver"],
        value.get("observer_workflow_blob_sha") != blobs["pr_gate_observer"],
    ))
    if semantic_drift:
        _drift("PR Gate", value, policy_head_sha)
    if provenance_drift and policy_head_sha is not None and value.get("policy_sha") == policy_head_sha:
        raise ValueError("PR Gate receipt contradicts its own declared policy head")
    return {
        "stream": PR_GATE_STREAM,
        "pull_request": pull_request,
        "head_sha": head,
        "mode": mode,
        "gate_conclusion": value["gate_run_conclusion"],
        "producer_run_id": entry["producer_run_id"],
        "artifact_id": entry["artifact_id"],
    }


def _validate_native(
    entry: dict[str, Any],
    value: object,
    blobs: dict[str, str],
    policy_head_sha: str | None = None,
) -> dict[str, Any]:
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
        "gate_workflow_blob_sha": blobs["pr_gate_workflow"],
        "serving_workflow_blob_sha": blobs["serving_workflow"],
        "worker_workflow_blob_sha": blobs["worker_workflow"],
        "resolver_blob_sha": blobs["trusted_run_resolver"],
        "observer_workflow_blob_sha": blobs["native_observer"],
    }
    for field in expected_blobs:
        exact_sha(value.get(field), f"native-image {field}")
    exact_digest(value.get("policy_inputs_sha256"), "native-image policy inputs digest")
    # Whole-file pins above remain attributable provenance. Currency is based
    # only on semantic routing inputs; workflow routing declarations are
    # validated fail-closed against native_routing_policy by the classifier.
    drifted = any(
        value.get(field) != expected_blobs[field]
        for field in ("policy_blob_sha", "routing_policy_blob_sha")
    )
    provenance_drift = any(
        value.get(field) != expected
        for field, expected in expected_blobs.items()
        if field not in ("policy_blob_sha", "routing_policy_blob_sha")
    ) or value.get("policy_inputs_sha256") != blobs["native_policy_inputs_sha256"]
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
    digests = value.get("image_input_digests")
    if not isinstance(digests, dict) or set(digests) != set(IMAGE_INPUT_CLASSES):
        raise ValueError("native-image image input digests are invalid")
    digests = {
        name: exact_digest(digests[name], f"native-image {name} input digest")
        for name in IMAGE_INPUT_CLASSES
    }
    tree_kind = value.get("image_input_tree")
    if tree_kind not in IMAGE_INPUT_TREES:
        raise ValueError("native-image image input tree kind is invalid")
    tree_sha = exact_sha(value.get("image_input_tree_sha"), "native-image image input tree")
    if (tree_kind == "head") != (tree_sha == head):
        raise ValueError("native-image image input tree identity does not replay")
    legacy = value.get("legacy")
    candidate = value.get("candidate")
    comparison = value.get("comparison")
    if not isinstance(legacy, dict) or not isinstance(candidate, dict) or not isinstance(comparison, dict):
        raise ValueError("native-image routing decision is invalid")
    serving = candidate.get("serving_variants")
    legacy_serving_variants = legacy.get("serving_variants")
    variant_names = set(SERVING_VARIANTS)
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
    # Same ordering rule as the PR Gate receipt: prove the receipt is sound
    # first, then decide whether it belongs to the current cohort.
    if drifted:
        _drift("native-image", value, policy_head_sha)
    if provenance_drift and policy_head_sha is not None and value.get("policy_sha") == policy_head_sha:
        raise ValueError("native-image receipt contradicts its own declared policy head")
    return {
        "stream": NATIVE_STREAM,
        "pull_request": pull_request,
        "base_sha": base,
        "head_sha": head,
        "gate_conclusion": value["gate_run_conclusion"],
        "gate_run_id": value["gate_run_id"],
        "legacy_serving": legacy_serving,
        "legacy_serving_count": sum(
            1 for item in legacy_serving_variants.values() if item
        ),
        "legacy_worker": legacy["worker_trigger"],
        "candidate_serving": candidate_serving,
        "candidate_serving_count": sum(1 for item in serving.values() if item),
        "candidate_serving_variants": {name: bool(serving[name]) for name in sorted(serving)},
        "candidate_worker": candidate["worker_build"],
        "image_input_digests": dict(sorted(digests.items())),
        "image_input_tree": tree_kind,
        "image_input_tree_sha": tree_sha,
        # The workflow builds the variants its own legacy case arms selected, so
        # only those variants actually produce reusable image evidence.
        "legacy_serving_variants": {
            name: bool(legacy_serving_variants[name]) for name in sorted(legacy_serving_variants)
        },
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
        associations = [
            pull for pull in (pulls if isinstance(pulls, list) else [])
            if isinstance(pull, dict)
        ]
        numbers = {pull.get("number") for pull in associations}
        # `pull_requests` on a workflow run is a LIVE projection of the pull
        # request, not a snapshot of it at run time: `base.sha` and `head.sha`
        # track the PR's CURRENT tip. Requiring `head.sha == observation head`
        # therefore matched only a PR's most recent head and rejected every
        # earlier one, and GitHub leaves the array empty often enough that the
        # check also rejected runs with no association at all. Both rejections
        # were reported as identity mismatches — a tamper-shaped word for an
        # API-shape artefact — and they were the sole cause of every
        # authoritative-outcome failure in run 32038145537.
        #
        # The run-invariant binding is the one already required above: a run's
        # own `head_sha` is the 40-hex content address of the exact tree it
        # built, on the exact workflow, from a pull-request event. An
        # association is used only for what it can still soundly prove — that a
        # run belongs to a DIFFERENT pull request.
        if numbers and observation["pull_request"] not in numbers:
            if isinstance(run.get("id"), int):
                rejected_ids.append(run["id"])
            continue
        matches.append(run)
    success = [run for run in matches if run.get("status") == "completed" and run.get("conclusion") == "success"]
    cancelled = [run for run in matches if run.get("conclusion") == "cancelled"]
    cancelled_only = bool(cancelled) and not success and len(cancelled) == len(matches)
    # Witnesses for supersession: a run of the SAME workflow, explicitly
    # associated with this pull request, at a DIFFERENT head, started after this
    # head's own work began. Only an association naming this PR is used, because
    # that is the one thing the live `pull_requests` array can still prove.
    started_here = [
        value for value in (_run_time(run, "run_started_at", "created_at") for run in cancelled)
        if value is not None
    ]
    superseding: list[int] = []
    if started_here:
        earliest = min(started_here)
        for run in runs:
            began = _run_time(run, "run_started_at", "created_at")
            if (
                run.get("path") == workflow
                and run.get("event") == "pull_request"
                and run.get("head_sha") != observation["head_sha"]
                and isinstance(run.get("id"), int)
                and began is not None
                and began > earliest
                and any(
                    isinstance(pull, dict)
                    and pull.get("number") == observation["pull_request"]
                    for pull in (run.get("pull_requests") or [])
                )
            ):
                superseding.append(run["id"])
    started = [
        value for value in (_run_time(run, "run_started_at", "created_at") for run in matches)
        if value is not None
    ]
    completed = [
        value for value in (_run_time(run, "updated_at") for run in success) if value is not None
    ]
    return {
        "success": bool(success),
        # A head whose image work was cancelled BY A LATER PUSH can never
        # acquire a successful outcome, so demanding one made a normal, correct
        # cancellation permanently red. But `cancelled` alone does not say who
        # cancelled it: an operator cancel or an infrastructure abort produces
        # the same conclusion and is a real missing outcome. Supersession must
        # therefore be WITNESSED — a later run of the same workflow, associated
        # with the same pull request, at a different head — not inferred from
        # the conclusion. Without a witness the head stays a failure.
        "cancelled_only": cancelled_only,
        "superseded": cancelled_only and bool(superseding),
        "superseded_by_run_ids": sorted(superseding),
        "superseded_by_later_receipt": False,
        # The image catalogs are collected WITHOUT a completed filter, so a head
        # pushed shortly before the audit has its image work still running. That
        # is an undetermined outcome, not an absent one: the next audit sees it.
        "in_flight": not success and any(
            run.get("status") != "completed" for run in matches
        ),
        "observed": bool(matches),
        "run_ids": sorted(run.get("id") for run in matches if isinstance(run.get("id"), int)),
        "conclusions": sorted({str(run.get("conclusion")) for run in matches}),
        "foreign_pull_request_run_ids": sorted(rejected_ids),
        # Attestation timing. ``started_at`` is when this head's own image work
        # began; ``completed_at`` is when its evidence became available to a
        # later head. Missing timestamps degrade to "unusable as a source".
        "started_at": min(started).isoformat() if started else None,
        "completed_at": max(completed).isoformat() if len(completed) == len(success) and completed else None,
    }


def _run_time(run: dict[str, Any], *fields: str) -> datetime | None:
    for field in fields:
        value = run.get(field)
        if isinstance(value, str):
            try:
                return parse_time(value, field)
            except ValueError:
                return None
    return None


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


def _built_classes(observation: dict[str, Any]) -> list[str]:
    """The image classes the authoritative workflows actually build for a head.

    The Serving Image Boundary workflow gates each variant on its own legacy
    case arm, so a variant the candidate selected but legacy did not is never
    built and never produces reusable evidence. The worker workflow is
    all-or-nothing on its legacy trigger.
    """
    classes = [
        f"serving_{variant}"
        for variant in SERVING_VARIANTS
        if observation["legacy_serving_variants"].get(variant)
    ]
    if observation["legacy_worker"]:
        classes.append("worker")
    return classes


def _reuse_cohorts(
    native_countable: list[dict[str, Any]],
) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    """Heads whose image inputs were already built successfully by an earlier push.

    Measured routing evidence for #3204 shows the candidate selector never
    narrows or avoids an image build, so exact-input reuse is the only savings
    mechanism the observation can still substantiate.

    A head counts only when, for every image class the authoritative workflows
    would build for it, an attestation for byte-identical inputs already
    existed **when this head's own image work started**. Heads whose receipt
    could not address the merge tree the images are built from are excluded
    from both sides: they can neither consume nor produce a reuse attestation.
    """
    reuse: dict[str, list[dict[str, Any]]] = {"serving": [], "worker": []}
    # Keyed by (image class, digest): a collapsed variant pattern must never let
    # one variant's build satisfy another variant's head.
    attested: dict[tuple[str, str], tuple[datetime, str]] = {}
    ordered = sorted(native_countable, key=_reuse_order_key)
    for item in ordered:
        if item["image_input_tree"] != "merge":
            continue
        digests = item["image_input_digests"]
        classes = _built_classes(item)
        for stream, required in (
            ("serving", [name for name in classes if name.startswith("serving_")]),
            ("worker", [name for name in classes if name == "worker"]),
        ):
            if not required:
                continue
            started = _reuse_timestamp(item, stream, "started_at")
            matched = {}
            for name in required:
                source = attested.get((name, digests[name]))
                if source is not None and started is not None and source[0] <= started:
                    matched[name] = source[1]
            if len(matched) == len(required):
                reuse[stream].append({
                    "pull_request": item["pull_request"],
                    "head_sha": item["head_sha"],
                    "image_classes": sorted(required),
                    "reused_from": matched,
                })
            outcome = item[f"{stream}_outcome"]
            completed = _reuse_timestamp(item, stream, "completed_at")
            if outcome["success"] and completed is not None:
                for name in required:
                    attested.setdefault((name, digests[name]), (completed, item["head_sha"]))
    return reuse["serving"], reuse["worker"]


def _reuse_timestamp(observation: dict[str, Any], stream: str, field: str) -> datetime | None:
    value = observation[f"{stream}_outcome"].get(field)
    if not isinstance(value, str):
        return None
    try:
        return parse_time(value, field)
    except ValueError:
        return None


def _reuse_order_key(observation: dict[str, Any]) -> tuple[str, int, str]:
    """Order by when the head's own image work started, not by observer run id.

    A ``workflow_dispatch`` re-observation or a PR Gate rerun of an older head
    gets a newer observer run id, which would otherwise reorder it after newer
    heads and change both the reuse source and the eligible count. ``gate_run_id``
    is push-monotone and breaks ties deterministically.
    """
    started = [
        observation[f"{stream}_outcome"].get("started_at") for stream in ("serving", "worker")
    ]
    earliest = sorted(value for value in started if isinstance(value, str))
    return (earliest[0] if earliest else "", observation["gate_run_id"], observation["head_sha"])


def summarize(
    index_value: object,
    archives: Path,
    serving_runs: Path,
    worker_runs: Path,
    policy_value: object,
    repository_root: Path,
    tombstone_value: object | None = None,
    policy_head_sha: str | None = None,
    now: datetime | None = None,
) -> dict[str, Any]:
    policy = load_policy(policy_value)
    current = now or datetime.now(timezone.utc)
    if not isinstance(index_value, dict) or index_value.get("contract") != INDEX_CONTRACT:
        raise ValueError("impact-routing evidence index is invalid")
    entries = index_value.get("artifacts")
    if not isinstance(entries, list):
        raise ValueError("impact-routing evidence index artifacts are invalid")
    tombstones = load_tombstones(
        tombstone_value
        if tombstone_value is not None
        else {"contract": TOMBSTONE_CONTRACT, "tombstones": []},
        current,
    )
    receipt_tombstones = {
        item["producer_run_id"]: item for item in tombstones if item["kind"] == "receipt"
    }
    head_tombstones = {
        item["head_sha"]: item for item in tombstones if item["kind"] == "image-outcome"
    }
    used_tombstones: set[int] = set()
    quarantined: list[dict[str, Any]] = []
    failures = list(index_value.get("integrity_failures", []))
    drifted: list[dict[str, Any]] = []
    observations: dict[str, list[dict[str, Any]]] = {PR_GATE_STREAM: [], NATIVE_STREAM: []}
    blobs = current_blobs(repository_root)
    for entry in entries:
        run_id = entry.get("producer_run_id") if isinstance(entry, dict) else None
        try:
            if not isinstance(entry, dict):
                raise ValueError("evidence index entry is invalid")
            receipt = _archive_json(entry, archives)
            observation = (
                _validate_pr_gate(entry, receipt, blobs, policy_head_sha)
                if entry.get("stream") == PR_GATE_STREAM
                else _validate_native(entry, receipt, blobs, policy_head_sha)
            )
            observations[observation["stream"]].append(observation)
        except CohortDrift as error:
            drifted.append({
                "producer_run_id": run_id,
                "stream": entry.get("stream") if isinstance(entry, dict) else None,
                "reason": str(error),
            })
        except (
            KeyError,
            OSError,
            RuntimeError,
            UnicodeDecodeError,
            ValueError,
            zipfile.BadZipFile,
        ) as error:
            record = {"producer_run_id": run_id, "reason": str(error)}
            tombstone = receipt_tombstones.get(run_id)
            if tombstone is not None and not tombstone["expired"]:
                used_tombstones.add(id(tombstone))
                quarantined.append({**record, "tombstone": tombstone})
            else:
                failures.append(record)
    pr_gate = _deduplicate(observations[PR_GATE_STREAM], failures)
    native = _deduplicate(observations[NATIVE_STREAM], failures)
    docs_only = [item for item in pr_gate if item["mode"] == "docs-only"]
    docs_failures = [item for item in docs_only if item["gate_conclusion"] != "success"]
    docs_success = [item for item in docs_only if item["gate_conclusion"] == "success"]

    serving_catalog = flatten_pages(serving_runs, "workflow_runs")
    worker_catalog = flatten_pages(worker_runs, "workflow_runs")
    # A second, receipt-borne witness for supersession. `gate_run_id` is
    # push-monotone (see `_reuse_order_key`), and it comes from a validated
    # receipt rather than the live `pull_requests` array — which GitHub leaves
    # empty often enough that run-association evidence alone would miss most
    # real supersessions and turn them back into permanent failures.
    later_pushes: dict[int, list[int]] = defaultdict(list)
    for item in native:
        later_pushes[item["pull_request"]].append(item["gate_run_id"])
    native_countable: list[dict[str, Any]] = []
    image_failures: list[dict[str, Any]] = []
    superseded_heads: list[dict[str, Any]] = []
    pending_heads: list[dict[str, Any]] = []
    shadow_only_heads: list[dict[str, Any]] = []
    for item in native:
        if item["gate_conclusion"] != "success":
            continue
        serving = _image_outcome(serving_catalog, item, SERVING_WORKFLOW)
        worker = _image_outcome(worker_catalog, item, WORKER_WORKFLOW)
        # These workflows are the AUTHORITATIVE legacy route while the
        # candidate remains report-only. Candidate-only heads intentionally do
        # not trigger them, so requiring an impossible exact-head run converts
        # a shadow-routing difference into a fabricated native-outcome failure.
        serving_required = item["legacy_serving"]
        worker_required = item["legacy_worker"]
        missing: list[str] = []
        if serving_required and not serving["success"]:
            missing.append("serving")
        if worker_required and not worker["success"]:
            missing.append("worker")
        if missing:
            outcomes = {"serving": serving, "worker": worker}
            record = {
                "head_sha": item["head_sha"],
                "pull_request": item["pull_request"],
                "missing_successful_outcomes": missing,
                "serving": serving,
                "worker": worker,
            }
            # A head is superseded when every missing outcome was CANCELLED and
            # a later push on the same pull request is WITNESSED — by a later
            # associated run of the same workflow, or by another validated
            # receipt for the same PR at a higher (push-monotone) gate run.
            # Nothing can ever make such a head green, and nothing should: it
            # is an excluded head, not a failed one. An all-cancelled head with
            # no witness stays a failure, because an operator cancel and an
            # infrastructure abort look identical from the conclusion alone.
            pushed_past = any(
                gate > item["gate_run_id"]
                for gate in later_pushes.get(item["pull_request"], [])
            )
            for name in missing:
                outcome = outcomes[name]
                if pushed_past and outcome["cancelled_only"] and not outcome["superseded"]:
                    outcome["superseded"] = True
                    outcome["superseded_by_later_receipt"] = True
            if all(outcomes[name]["superseded"] for name in missing):
                superseded_heads.append({**record, "reason": "image-outcome-superseded-head"})
                continue
            if all(
                outcomes[name]["superseded"] or outcomes[name]["in_flight"]
                for name in missing
            ):
                pending_heads.append({**record, "reason": "image-outcome-pending"})
                continue
            record["reason"] = (
                "no-exact-head-image-run"
                if any(not outcomes[name]["observed"] for name in missing)
                else "no-successful-image-outcome"
            )
            tombstone = head_tombstones.get(item["head_sha"])
            if tombstone is not None and not tombstone["expired"]:
                used_tombstones.add(id(tombstone))
                quarantined.append({**record, "tombstone": tombstone})
            else:
                image_failures.append(record)
            continue
        candidate_only_classes = [
            f"serving_{name}"
            for name in SERVING_VARIANTS
            if item["candidate_serving_variants"][name]
            and not item["legacy_serving_variants"][name]
        ]
        if item["candidate_worker"] and not item["legacy_worker"]:
            candidate_only_classes.append("worker")
        if candidate_only_classes:
            # No workflow executes candidate-only routes in observe mode. They
            # are valid shadow decisions, but cannot contribute to promotion
            # readiness until candidate execution evidence exists.
            shadow_only_heads.append({
                "pull_request": item["pull_request"],
                "head_sha": item["head_sha"],
                "candidate_only_classes": candidate_only_classes,
                "reason": "candidate-route-not-executed-in-observe-mode",
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
    serving_reuse, worker_reuse = _reuse_cohorts(native_countable)
    signals = {
        "serving_narrowing_ready": len(serving_narrowed) >= policy["minimum_serving_narrowed_heads"],
        "serving_reuse_ready": len(serving_reuse) >= policy["minimum_serving_reuse_heads"],
        "worker_avoidance_ready": len(worker_avoided) >= policy["minimum_worker_avoided_heads"],
        "worker_reuse_ready": len(worker_reuse) >= policy["minimum_worker_reuse_heads"],
    }
    savings_mechanism = {
        "serving": sorted(
            name
            for name, ready in (
                ("narrowing", signals["serving_narrowing_ready"]),
                ("exact-input-build-reuse", signals["serving_reuse_ready"]),
            )
            if ready
        ),
        "worker": sorted(
            name
            for name, ready in (
                ("avoidance", signals["worker_avoidance_ready"]),
                ("exact-input-build-reuse", signals["worker_reuse_ready"]),
            )
            if ready
        ),
    }
    # An expired tombstone is a failure in its own right: quarantine that has
    # outlived its expiry is exactly the "permanently red, permanently ignored"
    # state the tombstone mechanism exists to prevent.
    for tombstone in tombstones:
        if tombstone["expired"]:
            failures.append({
                "reason": "impact-routing tombstone expired without resolution",
                "tombstone": tombstone,
            })
    stale_tombstones = [
        tombstone for tombstone in tombstones
        if not tombstone["expired"] and id(tombstone) not in used_tombstones
    ]
    emission = index_value.get("receipt_emission")
    if not isinstance(emission, dict) or not isinstance(emission.get("all"), dict):
        raise ValueError("impact-routing evidence index receipt emission is invalid")
    loss = emission["all"]
    gates = {
        "integrity_clean": not failures,
        # Loss is only a gate once something was actually owed; an unmeasured
        # window must not read as a pass.
        "receipt_loss_within_budget": bool(loss["measured"])
        and loss["loss_ratio"] < policy["maximum_receipt_loss_ratio"],
        "docs_only_sample_ready": len(docs_success) >= policy["minimum_docs_only_heads"],
        "docs_only_gate_failures_zero": not docs_failures,
        "native_sample_ready": len(native_countable) >= policy["minimum_native_heads"],
        "serving_impacted_sample_ready": len(serving_impacted) >= policy["minimum_serving_impacted_heads"],
        "worker_impacted_sample_ready": len(worker_impacted) >= policy["minimum_worker_impacted_heads"],
        "serving_savings_sample_ready": (
            signals["serving_narrowing_ready"] or signals["serving_reuse_ready"]
        ),
        "worker_savings_sample_ready": (
            signals["worker_avoidance_ready"] or signals["worker_reuse_ready"]
        ),
        "authoritative_image_outcomes_clean": not image_failures,
    }
    eligible = all(gates.values())
    return {
        "contract": LEDGER_CONTRACT,
        "mode": "report-only",
        "mutation": "none",
        "promotion_authority": "none",
        "recommendation": "eligible-for-human-promotion-review" if eligible else "observe-more",
        # Which mechanism the evidence actually substantiated. Promotion of the
        # path router requires a narrowing/avoidance mechanism; a reuse-only
        # sample authorizes reviewing build-evidence reuse, nothing else.
        "savings_mechanism": savings_mechanism,
        "generated_at": current.isoformat().replace("+00:00", "Z"),
        # A measured breach of the loss budget. Distinct from the promotion
        # gate: an unmeasured window blocks promotion without being red.
        "receipt_loss_regression": bool(loss["measured"])
        and loss["loss_ratio"] >= policy["maximum_receipt_loss_ratio"],
        "policy": policy,
        "policy_generation_sha256": blobs["policy_generation_sha256"],
        "current_policy_blobs": blobs,
        "receipt_emission": emission,
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
            "serving_reuse_eligible_heads": len(serving_reuse),
            "worker_reuse_eligible_heads": len(worker_reuse),
            "authoritative_image_outcome_failures": len(image_failures),
            "image_outcome_superseded_heads": len(superseded_heads),
            "image_outcome_pending_heads": len(pending_heads),
            "candidate_only_shadow_heads": len(shadow_only_heads),
            "integrity_failures": len(failures),
            "quarantined_by_tombstone": len(quarantined),
            "stale_tombstones": len(stale_tombstones),
            "receipts_superseded_policy_generation": len(drifted),
            "observations_skipped": sum(
                1 for item in index_value.get("exclusions", [])
                if str(item.get("reason", "")).startswith("observation-skipped:")
            ),
            "observation_receipts_missing": loss["receipts_missing"],
            "observation_receipts_pending_index": loss["receipts_pending_index"],
        },
        "skipped_observations_by_code": skipped_by_code(index_value.get("exclusions", [])),
        "gates": gates,
        "signals": signals,
        "docs_only_failures": docs_failures,
        "image_outcome_failures": image_failures,
        "image_outcome_superseded_heads": superseded_heads,
        "image_outcome_pending_heads": pending_heads,
        "candidate_only_shadow_heads": shadow_only_heads,
        "policy_generation_superseded_receipts": drifted,
        "quarantined": quarantined,
        "stale_tombstones": stale_tombstones,
        "integrity_failures": failures,
        "countable": {
            "docs_only": docs_success,
            "native": native_countable,
        },
        "reuse_eligible": {
            "serving": serving_reuse,
            "worker": worker_reuse,
        },
        "exclusions": index_value.get("exclusions", []),
    }


def skipped_by_code(exclusions: list[dict[str, Any]]) -> dict[str, int]:
    """Count recorded observation skips per bounded code, newest schema first."""
    counts: dict[str, int] = {}
    for item in exclusions:
        reason = str(item.get("reason", ""))
        if reason.startswith("observation-skipped:"):
            code = reason.split(":", 1)[1]
            counts[code] = counts.get(code, 0) + 1
    return dict(sorted(counts.items()))


def markdown(ledger: dict[str, Any]) -> str:
    counts = ledger["counts"]
    gates = ledger["gates"]
    policy = ledger["policy"]
    loss = ledger["receipt_emission"]["all"]
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
        "- Exact-input build-reuse-eligible heads (serving/worker): "
        f"`{counts['serving_reuse_eligible_heads']}` / `{counts['worker_reuse_eligible_heads']}`",
        "- Substantiated savings mechanism: "
        f"serving `{', '.join(ledger['savings_mechanism']['serving']) or 'none'}`, "
        f"worker `{', '.join(ledger['savings_mechanism']['worker']) or 'none'}`",
        f"- Docs-only gate failures: `{counts['docs_only_failure_heads']}`",
        f"- Native authoritative outcome failures: `{counts['authoritative_image_outcome_failures']}`"
        f" (excluded: `{counts['image_outcome_superseded_heads']}` superseded, "
        f"`{counts['image_outcome_pending_heads']}` still building)",
        "- Candidate-only heads awaiting execution evidence (not promotion-countable): "
        f"`{counts['candidate_only_shadow_heads']}`",
        f"- Receipt integrity failures: `{counts['integrity_failures']}`",
        "- Receipt loss: "
        f"`{loss['receipts_missing']}` of `{loss['receipts_owed']}` owed = "
        f"`{loss['loss_ratio'] * 100:.2f}%`"
        + ("" if loss["measured"] else " (unmeasured: nothing was owed)")
        + f", budget `{policy['maximum_receipt_loss_ratio'] * 100:.2f}%`",
        f"- Receipts awaiting indexing (not loss): `{counts['observation_receipts_pending_index']}`",
        "- Receipts outside the current policy generation "
        f"`{ledger['policy_generation_sha256'][:12]}` (cohort drift, not loss): "
        f"`{counts['receipts_superseded_policy_generation']}`",
        f"- Quarantined by tombstone: `{counts['quarantined_by_tombstone']}`"
        + (
            " — " + ", ".join(
                f"`{item['tombstone']['issue'].rsplit('/', 1)[-1]}` "
                f"(expires `{item['tombstone']['expires_at']}`)"
                for item in ledger.get("quarantined", [])
            )
            if ledger.get("quarantined") else ""
        ),
        f"- Tombstones matching nothing (candidates for removal): `{counts['stale_tombstones']}`",
        f"- Observations skipped (superseded source): `{counts['observations_skipped']}`"
        + (
            " — " + ", ".join(
                f"`{code}`: `{value}`"
                for code, value in ledger.get("skipped_observations_by_code", {}).items()
            )
            if ledger.get("skipped_observations_by_code") else ""
        ),
        f"- Successful observers that owed a receipt and left none: "
        f"`{counts['observation_receipts_missing']}`",
        "",
        "| Gate | Passed |",
        "|---|---|",
        rows,
        "",
        "Cohort drift is not receipt loss: classifier, routing-policy, or evidence-routing "
        + "workflow changes start a new semantic generation. Other workflow blob pins remain "
        + "provenance, so routing-irrelevant maintenance does not reset the cohort.",
        "A successful observer shell without one exact stable-name receipt is never counted. ",
        "Reuse is *build* reuse only: the GDAL worker's Trivy scan is re-run on every head "
        "because its verdict depends on the vulnerability database at scan time, never on a "
        "previous head's attestation.",
        "Native decisions are countable only when every required exact-head legacy image workflow has a successful outcome.",
        "",
    ])


def trend(ledgers: list[object], policy_value: object, now: datetime | None = None) -> dict[str, Any]:
    """Turn a pile of retained ledgers into the promotion gate's actual question.

    The gate for every shadow optimisation is "green for N consecutive days with
    receipt loss under budget". A single run cannot answer that, so the auditor
    reads its own retained ledger artifacts and states the streak directly
    rather than leaving a human to reconstruct it from run history.

    A day is green when the ledger for that day had zero integrity failures AND
    measured receipt loss inside the budget. A day with no ledger BREAKS the
    streak: a missing measurement is not a passing one.
    """
    policy = load_policy(policy_value)
    current = now or datetime.now(timezone.utc)
    days: dict[str, dict[str, Any]] = {}
    generations: list[tuple[datetime, str, int, int]] = []
    for value in ledgers:
        if not isinstance(value, dict) or value.get("contract") != LEDGER_CONTRACT:
            continue
        generated = value.get("generated_at")
        counts = value.get("counts")
        emission = value.get("receipt_emission")
        if not isinstance(counts, dict) or not isinstance(emission, dict):
            continue
        loss = emission.get("all")
        if not isinstance(loss, dict):
            continue
        try:
            stamp = parse_time(generated, "ledger generation")
        except ValueError:
            continue
        generation = value.get("policy_generation_sha256")
        docs_heads = counts.get("docs_only_success_heads")
        native_heads = counts.get("native_countable_heads")
        if (
            isinstance(generation, str)
            and DIGEST.fullmatch(generation) is not None
            and isinstance(docs_heads, int)
            and not isinstance(docs_heads, bool)
            and isinstance(native_heads, int)
            and not isinstance(native_heads, bool)
            and docs_heads >= 0
            and native_heads >= 0
        ):
            generations.append((stamp, generation, docs_heads, native_heads))
        day = stamp.date().isoformat()
        # Re-derive green from each ledger's OWN measurements against TODAY's
        # policy, never from the gate it recorded at the time. Trusting the
        # stored gate would let a day that passed under a looser budget keep
        # counting after the budget is tightened, so lowering
        # `maximum_receipt_loss_ratio` could leave `promotion_gate_ready` true
        # on evidence that no longer satisfies it.
        failures = counts.get("integrity_failures", 0)
        measured = bool(loss.get("measured"))
        ratio_value = loss.get("loss_ratio", 0.0)
        green = (
            failures == 0
            and measured
            and isinstance(ratio_value, (int, float))
            and not isinstance(ratio_value, bool)
            and float(ratio_value) < policy["maximum_receipt_loss_ratio"]
        )
        existing = days.get(day)
        # Two ledgers on one day (a schedule plus a dispatch) must not let the
        # greener one paper over the other.
        if existing is None or (existing["green"] and not green):
            days[day] = {
                "date": day,
                "green": green,
                "integrity_failures": failures,
                "loss_ratio": ratio_value if green else loss.get("loss_ratio", 0.0),
                "measured": measured,
            }
    streak = 0
    maximum_loss = 0.0
    cursor = current.date()
    # Today may not have run yet, so start the walk at the most recent day that
    # actually produced a ledger; a gap anywhere before that still breaks it.
    while cursor.isoformat() not in days and streak == 0 and cursor >= current.date() - timedelta(days=1):
        cursor -= timedelta(days=1)
    while True:
        day = days.get(cursor.isoformat())
        if day is None or not day["green"]:
            break
        streak += 1
        maximum_loss = max(maximum_loss, float(day["loss_ratio"]))
        cursor -= timedelta(days=1)
    required = policy["promotion_green_days"]
    generations.sort(key=lambda item: item[0])
    resets = sum(
        previous[1] != following[1]
        for previous, following in zip(generations, generations[1:])
    )
    observation_weeks = (
        (generations[-1][0] - generations[0][0]).total_seconds() / 604800
        if len(generations) > 1 else 0.0
    )
    resets_per_week = resets / observation_weeks if observation_weeks > 0 else 0.0
    largest_sample = max(
        generations,
        key=lambda item: (min(item[2], item[3]), item[2] + item[3], item[0]),
        default=None,
    )
    return {
        "contract": TREND_CONTRACT,
        "generated_at": current.isoformat().replace("+00:00", "Z"),
        "required_green_days": required,
        "maximum_receipt_loss_ratio": policy["maximum_receipt_loss_ratio"],
        "consecutive_green_days": streak,
        "maximum_loss_ratio_in_streak": round(maximum_loss, 6),
        "promotion_gate_ready": streak >= required,
        "policy_generation_resets": resets,
        "policy_generation_resets_per_week": round(resets_per_week, 3),
        "largest_sample_within_generation": {
            "docs_only_heads": largest_sample[2] if largest_sample else 0,
            "native_heads": largest_sample[3] if largest_sample else 0,
        },
        "days": [days[key] for key in sorted(days, reverse=True)],
    }


def trend_markdown(value: dict[str, Any]) -> str:
    rows = "\n".join(
        f"| `{day['date']}` | `{str(day['green']).lower()}` | "
        f"`{day['integrity_failures']}` | `{day['loss_ratio'] * 100:.2f}%` |"
        for day in value["days"]
    )
    return "\n".join([
        "## Impact-routing ledger promotion trend",
        "",
        f"Consecutive green days: **{value['consecutive_green_days']}** / "
        f"{value['required_green_days']} "
        f"(worst receipt loss in streak `{value['maximum_loss_ratio_in_streak'] * 100:.2f}%`, "
        f"budget `{value['maximum_receipt_loss_ratio'] * 100:.2f}%`)",
        f"Policy-generation resets: `{value['policy_generation_resets']}` "
        f"(`{value['policy_generation_resets_per_week']:.3f}` per week)",
        "Largest sample reached within one generation (docs-only/native): "
        f"`{value['largest_sample_within_generation']['docs_only_heads']}` / "
        f"`{value['largest_sample_within_generation']['native_heads']}`",
        "",
        f"Promotion gate ready: **{str(value['promotion_gate_ready']).lower()}** — no shadow "
        "optimisation may be promoted until this is true.",
        "",
        "| Day | Green | Integrity failures | Receipt loss |",
        "|---|---|---|---|",
        rows,
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
    discover_parser.add_argument("--receipt-cutoff", required=True)
    discover_parser.add_argument("--output", type=Path, required=True)

    summary_parser = subparsers.add_parser("summarize")
    summary_parser.add_argument("--policy", type=Path, required=True)
    summary_parser.add_argument("--index", type=Path, required=True)
    summary_parser.add_argument("--archives", type=Path, required=True)
    summary_parser.add_argument("--serving-runs", type=Path, required=True)
    summary_parser.add_argument("--worker-runs", type=Path, required=True)
    summary_parser.add_argument("--repository-root", type=Path, required=True)
    summary_parser.add_argument("--tombstones", type=Path)
    summary_parser.add_argument("--policy-head-sha")
    summary_parser.add_argument("--output", type=Path, required=True)
    summary_parser.add_argument("--markdown", type=Path, required=True)

    trend_parser = subparsers.add_parser("trend")
    trend_parser.add_argument("--policy", type=Path, required=True)
    trend_parser.add_argument("--ledgers", type=Path, required=True)
    trend_parser.add_argument("--output", type=Path, required=True)
    trend_parser.add_argument("--markdown", type=Path, required=True)
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
            "maximum_producer_run_catalogs": policy["maximum_producer_run_catalogs"],
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
            cutoff=parse_time(args.receipt_cutoff, "receipt cutoff"),
        )
        write_json(args.output, result)
        return 1 if result["integrity_failures"] else 0
    if args.command == "trend":
        ledgers: list[object] = []
        for path in sorted(args.ledgers.rglob("*.json")):
            try:
                ledgers.append(load_json(path))
            except (OSError, UnicodeDecodeError, ValueError):
                continue
        result = trend(ledgers, policy)
        write_json(args.output, result)
        args.markdown.write_text(trend_markdown(result), encoding="utf-8", newline="\n")
        print(trend_markdown(result))
        return 0
    if args.policy_head_sha is not None:
        exact_sha(args.policy_head_sha, "policy head")
    ledger = summarize(
        load_json(args.index),
        args.archives,
        args.serving_runs,
        args.worker_runs,
        policy,
        args.repository_root,
        load_json(args.tombstones) if args.tombstones else None,
        args.policy_head_sha,
    )
    write_json(args.output, ledger)
    args.markdown.write_text(markdown(ledger), encoding="utf-8", newline="\n")
    print(markdown(ledger))
    # Loss is a fail-closed condition for FUTURE receipts alongside integrity:
    # a producer that stops emitting must redden the ledger, and cohort drift —
    # which is not loss — must not. A window that owed nothing is unmeasured,
    # which blocks promotion but is not a regression to go red over.
    return 1 if (ledger["integrity_failures"] or ledger["receipt_loss_regression"]) else 0


if __name__ == "__main__":
    raise SystemExit(main())
