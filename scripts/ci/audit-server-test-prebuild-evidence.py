#!/usr/bin/env python3
"""Build a fail-closed, report-only ledger from hosted prebuild parity receipts."""

from __future__ import annotations

import argparse
import importlib.util
import json
import math
import re
import stat
import zipfile
from collections import Counter, defaultdict
from pathlib import Path, PurePosixPath

INDEX_CONTRACT = "honua.server-test-prebuild-evidence-index/v1"
LEDGER_CONTRACT = "honua.server-test-prebuild-evidence-ledger/v1"
OBSERVATION_CONTRACT = "honua.server-test-prebuild-parity-observation/v1"
SUMMARY_CONTRACT = "honua.server-test-prebuild-benchmark-summary/v1"
POLICY_CONTRACT = "honua.server-test-prebuild-promotion-policy/v1"
WORKFLOW_NAME = "Server Test Prebuild Parity Observation"
WORKFLOW_PATH = ".github/workflows/server-test-prebuild-parity.yml"
ARTIFACT = re.compile(
    r"^server-test-prebuild-parity-receipt-(?P<pr>[1-9][0-9]*)-"
    r"(?P<head>[0-9a-f]{40})-attempt-(?P<attempt>[1-9][0-9]*)$"
)
SHA = re.compile(r"^[0-9a-f]{40}$")
MAX_RECEIPT_BYTES = 2 * 1024 * 1024
MAX_ARCHIVE_BYTES = 20 * 1024 * 1024
PREBUILD_RECEIPT_SCRIPT = Path(__file__).with_name("server-test-prebuild-receipt.py")
PREBUILD_RECEIPT_SPEC = importlib.util.spec_from_file_location(
    "server_test_prebuild_receipt_for_evidence", PREBUILD_RECEIPT_SCRIPT
)
assert PREBUILD_RECEIPT_SPEC and PREBUILD_RECEIPT_SPEC.loader
PREBUILD_RECEIPT = importlib.util.module_from_spec(PREBUILD_RECEIPT_SPEC)
PREBUILD_RECEIPT_SPEC.loader.exec_module(PREBUILD_RECEIPT)


def load_json(path: Path) -> object:
    return json.loads(path.read_text(encoding="utf-8"))


def measurement_policy_digest(policy_root: Path, policy_sha: str) -> str:
    return PREBUILD_RECEIPT.policy_inputs_digest(policy_root, policy_sha)


def flatten_runs(payload: object) -> list[dict]:
    pages = payload if isinstance(payload, list) else [payload]
    runs: list[dict] = []
    for page in pages:
        if not isinstance(page, dict) or not isinstance(page.get("workflow_runs"), list):
            raise ValueError("workflow-run payload is invalid")
        runs.extend(item for item in page["workflow_runs"] if isinstance(item, dict))
    return runs


def discover(runs_payload: object, catalog_root: Path, default_branch: str) -> dict:
    artifacts: list[dict] = []
    exclusions: list[dict] = []
    seen_runs: set[int] = set()
    for run in sorted(flatten_runs(runs_payload), key=lambda item: item.get("created_at", "")):
        run_id = run.get("id")
        if not isinstance(run_id, int) or run_id <= 0 or run_id in seen_runs:
            continue
        seen_runs.add(run_id)
        if (
            run.get("path") != WORKFLOW_PATH
            or run.get("status") != "completed"
            or run.get("conclusion") != "success"
            or run.get("head_branch") != default_branch
            or run.get("event") not in {"workflow_run", "workflow_dispatch"}
            or not SHA.fullmatch(str(run.get("head_sha", "")))
            or not isinstance(run.get("run_attempt"), int)
            or run["run_attempt"] <= 0
        ):
            continue
        catalog_path = catalog_root / f"{run_id}.json"
        if not catalog_path.is_file():
            exclusions.append({"run_id": run_id, "reason": "artifact-catalog-missing"})
            continue
        catalog = load_json(catalog_path)
        values = catalog.get("artifacts") if isinstance(catalog, dict) else None
        if not isinstance(values, list):
            exclusions.append({"run_id": run_id, "reason": "artifact-catalog-invalid"})
            continue
        matches = []
        for artifact in values:
            if not isinstance(artifact, dict) or artifact.get("expired") is not False:
                continue
            match = ARTIFACT.fullmatch(str(artifact.get("name", "")))
            if match and int(match.group("attempt")) == run["run_attempt"]:
                matches.append((artifact, match))
        if len(matches) != 1:
            exclusions.append(
                {
                    "run_id": run_id,
                    "reason": (
                        "evidence-artifact-missing"
                        if not matches
                        else "evidence-artifact-ambiguous"
                    ),
                }
            )
            continue
        artifact, match = matches[0]
        artifact_id = artifact.get("id")
        attempt = int(match.group("attempt"))
        if not isinstance(artifact_id, int) or artifact_id <= 0 or attempt != run["run_attempt"]:
            exclusions.append({"run_id": run_id, "reason": "evidence-artifact-identity-mismatch"})
            continue
        artifacts.append(
            {
                "artifact_id": artifact_id,
                "artifact_name": artifact["name"],
                "created_at": artifact.get("created_at"),
                "head_sha": match.group("head"),
                "pull_request": int(match.group("pr")),
                "run_attempt": run["run_attempt"],
                "run_id": run_id,
                "verifier_policy_sha": run["head_sha"],
            }
        )
    return {
        "contract": INDEX_CONTRACT,
        "workflow": {"name": WORKFLOW_NAME, "path": WORKFLOW_PATH},
        "artifacts": artifacts,
        "exclusions": exclusions,
    }


