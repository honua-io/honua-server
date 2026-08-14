#!/usr/bin/env python3
"""Offline contract checks for measure-actions-baseline.py."""

from __future__ import annotations

import json
import subprocess
import sys
import tempfile
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parents[2]
COLLECTOR = REPO_ROOT / "scripts" / "ci" / "measure-actions-baseline.py"
FIXTURE = SCRIPT_DIR / "actions-baseline.sample.json"
GENERATED_AT = "2026-08-13T00:00:00Z"


def run(*extra: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [
            sys.executable,
            str(COLLECTOR),
            "--fixture",
            str(FIXTURE),
            "--generated-at",
            GENERATED_AT,
            *extra,
        ],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def main() -> int:
    json_run = run()
    require(json_run.returncode == 0, json_run.stderr)
    report = json.loads(json_run.stdout)
    require(report["schema"] == "honua.actions-baseline/v1", "wrong output schema")
    require(report["generated_at"] == GENERATED_AT, "generated timestamp is not deterministic")

    workflow = report["workflows"][0]
    require(workflow["sampled_runs"] == 4, "in-progress run was lost from sampled count")
    require(workflow["completed_runs"] == 3, "completed-run count is wrong")
    require(workflow["counts"] == {"cancelled": 1, "failure": 1, "in_progress": 1, "success": 1}, "conclusion counts drifted")
    require(workflow["queue_seconds"] == {"p50": 90.0, "p90": 150.0}, "nearest-rank queue percentiles drifted")
    require(workflow["critical_path_seconds"] == {"p50": 540.0, "p90": 1020.0}, "critical-path percentiles drifted")
    require(workflow["successful_critical_path_seconds"] == {"p50": 540.0, "p90": 540.0}, "successful critical path drifted")
    require(workflow["time_to_first_failure_seconds"] == {"p50": 720.0, "p90": 720.0}, "first failure timing drifted")
    require(workflow["runner_minutes"] == 28.67, "all-attempt runner sum drifted")
    require(workflow["estimated_rounded_linux_minutes"] == 30, "rounded Linux estimate drifted")
    require(workflow["cancelled_runner_minutes"] == 3.67, "cancelled consumed time drifted")
    cancelled = next(run for run in workflow["runs"] if run["conclusion"] == "cancelled")
    require(cancelled["jobs"]["missing_timestamps"] == 1, "missing timestamp was not surfaced")
    require(cancelled["jobs"]["skipped"] == 0, "cancelled job was misclassified as skipped")

    markdown_run = run("--format", "markdown")
    require(markdown_run.returncode == 0, markdown_run.stderr)
    require("| PR Gate | 4 | 1 | 1 | 1 |" in markdown_run.stdout, "Markdown summary counts drifted")
    require("not GitHub invoice data" in markdown_run.stdout, "estimate disclaimer is missing")

    with tempfile.TemporaryDirectory() as directory:
        json_path = Path(directory) / "report.json"
        markdown_path = Path(directory) / "report.md"
        input_path = Path(directory) / "input.json"
        output_run = run(
            "--input-out",
            str(input_path),
            "--json-out",
            str(json_path),
            "--markdown-out",
            str(markdown_path),
        )
        require(output_run.returncode == 0, output_run.stderr)
        require(json.loads(json_path.read_text(encoding="utf-8")) == report, "written JSON differs from stdout")
        require(markdown_path.read_text(encoding="utf-8") == markdown_run.stdout, "written Markdown differs from stdout")
        require(json.loads(input_path.read_text(encoding="utf-8"))["schema"] == "honua.actions-baseline.input/v1", "raw input was not preserved")

    bad_fixture = json.loads(FIXTURE.read_text(encoding="utf-8"))
    bad_fixture["schema"] = "unsafe/unknown"
    with tempfile.TemporaryDirectory() as directory:
        bad_path = Path(directory) / "bad.json"
        bad_path.write_text(json.dumps(bad_fixture), encoding="utf-8")
        process = subprocess.run(
            [sys.executable, str(COLLECTOR), "--fixture", str(bad_path)],
            check=False,
            capture_output=True,
            text=True,
            encoding="utf-8",
        )
    require(process.returncode == 2, "unknown fixture schema did not fail closed")
    require("input schema must be" in process.stderr, "schema rejection was not explicit")

    print("actions baseline fixtures passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
