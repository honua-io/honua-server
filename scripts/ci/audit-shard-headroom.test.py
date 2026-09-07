#!/usr/bin/env python3
"""Tests for the shard headroom audit and its `--max-utilization` drain guard.

The guard added in #3204 exists because three shards hit their budget in 24
hours (#4450, #4455, and the Analytics Studio Export shard on run 34072577139).
Each time, the shard was already spending >85% of its budget on the run BEFORE
the one that went red, and nothing said so. These tests pin the two properties
that make the guard useful: it reads the LAST run (which is all a PR gate has),
and a timed-out run is scored at the budget it was killed at rather than at the
truncated duration `timeout` happened to record.
"""

from __future__ import annotations

import contextlib
import importlib.util
import io
import json
from pathlib import Path
from tempfile import TemporaryDirectory


SCRIPT = Path(__file__).with_name("audit-shard-headroom.py")
SPEC = importlib.util.spec_from_file_location("shard_headroom", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def config(*shards: tuple[str, int]) -> dict:
    return {
        "shard_budget_policy": {
            "min_job_overhead_minutes": 10,
            "target_utilization": 0.7,
            "warn_utilization": 0.8,
        },
        "shards": [
            {
                "shard_name": name,
                "test_timeout_minutes": cap,
                "timeout_minutes": cap + 10,
            }
            for name, cap in shards
        ],
    }


def record(shard: str, seconds: float, *, started_at: str, cap: int = 20, timed_out: bool = False) -> dict:
    return {
        "shard": shard,
        "duration_seconds": seconds,
        "timeout_minutes": cap,
        "timed_out": timed_out,
        "capacity_status": "capacity_exhausted" if timed_out else "ok",
        "started_at": started_at,
    }


def audit(cfg: dict, records: list[dict]) -> dict[str, dict]:
    observations: dict[str, list[dict]] = {}
    for entry in records:
        observations.setdefault(entry["shard"], []).append(entry)
    return {row["shard"]: row for row in MODULE.audit(cfg, observations)}


def run_cli(records: list[dict], cfg: dict, *args: str) -> int:
    with TemporaryDirectory() as tmp:
        root = Path(tmp)
        config_path = root / "ci-shards.json"
        config_path.write_text(json.dumps(cfg), encoding="utf-8")
        timings = root / "timings"
        timings.mkdir()
        for index, entry in enumerate(records):
            (timings / f"{index}.timing.json").write_text(json.dumps(entry), encoding="utf-8")
        # The audit prints its report to stdout and its annotations to stderr;
        # a test asserting on the exit code should not spray either into the CI log.
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            return MODULE.main(
                ["--config", str(config_path), "--timings-dir", str(timings), *args]
            )


def test_latest_utilization_reads_the_newest_run_not_the_average() -> None:
    """A shard that has just grown must be judged on the run that grew it.

    Averaging or taking p50 over a history would let a shard that jumped from
    8 to 19 minutes on its most recent run look healthy for several more runs —
    exactly long enough for the next PR to push it over the cap.
    """
    cfg = config(("Alpha", 20))
    rows = audit(
        cfg,
        [
            record("Alpha", 480, started_at="2026-09-06T01:00:00Z"),
            record("Alpha", 500, started_at="2026-09-06T02:00:00Z"),
            record("Alpha", 1140, started_at="2026-09-06T03:00:00Z"),
        ],
    )
    assert rows["Alpha"]["latest_minutes"] == 19.0
    assert rows["Alpha"]["latest_utilization"] == 0.95
    assert rows["Alpha"]["p50"] == 8.3


def test_latest_ignores_file_order_and_uses_the_timestamp() -> None:
    """Artifacts arrive in directory order, which is not chronological."""
    cfg = config(("Alpha", 20))
    rows = audit(
        cfg,
        [
            record("Alpha", 1140, started_at="2026-09-06T03:00:00Z"),
            record("Alpha", 480, started_at="2026-09-06T01:00:00Z"),
        ],
    )
    assert rows["Alpha"]["latest_minutes"] == 19.0


def test_timed_out_latest_run_is_scored_at_the_budget_it_was_killed_at() -> None:
    """`timeout` truncates the duration; the honest number is the cap.

    A shard killed at a 22m cap may record 21.9m of wall time. Reporting 21.9
    would say 99.5% — under a 100% reading and, worse, under any guard set at
    exactly 1.0. The censored floor is the cap itself.
    """
    cfg = config(("Alpha", 22))
    rows = audit(
        cfg,
        [record("Alpha", 1314, started_at="2026-09-07T02:05:00Z", cap=22, timed_out=True)],
    )
    assert rows["Alpha"]["latest_minutes"] == 22.0
    assert rows["Alpha"]["latest_utilization"] == 1.0


def test_timed_out_run_is_scored_against_its_own_recorded_cap() -> None:
    """A timeout recorded under an older, smaller cap must not be inflated.

    The record proves the shard needed at least the cap it ran under, not
    whatever is configured today.
    """
    cfg = config(("Alpha", 30))
    rows = audit(
        cfg,
        [record("Alpha", 1190, started_at="2026-09-07T02:05:00Z", cap=20, timed_out=True)],
    )
    assert rows["Alpha"]["latest_minutes"] == 20.0
    assert rows["Alpha"]["latest_utilization"] == round(20 / 30, 3)


def test_max_utilization_fails_when_the_last_run_is_over_the_line() -> None:
    cfg = config(("Alpha", 20))
    records = [record("Alpha", 1080, started_at="2026-09-06T03:00:00Z")]  # 18m = 90%
    assert run_cli(records, cfg, "--max-utilization", "0.85") == 1


def test_max_utilization_passes_a_shard_with_headroom() -> None:
    cfg = config(("Alpha", 20))
    records = [record("Alpha", 720, started_at="2026-09-06T03:00:00Z")]  # 12m = 60%
    assert run_cli(records, cfg, "--max-utilization", "0.85") == 0


def test_max_utilization_is_exclusive_at_the_boundary() -> None:
    """Exactly at the limit is not over it, so the threshold is reportable."""
    cfg = config(("Alpha", 20))
    records = [record("Alpha", 1020, started_at="2026-09-06T03:00:00Z")]  # 17m = 85%
    assert run_cli(records, cfg, "--max-utilization", "0.85") == 0


def test_shards_without_artifacts_are_skipped_not_failed() -> None:
    """A PR gate only runs the shards its diff selected.

    Treating an absent shard as 0% would be harmless but treating it as a
    failure would make the guard unusable on every targeted run, so absence
    must simply not be evidence.
    """
    cfg = config(("Alpha", 20), ("Beta", 20))
    records = [record("Alpha", 600, started_at="2026-09-06T03:00:00Z")]
    assert run_cli(records, cfg, "--max-utilization", "0.85") == 0


def test_max_utilization_is_independent_of_fail_on_warn() -> None:
    """The two flags answer different questions and must not alias.

    A shard whose p90 is over the warn line but whose LAST run has headroom
    (because the rebalance already landed) must not fail the drain guard.
    """
    cfg = config(("Alpha", 20))
    records = [
        record("Alpha", 1140, started_at="2026-09-06T01:00:00Z"),  # 19m, pre-rebalance
        record("Alpha", 1140, started_at="2026-09-06T02:00:00Z"),
        record("Alpha", 600, started_at="2026-09-06T03:00:00Z"),  # 10m, post-rebalance
    ]
    assert run_cli(records, cfg, "--max-utilization", "0.85") == 0
    assert run_cli(records, cfg, "--fail-on-warn") == 1


def test_markdown_table_reports_the_last_run_column() -> None:
    cfg = config(("Alpha", 20))
    rows = list(
        audit(cfg, [record("Alpha", 1080, started_at="2026-09-06T03:00:00Z")]).values()
    )
    table = MODULE.render_markdown(rows)
    assert "Last (min)" in table and "Last / cap" in table
    assert "| 18.0 |" in table and "90%" in table


def test_records_without_a_timestamp_never_win_the_latest_slot() -> None:
    """A stampless legacy artifact must not masquerade as the newest sample."""
    cfg = config(("Alpha", 20))
    stale = record("Alpha", 1140, started_at="2026-09-06T03:00:00Z")
    legacy = {
        "shard": "Alpha",
        "duration_seconds": 300,
        "timeout_minutes": 20,
        "timed_out": False,
        "capacity_status": "ok",
    }
    rows = audit(cfg, [legacy, stale])
    assert rows["Alpha"]["latest_minutes"] == 19.0


test_latest_utilization_reads_the_newest_run_not_the_average()
test_latest_ignores_file_order_and_uses_the_timestamp()
test_timed_out_latest_run_is_scored_at_the_budget_it_was_killed_at()
test_timed_out_run_is_scored_against_its_own_recorded_cap()
test_max_utilization_fails_when_the_last_run_is_over_the_line()
test_max_utilization_passes_a_shard_with_headroom()
test_max_utilization_is_exclusive_at_the_boundary()
test_shards_without_artifacts_are_skipped_not_failed()
test_max_utilization_is_independent_of_fail_on_warn()
test_markdown_table_reports_the_last_run_column()
test_records_without_a_timestamp_never_win_the_latest_slot()
print("shard-headroom-audit-guard=ok")
