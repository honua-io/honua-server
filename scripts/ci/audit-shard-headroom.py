#!/usr/bin/env python3
"""Audit Honua.Server.Tests shard timeout headroom (#3054).

`scripts/ci/run-server-test-shard.sh` writes one `*.timing.json` per shard run
and flags an individual run that consumed too much of its budget. This script is
the fleet view: point it at a directory of collected timing artifacts (one or
many CI runs) and it reports, per shard, the observed p50/p90 test-step duration
against the `test_timeout_minutes` configured in `.github/ci-shards.json`.

Use it to re-base budgets. The rule recorded in `shard_budget_policy` is:

* keep measured p90 at or below `target_utilization` of the inner cap, and
* keep `timeout_minutes` at least `min_job_overhead_minutes` above the inner cap
  (enforced statically by `scripts/ci/validate-ci-router.sh`).

Examples::

    # after `gh run download <run-id> -p 'server-test-results-*'`
    scripts/ci/audit-shard-headroom.py --timings-dir ./artifacts --markdown

    # gate a collection of runs: non-zero exit if any shard is over the warn line
    scripts/ci/audit-shard-headroom.py --timings-dir ./artifacts --fail-on-warn
"""

from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_CONFIG = REPO_ROOT / ".github" / "ci-shards.json"


def percentile(values: list[float], fraction: float) -> float:
    """Nearest-rank percentile over an already-sorted, non-empty list."""
    index = min(len(values) - 1, max(0, int(round(fraction * (len(values) - 1)))))
    return values[index]


def load_timings(directory: Path) -> dict[str, list[dict]]:
    observations: dict[str, list[dict]] = {}
    for path in sorted(directory.rglob("*.timing.json")):
        try:
            record = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as error:
            print(f"warning: skipping unreadable timing artifact {path}: {error}", file=sys.stderr)
            continue
        shard = record.get("shard")
        if not shard:
            continue
        observations.setdefault(shard, []).append(record)
    return observations


def audit(config: dict, observations: dict[str, list[dict]]) -> list[dict]:
    policy = config.get("shard_budget_policy", {})
    target = float(policy.get("target_utilization", 0.7))
    warn = float(policy.get("warn_utilization", 0.8))

    rows: list[dict] = []
    for shard in config["shards"]:
        name = shard["shard_name"]
        cap_minutes = shard["test_timeout_minutes"]
        records = observations.get(name, [])
        durations = sorted(r["duration_seconds"] / 60.0 for r in records if "duration_seconds" in r)
        timeouts = sum(1 for r in records if r.get("timed_out"))
        row = {
            "shard": name,
            "runs": len(durations),
            "timeouts": timeouts,
            "test_timeout_minutes": cap_minutes,
            "timeout_minutes": shard["timeout_minutes"],
            "p50": None,
            "p90": None,
            "utilization": None,
            "status": "no_data",
            "recommended_test_timeout_minutes": cap_minutes,
        }
        if durations:
            row["p50"] = round(percentile(durations, 0.5), 1)
            row["p90"] = round(percentile(durations, 0.9), 1)
            utilization = row["p90"] / cap_minutes
            row["utilization"] = round(utilization, 3)
            # A censored sample: a shard that was killed at its cap would have
            # run longer, so its true p90 is at least the cap.
            reference = max(row["p90"], cap_minutes) if timeouts else row["p90"]
            if utilization >= 1.0 or timeouts:
                row["status"] = "over_capacity"
            elif utilization >= warn:
                row["status"] = "low_headroom"
            else:
                row["status"] = "ok"
            if row["status"] != "ok":
                row["recommended_test_timeout_minutes"] = int(math.ceil(reference / target))
        rows.append(row)
    rows.sort(key=lambda r: (r["utilization"] is None, -(r["utilization"] or 0)))
    return rows


def render_markdown(rows: list[dict]) -> str:
    lines = [
        "| Shard | Runs | Timeouts | p50 (min) | p90 (min) | Cap (min) | p90 / cap | Status | Recommended cap |",
        "|---|---:|---:|---:|---:|---:|---:|---|---:|",
    ]
    for row in rows:
        utilization = "-" if row["utilization"] is None else f"{row['utilization'] * 100:.0f}%"
        recommended = (
            "-"
            if row["recommended_test_timeout_minutes"] == row["test_timeout_minutes"]
            else str(row["recommended_test_timeout_minutes"])
        )
        lines.append(
            f"| {row['shard']} | {row['runs']} | {row['timeouts']} | {row['p50'] or '-'} | "
            f"{row['p90'] or '-'} | {row['test_timeout_minutes']} | {utilization} | "
            f"{row['status']} | {recommended} |"
        )
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--config", type=Path, default=DEFAULT_CONFIG, help="path to ci-shards.json")
    parser.add_argument(
        "--timings-dir",
        type=Path,
        required=True,
        help="directory searched recursively for *.timing.json shard artifacts",
    )
    parser.add_argument("--markdown", action="store_true", help="emit a markdown table instead of JSON")
    parser.add_argument(
        "--fail-on-warn",
        action="store_true",
        help="exit non-zero when any shard is at or above the configured warn utilization",
    )
    args = parser.parse_args(argv)

    if not args.timings_dir.is_dir():
        print(f"error: --timings-dir {args.timings_dir} is not a directory", file=sys.stderr)
        return 2

    config = json.loads(args.config.read_text(encoding="utf-8"))
    rows = audit(config, load_timings(args.timings_dir))

    if args.markdown:
        print(render_markdown(rows))
    else:
        print(json.dumps(rows, indent=2))

    flagged = [r for r in rows if r["status"] in {"low_headroom", "over_capacity"}]
    if flagged:
        for row in flagged:
            print(
                f"::warning::HONUA_SHARD_LOW_HEADROOM shard='{row['shard']}' p90="
                f"{row['p90']}m of {row['test_timeout_minutes']}m; recommended cap "
                f"{row['recommended_test_timeout_minutes']}m",
                file=sys.stderr,
            )
        if args.fail_on_warn:
            return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
