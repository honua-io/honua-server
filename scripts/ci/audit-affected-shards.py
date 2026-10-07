#!/usr/bin/env python3
"""Collect every PR Gate verdict in a fixed seven-day window; fail closed on unknown reds.

Read-only: this tool cannot change workflows, protection, PRs, or runs. Reuses
the impact-routing collector's complete time-sliced pagination and transport
backoff. A successful report-only check is not evidence that its shards passed.
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import re
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
from pathlib import Path


REPOSITORY = "honua-io/honua-server"
CONTEXT = "PR Gate / Affected shards"
MINIMUM_SAMPLES = 60
MAXIMUM_FALSE_RED_RATE = 0.02
RED = re.compile(r"^HONUA_AFFECTED_SHARD_RED shard='([^']+)' status='([^']+)'")
SHA = re.compile(r"[0-9a-f]{40}")


def collector():
    spec = importlib.util.spec_from_file_location(
        "impact_collector", Path(__file__).with_name("collect-impact-routing-runs.py")
    )
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def collect(upper: datetime, directory: Path, api=None):
    """Persist API inputs before summarizing; never silently truncate a catalog."""
    client = collector()
    fetch = api or client.github_page
    lower = upper - timedelta(days=7)
    directory.mkdir(parents=True, exist_ok=True)
    (directory / "observations.json").unlink(missing_ok=True)
    queries = client.collect(
        f"repos/{REPOSITORY}/actions/workflows/pr-gate.yml/runs",
        int(lower.timestamp()), int(upper.timestamp()) - 1, 1000,
        event="pull_request", fetch=fetch,
    )
    runs = [run for query in queries for page in query for run in page["workflow_runs"]]
    def read_run(run):
        checkpoint = directory / f"run-{run['id']}.json"
        if checkpoint.exists() and run["status"] == "completed":
            cached = json.loads(checkpoint.read_text())
            if all(cached.get(field) == run[source] for field, source in (
                ("run_id", "id"), ("run_attempt", "run_attempt"),
                ("head_sha", "head_sha"), ("created_at", "created_at"), ("status", "status"),
            )):
                return cached
        record = {
            "run_id": run["id"], "run_attempt": run["run_attempt"],
            "head_sha": run["head_sha"], "created_at": run["created_at"],
            "url": run["html_url"], "status": run["status"],
            "annotations": [], "aggregate_conclusion": None,
        }
        if run["status"] == "completed":
            # Attempt-qualified jobs prevent a prior successful rerun from
            # supplying the evidence for the catalog's current attempt.
            endpoint = f"repos/{REPOSITORY}/actions/runs/{run['id']}/attempts/{run['run_attempt']}/jobs"
            jobs = []
            for page in range(1, 101):
                value = fetch(endpoint, {"per_page": 100, "page": page})
                jobs.extend(value["jobs"])
                if len(jobs) == value["total_count"]:
                    break
                if not value["jobs"] or len(jobs) > value["total_count"]:
                    raise ValueError("incomplete job catalog")
            else:
                raise ValueError("job catalog exceeds bound")
            aggregates = [job for job in jobs if job["name"] == CONTEXT]
            if len(aggregates) == 1:
                job = aggregates[0]
                record["aggregate_conclusion"] = job["conclusion"]
                match = re.fullmatch(
                    rf"https://api.github.com/repos/{REPOSITORY}/check-runs/([1-9][0-9]*)",
                    job["check_run_url"],
                )
                if not match:
                    raise ValueError("aggregate check belongs to another repository")
                endpoint = f"repos/{REPOSITORY}/check-runs/{match[1]}"
                check = fetch(endpoint, {})
                expected = check["output"]["annotations_count"]
                for page in range(1, 101):
                    annotations = fetch(endpoint + "/annotations", {"per_page": 100, "page": page})
                    record["annotations"].extend(annotations)
                    if len(record["annotations"]) == expected:
                        break
                    if not annotations or len(record["annotations"]) > expected:
                        raise ValueError("incomplete annotation catalog")
                else:
                    raise ValueError("annotation catalog exceeds bound")
        # Incremental backup makes an interrupted collection reviewable, but
        # only observations.json below represents a COMPLETE collection.
        checkpoint.write_text(json.dumps(record, indent=2) + "\n")
        return record
    # Four independent, read-only API streams; catalog membership stays fixed.
    with ThreadPoolExecutor(max_workers=4) as pool:
        records = list(pool.map(read_run, runs))
    value = {
        "contract": "honua.affected-shards-observations/v1",
        "from": lower.isoformat(), "to": upper.isoformat(), "runs": records,
    }
    (directory / "observations.json").write_text(json.dumps(value, indent=2) + "\n")
    return value


def summarize(value, adjudications):
    lower = datetime.fromisoformat(value["from"].replace("Z", "+00:00"))
    upper = datetime.fromisoformat(value["to"].replace("Z", "+00:00"))
    if value.get("contract") != "honua.affected-shards-observations/v1" or upper - lower != timedelta(days=7):
        raise ValueError("expected one complete seven-day observation window")
    decisions = {}
    for entry in adjudications:
        key = (entry["run_id"], entry["run_attempt"], entry["head_sha"], entry["shard"])
        if key in decisions or entry["verdict"] not in {"confirmed-red", "false-red"}:
            raise ValueError("duplicate or invalid red adjudication")
        # Human reconciliation must link the same shard's trailing result for
        # the merge commit; a green rerun on a different head is not enough.
        if not SHA.fullmatch(entry["merge_sha"]) or not re.fullmatch(
            rf"https://github.com/{REPOSITORY}/actions/runs/[1-9][0-9]*/job/[1-9][0-9]*",
            entry["trailing_job_url"],
        ) or not entry.get("reason"):
            raise ValueError("adjudication lacks merge-commit trailing-shard evidence")
        decisions[key] = entry["verdict"]
    seen = set()
    used = set()
    samples, unavailable, skipped, pending, reds, false_reds, unresolved = [], [], [], [], [], [], []
    for run in value["runs"]:
        identity = run["run_id"]
        if identity in seen or not SHA.fullmatch(run["head_sha"]):
            raise ValueError("duplicate run or invalid head")
        seen.add(identity)
        created = datetime.fromisoformat(run["created_at"].replace("Z", "+00:00"))
        if not lower <= created < upper:
            raise ValueError("run outside the declared window")
        if run["status"] != "completed":
            pending.append(identity)
            continue
        messages = [entry["message"] for entry in run["annotations"]]
        red = [RED.match(message) for message in messages if message.startswith("HONUA_AFFECTED_SHARD_RED")]
        if any(match is None for match in red):
            raise ValueError("malformed shard-red annotation")
        green = any(message.startswith("HONUA_AFFECTED_SHARD_GREEN ") for message in messages)
        missing = any(message.startswith("HONUA_AFFECTED_SHARD_UNAVAILABLE") for message in messages)
        if green and (red or missing):
            raise ValueError("conflicting shard verdict annotations")
        if red:
            samples.append(run)
            reds.append(identity)
            verdicts = []
            for match in red:
                shard = match[1]
                key = (identity, run["run_attempt"], run["head_sha"], shard)
                decision = decisions.get(key)
                if decision is None:
                    unresolved.append({"run_id": identity, "head_sha": run["head_sha"], "shard": shard, "status": match[2]})
                else:
                    used.add(key)
                verdicts.append(decision)
            if "false-red" in verdicts:
                false_reds.append(identity)
        elif missing or run["aggregate_conclusion"] != "success":
            unavailable.append(identity)
        elif green:
            samples.append(run)
        else:
            # A docs/infrastructure-only selection emits no shard annotation;
            # success is deliberately excluded from the executed sample.
            skipped.append(identity)
    if used != set(decisions):
        raise ValueError("adjudication does not match a red in this window")
    count = len(samples)
    rate = len(false_reds) / count if count else None
    unresolved_runs = {entry["run_id"] for entry in unresolved}
    maximum = len(set(false_reds) | unresolved_runs) / count if count else None
    gates = {
        "sample_floor": count >= MINIMUM_SAMPLES,
        "all_reds_reconciled": not unresolved,
        "false_red_rate_below_two_percent": rate is not None and not unresolved and rate < MAXIMUM_FALSE_RED_RATE,
    }
    return {
        "contract": "honua.affected-shards-audit/v1", "from": value["from"], "to": value["to"],
        "minimum_samples": MINIMUM_SAMPLES, "maximum_false_red_rate": MAXIMUM_FALSE_RED_RATE,
        "counts": {"catalog_runs": len(seen), "sample_runs": count,
                   "distinct_sample_heads": len({run["head_sha"] for run in samples}),
                   "red_runs": len(reds), "false_red_runs": len(false_reds),
                   "unresolved_red_runs": len(unresolved_runs), "unresolved_red_shards": len(unresolved),
                   "unavailable_runs": len(unavailable), "skipped_runs": len(skipped), "pending_runs": len(pending)},
        "false_red_rate": rate if not unresolved else None, "false_red_rate_lower_bound": rate,
        "false_red_rate_upper_bound": maximum, "sample_shortfall": max(0, MINIMUM_SAMPLES - count),
        "gates": gates, "promotion_ready": all(gates.values()), "unresolved": unresolved,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--collect", type=Path, help="collect a complete window into this directory")
    parser.add_argument("--as-of", help="exclusive UTC window end (defaults to now)")
    parser.add_argument("--observations", type=Path, help="replay a previously complete collection")
    parser.add_argument("--adjudications", type=Path, help="audited red reconciliations; defaults to none")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if bool(args.collect) == bool(args.observations):
        parser.error("choose exactly one of --collect or --observations")
    if args.collect:
        upper = datetime.fromisoformat(args.as_of.replace("Z", "+00:00")) if args.as_of else datetime.now(timezone.utc)
        if upper.utcoffset() != timedelta(0):
            parser.error("--as-of must be UTC")
        value = collect(upper.replace(microsecond=0), args.collect)
    else:
        value = json.loads(args.observations.read_text())
    adjudications = json.loads(args.adjudications.read_text()) if args.adjudications else []
    result = summarize(value, adjudications)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps(result, indent=2))
    return 0 if result["promotion_ready"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
