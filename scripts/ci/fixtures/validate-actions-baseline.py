#!/usr/bin/env python3
"""Offline contract checks for measure-actions-baseline.py."""

from __future__ import annotations

import gzip
import importlib.util
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
    require(workflow["runner_minutes"] == 30.67, "active-run/all-attempt runner sum drifted")
    require(workflow["estimated_rounded_linux_minutes"] == 32, "rounded Linux estimate drifted")
    require(workflow["cancelled_runner_minutes"] == 3.67, "cancelled consumed time drifted")
    cancelled = next(run for run in workflow["runs"] if run["conclusion"] == "cancelled")
    require(cancelled["jobs"]["missing_timestamps"] == 1, "missing timestamp was not surfaced")
    require(cancelled["jobs"]["skipped"] == 0, "cancelled job was misclassified as skipped")

    markdown_run = run("--format", "markdown")
    require(markdown_run.returncode == 0, markdown_run.stderr)
    require("| PR Gate | 4 | 1 | 1 | 1 |" in markdown_run.stdout, "Markdown summary counts drifted")
    require("not GitHub invoice data" in markdown_run.stdout, "estimate disclaimer is missing")

    spec = importlib.util.spec_from_file_location("measure_actions_baseline", COLLECTOR)
    require(spec is not None and spec.loader is not None, "collector module could not be loaded")
    collector = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(collector)

    page_calls: list[str] = []

    def full_page(endpoint: str) -> dict:
        page_calls.append(endpoint)
        return {"workflow_runs": [{"id": index} for index in range(100)]}

    bounded_pages = collector.bounded_workflow_run_pages(
        "repos/honua-io/honua-server/actions/workflows/ci.yml/runs?per_page=100",
        30,
        fetch_page=full_page,
    )
    require(len(bounded_pages) == 1, "run pagination fetched beyond the requested limit")
    require(len(page_calls) == 1 and page_calls[0].endswith("&page=1"), "bounded page request drifted")

    terminal_failure_markdown = collector.render_markdown({
        "repository": "honua-io/honua-server",
        "created_after": None,
        "sample_limit_per_workflow": 2,
        "workflows": [{
            "name": "Terminal failures",
            "sampled_runs": 2,
            "counts": {"action_required": 1, "startup_failure": 1},
            "queue_seconds": {"p90": None},
            "successful_critical_path_seconds": {"p90": None},
            "time_to_first_failure_seconds": {"p50": None},
            "runner_minutes": 0.0,
            "estimated_rounded_linux_minutes": 0,
            "cancelled_runner_minutes": 0.0,
        }],
    })
    require(
        "| Terminal failures | 2 | 0 | 2 | 0 |" in terminal_failure_markdown,
        "Markdown omitted recognized terminal failure conclusions",
    )

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

        gzip_path = Path(directory) / "input.json.gz"
        gzip_repeat_path = Path(directory) / "input-repeat.json.gz"
        gzip_run = run("--input-out", str(gzip_path))
        gzip_repeat_run = run("--input-out", str(gzip_repeat_path))
        require(gzip_run.returncode == 0, gzip_run.stderr)
        require(gzip_repeat_run.returncode == 0, gzip_repeat_run.stderr)
        require(gzip_path.read_bytes() == gzip_repeat_path.read_bytes(), "gzip input is not deterministic")
        with gzip.open(gzip_path, "rt", encoding="utf-8") as handle:
            require(json.load(handle)["schema"] == "honua.actions-baseline.input/v1", "gzip input was not preserved")
        compressed_fixture_run = subprocess.run(
            [
                sys.executable,
                str(COLLECTOR),
                "--fixture",
                str(gzip_path),
                "--generated-at",
                GENERATED_AT,
            ],
            check=False,
            capture_output=True,
            text=True,
            encoding="utf-8",
        )
        require(compressed_fixture_run.returncode == 0, compressed_fixture_run.stderr)
        require(json.loads(compressed_fixture_run.stdout) == report, "gzip fixture changed the report")

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