def positive_int(value: object, label: str) -> int:
    if not isinstance(value, int) or isinstance(value, bool) or value <= 0:
        raise ValueError(f"{label} is invalid")
    return value


def load_receipt_archive(root: Path, artifact_id: int) -> object:
    path = root / f"{artifact_id}.zip"
    if not path.is_file() or path.is_symlink():
        raise ValueError("receipt archive is missing or unsafe")
    if path.stat().st_size > MAX_ARCHIVE_BYTES:
        raise ValueError("receipt archive is oversized")
    with zipfile.ZipFile(path) as archive:
        matches: list[zipfile.ZipInfo] = []
        names: set[str] = set()
        for info in archive.infolist():
            member = PurePosixPath(info.filename)
            if (
                not info.filename
                or "\\" in info.filename
                or member.is_absolute()
                or any(part in {"", ".", ".."} for part in member.parts)
                or info.filename in names
                or stat.S_ISLNK(info.external_attr >> 16)
                or info.flag_bits & 0x1
            ):
                raise ValueError("receipt archive contains an unsafe member")
            names.add(info.filename)
            if not info.is_dir() and member.name == "parity-observation.json":
                matches.append(info)
        if len(matches) != 1:
            raise ValueError("receipt file is missing or ambiguous")
        receipt = matches[0]
        if receipt.file_size > MAX_RECEIPT_BYTES:
            raise ValueError("receipt file is oversized")
        with archive.open(receipt) as stream:
            payload = stream.read(MAX_RECEIPT_BYTES + 1)
        if len(payload) > MAX_RECEIPT_BYTES:
            raise ValueError("receipt file is oversized")
        return json.loads(payload.decode("utf-8"))


