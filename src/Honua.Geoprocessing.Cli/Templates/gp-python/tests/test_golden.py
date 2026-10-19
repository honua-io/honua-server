"""Golden-result tests for the summary process, run through the real local harness.

Each test runs ``honua-gp-python`` as a subprocess exactly as a developer does,
then asserts on the receipt the harness wrote. Failure messages name the first
differing value and the receipt path so a regression is actionable from the
test output alone.
"""
from __future__ import annotations

import hashlib
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

import pytest

TEMPLATE = Path(__file__).resolve().parents[1]
FIXTURES = TEMPLATE / "fixtures"
EXPECTED = TEMPLATE / "expected"
HARNESS = os.environ.get("HONUA_GP_PYTHON_HARNESS") or shutil.which("honua-gp-python")

pytestmark = pytest.mark.skipif(
    HARNESS is None,
    reason="honua-gp-python is not installed; see README.md (install the harness or set HONUA_GP_PYTHON_HARNESS)",
)


def run_harness(output: Path, *, features: Path, max_output_bytes: int | None = None) -> dict:
    """Run the process once and return its receipt."""
    args = [
        HARNESS,
        "--source", str(TEMPLATE / "process"),
        "--entrypoint", "summary_tool:execute",
        "--params", str(FIXTURES / "params.json"),
        "--input", f"features={features}",
        "--output", str(output),
    ]
    if max_output_bytes is not None:
        args += ["--max-output-bytes", str(max_output_bytes)]
    completed = subprocess.run(args, capture_output=True, text=True, check=False)
    summary = json.loads(completed.stdout.strip().splitlines()[-1])
    receipt_path = Path(summary["receipt"])
    receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
    receipt["_receiptPath"] = str(receipt_path)
    receipt["_exitCode"] = completed.returncode
    assert receipt["exitCode"] == completed.returncode, f"receipt exit code disagrees with the process: {receipt_path}"
    return receipt


def first_difference(expected: dict, actual: dict) -> str | None:
    for key in sorted(set(expected) | set(actual)):
        if expected.get(key) != actual.get(key):
            return f"{key}: expected {expected.get(key)!r}, got {actual.get(key)!r}"
    return None


def test_summary_matches_golden(tmp_path: Path) -> None:
    golden = json.loads((EXPECTED / "golden.json").read_text(encoding="utf-8"))
    receipt = run_harness(tmp_path, features=FIXTURES / "input.geojson")
    assert receipt["status"] == "succeeded", f"{receipt['status']} in phase {receipt.get('failedPhase')}; receipt: {receipt['_receiptPath']}"
    assert [a["name"] for a in receipt["artifacts"]] == [golden["artifact"]], f"artifacts {receipt['artifacts']}; receipt: {receipt['_receiptPath']}"
    artifact = Path(receipt["_receiptPath"]).parent / receipt["artifacts"][0]["path"]
    produced = json.loads(artifact.read_text(encoding="utf-8"))
    expected = json.loads((EXPECTED / golden["artifact"]).read_text(encoding="utf-8"))
    difference = first_difference(expected, produced)
    assert difference is None, f"first difference {difference}; artifact: {artifact}; receipt: {receipt['_receiptPath']}"
    digest = hashlib.sha256(artifact.read_bytes()).hexdigest()
    assert digest == golden["sha256"], f"artifact bytes differ: sha256 {digest} != {golden['sha256']}; artifact: {artifact}"
    assert receipt["artifacts"][0]["sha256"] == golden["sha256"]
    assert receipt["eventsTruncated"] is False


def test_invalid_input_fails_in_execute_with_receipt(tmp_path: Path) -> None:
    receipt = run_harness(tmp_path, features=FIXTURES / "invalid-input.geojson")
    assert receipt["_exitCode"] == 1, f"exit {receipt['_exitCode']}; receipt: {receipt['_receiptPath']}"
    assert receipt["status"] == "failed"
    assert receipt["failedPhase"] == "execute"
    assert receipt["error"]["message"].splitlines()[0] == "Expected a GeoJSON FeatureCollection."
    assert receipt["artifacts"] == [], "a failed run must not advertise artifacts"


def test_output_cap_fails_when_the_artifact_is_registered(tmp_path: Path) -> None:
    receipt = run_harness(tmp_path, features=FIXTURES / "input.geojson", max_output_bytes=1)
    assert receipt["_exitCode"] == 1, f"exit {receipt['_exitCode']}; receipt: {receipt['_receiptPath']}"
    assert receipt["status"] == "failed"
    # The output sink refuses the artifact at registration, so the failure is in the execute phase.
    assert receipt["failedPhase"] == "execute"
    assert receipt["error"]["type"] == "OutputSizeExceeded"
    assert receipt["error"]["message"].splitlines()[0].startswith("Adding artifact 'summary.json'")
    assert receipt["artifacts"] == []


def test_missing_input_is_a_setup_failure(tmp_path: Path) -> None:
    receipt = run_harness(tmp_path, features=FIXTURES / "does-not-exist.geojson")
    assert receipt["_exitCode"] == 2
    assert receipt["status"] == "setupFailed"
    assert receipt["failedPhase"] == "setup"


if __name__ == "__main__":
    sys.exit(pytest.main([__file__, "-q"]))
