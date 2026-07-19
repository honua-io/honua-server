from __future__ import annotations

import importlib.util
import json
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
SCRIPT = ROOT / "scripts" / "conformance" / "cite" / "parse_wps20_results.py"
FIXTURES = ROOT / "tests" / "fixtures" / "cite" / "wps20"
SPEC = importlib.util.spec_from_file_location("parse_wps20_results", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
PARSER = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = PARSER
SPEC.loader.exec_module(PARSER)


def run_parser(tmp_path: Path, fixture: str, profile: str) -> tuple[int, dict[str, object]]:
    summary = tmp_path / "summary.md"
    output_json = tmp_path / "summary.json"
    exit_code = PARSER.main(
        [
            "--input",
            str(FIXTURES / fixture),
            "--profile",
            profile,
            "--summary",
            str(summary),
            "--json",
            str(output_json),
            "--ets-exit-code",
            "1",
        ]
    )
    return exit_code, json.loads(output_json.read_text(encoding="utf-8"))


def test_basic_async_ignores_unselected_sync_failure_but_preserves_raw_totals(tmp_path: Path) -> None:
    exit_code, result = run_parser(tmp_path, "all-classes.xml", "basic-async")

    assert exit_code == 0
    assert result["status"] == "passed"
    assert result["selectedTotals"] == {
        "total": 4,
        "passed": 4,
        "failed": 0,
        "skipped": 0,
        "canttell": 0,
    }
    assert result["rawTotals"]["failed"] == 1
    assert result["etsExitCode"] == 1


def test_all_profile_fails_when_any_class_fails(tmp_path: Path) -> None:
    exit_code, result = run_parser(tmp_path, "all-classes.xml", "all")

    assert exit_code == 1
    assert result["selectedTotals"]["failed"] == 1


def test_selected_skip_is_a_failure(tmp_path: Path) -> None:
    exit_code, result = run_parser(tmp_path, "selected-skip.xml", "basic-async")

    assert exit_code == 1
    assert result["selectedTotals"]["skipped"] == 1