def validate_receipt(entry: dict, value: object) -> dict:
    positive_int(entry.get("pull_request"), "index pull request")
    positive_int(entry.get("run_attempt"), "index run attempt")
    positive_int(entry.get("artifact_id"), "index artifact id")
    if not SHA.fullmatch(str(entry.get("head_sha", ""))):
        raise ValueError("index head sha is invalid")
    if not SHA.fullmatch(str(entry.get("verifier_policy_sha", ""))):
        raise ValueError("index policy sha is invalid")
    artifact_match = ARTIFACT.fullmatch(str(entry.get("artifact_name", "")))
    if (
        artifact_match is None
        or int(artifact_match.group("pr")) != entry["pull_request"]
        or artifact_match.group("head") != entry["head_sha"]
        or int(artifact_match.group("attempt")) != entry["run_attempt"]
    ):
        raise ValueError("index artifact name is inconsistent")
    if not isinstance(value, dict) or value.get("contract") != OBSERVATION_CONTRACT:
        raise ValueError("receipt contract is invalid")
    if (
        value.get("pull_request") != entry["pull_request"]
        or value.get("head_sha") != entry["head_sha"]
        or value.get("verifier_run_id") != entry["run_id"]
        or not re.fullmatch(r"[0-9a-f]{64}", str(value.get("measurement_policy_digest", "")))
        or not isinstance(value.get("countable"), bool)
    ):
        raise ValueError("receipt identity is inconsistent")
    producer_run_id = positive_int(value.get("producer_run_id"), "producer run id")
    summary = value.get("summary")
    if not isinstance(summary, dict) or summary.get("contract") != SUMMARY_CONTRACT:
        raise ValueError("receipt summary contract is invalid")
    if (
        summary.get("head_sha") != entry["head_sha"]
        or not isinstance(summary.get("profile"), str)
        or not summary["profile"].strip()
    ):
        raise ValueError("receipt summary identity is inconsistent")
    parity_failures = summary.get("parity_failures")
    reuse_failures = summary.get("reuse_failures")
    if not isinstance(parity_failures, list) or not isinstance(reuse_failures, list):
        raise ValueError("receipt failure evidence is invalid")
    baseline = summary.get("baseline")
    candidate = summary.get("candidate")
    if not isinstance(baseline, dict) or not isinstance(candidate, dict):
        raise ValueError("receipt cost evidence is missing")
    baseline_minutes = positive_int(baseline.get("rounded_runner_minutes"), "baseline minutes")
    candidate_minutes = positive_int(
        candidate.get("rounded_runner_minutes_including_prebuild"), "candidate minutes"
    )
    baseline_p90_test_start_ms = positive_int(
        baseline.get("p90_test_start_ms"), "baseline p90 test start"
    )
    candidate_p90_test_start_ms = positive_int(
        candidate.get("p90_test_start_ms"), "candidate p90 test start"
    )
    baseline_wall_clock_ms = positive_int(baseline.get("wall_clock_ms"), "baseline wall clock")
    candidate_wall_clock_ms = positive_int(candidate.get("wall_clock_ms"), "candidate wall clock")
    head_to_first_test_ms = positive_int(
        candidate.get("head_to_first_test_ms"), "head-to-first-test"
    )
    truthful_countable = (
        not parity_failures
        and not reuse_failures
        and summary.get("producer_evidence_ok") is True
        and summary.get("producer_ready_before_candidate") is True
    )
    if value["countable"] and not truthful_countable:
        raise ValueError("receipt claims countable despite contradictory evidence")
    return {
        "baseline_minutes": baseline_minutes,
        "baseline_p90_test_start_ms": baseline_p90_test_start_ms,
        "baseline_wall_clock_ms": baseline_wall_clock_ms,
        "candidate_minutes": candidate_minutes,
        "candidate_p90_test_start_ms": candidate_p90_test_start_ms,
        "candidate_wall_clock_ms": candidate_wall_clock_ms,
        "countable": value["countable"],
        "artifact_id": entry["artifact_id"],
        "head_sha": entry["head_sha"],
        "head_to_first_test_ms": head_to_first_test_ms,
        "measurement_policy_digest": value["measurement_policy_digest"],
        "profile": summary["profile"],
        "producer_run_id": producer_run_id,
        "pull_request": entry["pull_request"],
        "run_id": entry["run_id"],
        "verifier_policy_sha": entry["verifier_policy_sha"],
    }


def nearest_rank(values: list[int], percentile: float) -> int | None:
    if not values:
        return None
    ordered = sorted(values)
    return ordered[max(0, math.ceil(percentile * len(ordered)) - 1)]


def summarize(
    index: object,
    receipts_root: Path,
    policy: object,
    current_policy_digest: str,
) -> dict:
    if not isinstance(index, dict) or index.get("contract") != INDEX_CONTRACT:
        raise ValueError("evidence index contract is invalid")
    if index.get("workflow") != {"name": WORKFLOW_NAME, "path": WORKFLOW_PATH}:
        raise ValueError("evidence index workflow identity is invalid")
    if not isinstance(policy, dict) or policy.get("contract") != POLICY_CONTRACT:
        raise ValueError("promotion policy contract is invalid")
    if not re.fullmatch(r"[0-9a-f]{64}", current_policy_digest):
        raise ValueError("current measurement policy digest is invalid")
    minimum_heads = positive_int(policy.get("minimum_countable_heads"), "minimum countable heads")
    minimum_cost = positive_int(policy.get("minimum_cost_heads"), "minimum cost heads")
    required_profiles = policy.get("required_profiles")
    if (
        not isinstance(required_profiles, list)
        or len(required_profiles) < 2
        or not all(isinstance(item, str) and item.strip() for item in required_profiles)
        or len(required_profiles) != len(set(required_profiles))
    ):
        raise ValueError("required profiles are invalid")
    minimum_heads_per_profile = positive_int(
        policy.get("minimum_countable_heads_per_profile"),
        "minimum countable heads per profile",
    )
    minimum_cost_per_profile = positive_int(
        policy.get("minimum_cost_heads_per_profile"), "minimum cost heads per profile"
    )
    minimum_savings = policy.get("minimum_runner_minute_savings_percent")
    if (
        not isinstance(minimum_savings, (int, float))
        or isinstance(minimum_savings, bool)
        or not 0 <= minimum_savings <= 100
    ):
        raise ValueError("minimum runner-minute savings is invalid")
    require_p90_improvement = policy.get("require_p90_test_start_improvement")
    if not isinstance(require_p90_improvement, bool):
        raise ValueError("p90 test-start policy is invalid")
    maximum_wall_regression = policy.get("max_wall_clock_regression_percent")
    if (
        not isinstance(maximum_wall_regression, (int, float))
        or isinstance(maximum_wall_regression, bool)
        or not 0 <= maximum_wall_regression <= 100
    ):
        raise ValueError("wall-clock regression policy is invalid")
    entries = index.get("artifacts")
    if not isinstance(entries, list):
        raise ValueError("evidence index artifacts are invalid")
    discovery_exclusions = index.get("exclusions")
    if not isinstance(discovery_exclusions, list):
        raise ValueError("evidence index exclusions are invalid")

    observations: list[dict] = []
    integrity_failures: list[dict] = []
    for entry in entries:
        if not isinstance(entry, dict):
            integrity_failures.append({"run_id": None, "reason": "index-entry-invalid"})
            continue
        run_id = entry.get("run_id")
        try:
            positive_int(run_id, "run id")
            observations.append(
                validate_receipt(entry, load_receipt_archive(receipts_root, entry["artifact_id"]))
            )
        except (
            KeyError,
            OSError,
            RuntimeError,
            TypeError,
            UnicodeDecodeError,
            json.JSONDecodeError,
            ValueError,
            zipfile.BadZipFile,
        ) as error:
            integrity_failures.append({"run_id": run_id, "reason": str(error)})

    current_observations = [
        item for item in observations if item["measurement_policy_digest"] == current_policy_digest
    ]
    noncurrent_policy_observations = len(observations) - len(current_observations)
    unexpected_profiles = sorted(
        {item["profile"] for item in current_observations} - set(required_profiles)
    )
    if unexpected_profiles:
        integrity_failures.append(
            {
                "run_id": None,
                "reason": f"current policy produced unsupported profiles: {unexpected_profiles}",
            }
        )

    grouped: dict[str, list[dict]] = defaultdict(list)
    for observation in current_observations:
        grouped[observation["head_sha"]].append(observation)
    duplicate_heads = sorted(head for head, values in grouped.items() if len(values) != 1)
    countable = [
        values[0]
        for values in grouped.values()
        if len(values) == 1 and values[0]["countable"]
    ]
    profiles = Counter(item["profile"] for item in countable)
    baseline_minutes = sum(item["baseline_minutes"] for item in countable)
    candidate_minutes = sum(item["candidate_minutes"] for item in countable)
    savings_percent = (
        round((baseline_minutes - candidate_minutes) * 100 / baseline_minutes, 2)
        if baseline_minutes
        else None
    )
    baseline_p90_test_start_ms = nearest_rank(
        [item["baseline_p90_test_start_ms"] for item in countable], 0.90
    )
    candidate_p90_test_start_ms = nearest_rank(
        [item["candidate_p90_test_start_ms"] for item in countable], 0.90
    )
    baseline_p90_wall_clock_ms = nearest_rank(
        [item["baseline_wall_clock_ms"] for item in countable], 0.90
    )
    candidate_p90_wall_clock_ms = nearest_rank(
        [item["candidate_wall_clock_ms"] for item in countable], 0.90
    )
    parity_ready = len(countable) >= minimum_heads
    cost_ready = len(countable) >= minimum_cost
    profile_results: dict[str, dict] = {}
    for profile in required_profiles:
        values = [item for item in countable if item["profile"] == profile]
        profile_baseline_minutes = sum(item["baseline_minutes"] for item in values)
        profile_candidate_minutes = sum(item["candidate_minutes"] for item in values)
        profile_savings = (
            round(
                (profile_baseline_minutes - profile_candidate_minutes)
                * 100
                / profile_baseline_minutes,
                2,
            )
            if profile_baseline_minutes
            else None
        )
        profile_baseline_start = nearest_rank(
            [item["baseline_p90_test_start_ms"] for item in values], 0.90
        )
        profile_candidate_start = nearest_rank(
            [item["candidate_p90_test_start_ms"] for item in values], 0.90
        )
        profile_baseline_wall = nearest_rank(
            [item["baseline_wall_clock_ms"] for item in values], 0.90
        )
        profile_candidate_wall = nearest_rank(
            [item["candidate_wall_clock_ms"] for item in values], 0.90
        )
        profile_results[profile] = {
            "countable_heads": len(values),
            "runner_minute_savings_percent": profile_savings,
            "baseline_p90_test_start_ms": profile_baseline_start,
            "candidate_p90_test_start_ms": profile_candidate_start,
            "baseline_p90_wall_clock_ms": profile_baseline_wall,
            "candidate_p90_wall_clock_ms": profile_candidate_wall,
            "gates": {
                "parity_sample_ready": len(values) >= minimum_heads_per_profile,
                "cost_sample_ready": len(values) >= minimum_cost_per_profile,
                "runner_minute_target_met": (
                    profile_savings is not None and profile_savings >= minimum_savings
                ),
                "p90_test_start_improved": (
                    profile_baseline_start is not None
                    and profile_candidate_start is not None
                    and (
                        profile_candidate_start < profile_baseline_start
                        if require_p90_improvement
                        else profile_candidate_start <= profile_baseline_start
                    )
                ),
                "p90_wall_clock_within_budget": (
                    profile_baseline_wall is not None
                    and profile_candidate_wall is not None
                    and profile_candidate_wall * 100
                    <= profile_baseline_wall * (100 + maximum_wall_regression)
                ),
            },
        }
    profile_ready = all(
        value["gates"]["parity_sample_ready"] and value["gates"]["cost_sample_ready"]
        for value in profile_results.values()
    )
    savings_ready = all(
        value["gates"]["runner_minute_target_met"] for value in profile_results.values()
    )
    p90_test_start_ready = all(
        value["gates"]["p90_test_start_improved"] for value in profile_results.values()
    )
    wall_clock_ready = all(
        value["gates"]["p90_wall_clock_within_budget"] for value in profile_results.values()
    )
    recommendation = (
        "eligible-for-human-promotion-review"
        if parity_ready
        and cost_ready
        and profile_ready
        and savings_ready
        and p90_test_start_ready
        and wall_clock_ready
        and not integrity_failures
        and not duplicate_heads
        else "insufficient-evidence"
    )
    return {
        "contract": LEDGER_CONTRACT,
        "mode": "report-only",
        "mutation": "none",
        "promotion_authority": "none",
        "measurement_policy_digest": current_policy_digest,
        "recommendation": recommendation,
        "thresholds": policy,
        "counts": {
            "artifacts": len(entries),
            "validated_receipts": len(observations),
            "current_policy_receipts": len(current_observations),
            "noncurrent_policy_receipts": noncurrent_policy_observations,
            "distinct_countable_heads": len(countable),
            "distinct_profiles": len(profiles),
            "excluded_successful_shells": len(discovery_exclusions),
        },
        "profiles": profile_results,
        "cost": {
            "baseline_rounded_runner_minutes": baseline_minutes,
            "candidate_rounded_runner_minutes": candidate_minutes,
            "runner_minute_savings_percent": savings_percent,
            "baseline_p90_test_start_ms": baseline_p90_test_start_ms,
            "candidate_p90_test_start_ms": candidate_p90_test_start_ms,
            "baseline_p90_wall_clock_ms": baseline_p90_wall_clock_ms,
            "candidate_p90_wall_clock_ms": candidate_p90_wall_clock_ms,
            "p90_head_to_first_test_ms": nearest_rank(
                [item["head_to_first_test_ms"] for item in countable], 0.90
            ),
        },
        "gates": {
            "parity_sample_ready": parity_ready,
            "cost_sample_ready": cost_ready,
            "profile_sample_ready": profile_ready,
            "runner_minute_target_met": savings_ready,
            "p90_test_start_improved": p90_test_start_ready,
            "p90_wall_clock_within_budget": wall_clock_ready,
            "integrity_clean": not integrity_failures and not duplicate_heads,
        },
        "countable_observations": sorted(
            countable, key=lambda item: (item["head_sha"], item["run_id"])
        ),
        "duplicate_heads": duplicate_heads,
        "integrity_failures": integrity_failures,
        "discovery_exclusions": discovery_exclusions,
    }


def markdown(ledger: dict) -> str:
    counts = ledger["counts"]
    cost = ledger["cost"]
    gates = ledger["gates"]

    def metric(value: object, suffix: str = "") -> str:
        return "`n/a`" if value is None else f"`{value}`{suffix}"

    return "\n".join(
        [
            "# Server-test prebuild evidence ledger",
            "",
            f"Recommendation: **{ledger['recommendation']}** (report-only; no promotion authority)",
            "",
            f"- Distinct countable exact heads: `{counts['distinct_countable_heads']}`",
            f"- Distinct profiles: `{counts['distinct_profiles']}`",
            "- Current/noncurrent policy receipts: "
            f"`{counts['current_policy_receipts']}` / "
            f"`{counts['noncurrent_policy_receipts']}`",
            "- Successful shells excluded for missing/invalid evidence: "
            f"`{counts['excluded_successful_shells']}`",
            f"- Runner-minute savings: {metric(cost['runner_minute_savings_percent'], '%')}",
            "- Baseline/candidate p90 test start: "
            f"{metric(cost['baseline_p90_test_start_ms'])} / "
            f"{metric(cost['candidate_p90_test_start_ms'], ' ms')}",
            "- Baseline/candidate p90 wall clock: "
            f"{metric(cost['baseline_p90_wall_clock_ms'])} / "
            f"{metric(cost['candidate_p90_wall_clock_ms'], ' ms')}",
            f"- p90 head-to-first-test: {metric(cost['p90_head_to_first_test_ms'], ' ms')}",
            f"- Integrity failures: `{len(ledger['integrity_failures'])}`",
            f"- Duplicate exact heads: `{len(ledger['duplicate_heads'])}`",
            "",
            "| Gate | Ready |",
            "|---|---|",
            *[f"| `{name}` | `{str(value).lower()}` |" for name, value in gates.items()],
            "",
            "| Required profile | Heads | Savings | p90 start | p90 wall |",
            "|---|---:|---:|---:|---:|",
            *[
                f"| `{name}` | {value['countable_heads']} | "
                f"{metric(value['runner_minute_savings_percent'], '%')} | "
                f"{metric(value['candidate_p90_test_start_ms'], ' ms')} | "
                f"{metric(value['candidate_p90_wall_clock_ms'], ' ms')} |"
                for name, value in ledger["profiles"].items()
            ],
            "",
            "A green workflow shell without a validated receipt is never counted.",
        ]
    ) + "\n"


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    discover_parser = subparsers.add_parser("discover")
    discover_parser.add_argument("--runs", type=Path, required=True)
    discover_parser.add_argument("--catalog", type=Path, required=True)
    discover_parser.add_argument("--default-branch", required=True)
    discover_parser.add_argument("--output", type=Path, required=True)
    digest_parser = subparsers.add_parser("policy-digest")
    digest_parser.add_argument("--policy-root", type=Path, required=True)
    digest_parser.add_argument("--policy-sha", required=True)
    digest_parser.add_argument("--github-output", type=Path)
    summarize_parser = subparsers.add_parser("summarize")
    summarize_parser.add_argument("--index", type=Path, required=True)
    summarize_parser.add_argument("--receipts", type=Path, required=True)
    summarize_parser.add_argument("--policy", type=Path, required=True)
    summarize_parser.add_argument("--policy-digest", required=True)
    summarize_parser.add_argument("--output", type=Path, required=True)
    summarize_parser.add_argument("--markdown", type=Path, required=True)
    args = parser.parse_args()
    if args.command == "discover":
        write_json(args.output, discover(load_json(args.runs), args.catalog, args.default_branch))
        return 0
    if args.command == "policy-digest":
        digest = measurement_policy_digest(args.policy_root, args.policy_sha)
        if args.github_output:
            with args.github_output.open("a", encoding="utf-8", newline="\n") as handle:
                handle.write(f"measurement_policy_digest={digest}\n")
        print(digest)
        return 0
    ledger = summarize(
        load_json(args.index),
        args.receipts,
        load_json(args.policy),
        args.policy_digest,
    )
    write_json(args.output, ledger)
    rendered = markdown(ledger)
    args.markdown.write_text(rendered, encoding="utf-8")
    print(rendered)
    return 1 if not ledger["gates"]["integrity_clean"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
