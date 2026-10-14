"""Exercise the author-facing entrypoint with real tools and filesystem artifacts."""
from __future__ import annotations

import json
from pathlib import Path

import pytest

from honua_customcode_harness.local import run_local


def tool(tmp_path: Path, body: str) -> Path:
    source = tmp_path / "src"
    source.mkdir()
    (source / "local_tool.py").write_text(
        "from honua_customcode_harness import GpResult\n"
        "def execute(context):\n" + body, encoding="utf-8"
    )
    return source


def receipt(output: Path) -> dict:
    files = list(output.glob("*/receipt.json"))
    assert len(files) == 1
    return json.loads(files[0].read_text())


def test_local_executes_real_contract_without_cloud(tmp_path):
    source = tool(tmp_path,
        "    assert context.client is None\n"
        "    context.progress.report(50, 'transform')\n"
        "    p = context.workdir / 'result.txt'\n"
        "    p.write_text(context.inputs['data'].read_text().upper())\n"
        "    context.output.add_artifact('answer.txt', p)\n"
        "    return GpResult.succeeded('done')\n")
    data = tmp_path / "input.txt"
    data.write_text("hello")
    output = tmp_path / "runs"
    assert run_local(source=source, entrypoint="local_tool:execute", params={},
                     inputs={"data": data}, output=output) == 0
    result = receipt(output)
    assert result["status"] == "succeeded"
    artifact = result["artifacts"][0]
    assert artifact["name"] == "answer.txt"
    assert (output / result["runId"] / artifact["path"]).read_text() == "HELLO"
    assert len(artifact["sha256"]) == 64
    events = [json.loads(line) for line in (output / result["runId"] / "events.jsonl").read_text().splitlines()]
    assert any(e.get("percentComplete") == 50 and e["phase"] == "transform" for e in events)
    assert events[-1]["phase"] == "succeeded"


@pytest.mark.parametrize("body,status,code", [
    ("    raise RuntimeError('broken input')\n", "failed", 1),
    ("    return GpResult.failed('bad data')\n", "failed", 1),
    ("    context.cancellation.cancel()\n    context.cancellation.raise_if_cancelled()\n", "cancelled", 3),
    ("    context.cancellation.cancel()\n    return GpResult.succeeded()\n", "cancelled", 3),
    ("    return 42\n", "failed", 1),
])
def test_local_terminal_evidence(tmp_path, body, status, code):
    source = tool(tmp_path, body)
    output = tmp_path / "runs"
    assert run_local(source=source, entrypoint="local_tool:execute", params={}, inputs={}, output=output) == code
    assert receipt(output)["status"] == status


def test_local_rechecks_artifacts_after_tool_mutates_file(tmp_path):
    source = tool(tmp_path,
        "    p = context.workdir / 'out.txt'\n"
        "    p.write_text('x')\n"
        "    context.output.add_artifact('out.txt', p)\n"
        "    p.write_text('x' * 100)\n"
        "    return GpResult.succeeded()\n")
    output = tmp_path / "runs"
    assert run_local(source=source, entrypoint="local_tool:execute", params={}, inputs={}, output=output,
                     max_output_bytes=10) == 1
    assert receipt(output)["artifacts"] == []


def test_local_missing_input_is_setup_failure_with_receipt(tmp_path):
    source = tool(tmp_path, "    return GpResult.succeeded()\n")
    output = tmp_path / "runs"
    assert run_local(source=source, entrypoint="local_tool:execute", params={},
                     inputs={"missing": tmp_path / "absent"}, output=output) == 2
    assert receipt(output)["status"] == "setupFailed"


def test_local_each_run_uses_fresh_directory(tmp_path):
    source = tool(tmp_path, "    return GpResult.succeeded()\n")
    output = tmp_path / "runs"
    for _ in range(2):
        assert run_local(source=source, entrypoint="local_tool:execute", params={}, inputs={}, output=output) == 0
    assert len(list(output.glob("*/receipt.json"))) == 2


def test_local_bounded_events_disclose_truncation(tmp_path):
    source = tool(tmp_path,
        "    for i in range(1100):\n"
        "        context.progress.report(50, 'working')\n"
        "    return GpResult.succeeded()\n")
    output = tmp_path / "runs"
    assert run_local(source=source, entrypoint="local_tool:execute", params={}, inputs={}, output=output) == 0
    assert receipt(output)["eventsTruncated"] is True


def test_documented_sample_has_expected_numeric_result(tmp_path):
    sample = Path(__file__).resolve().parents[4] / "samples" / "gp-custom-local"
    output = tmp_path / "runs"
    assert run_local(source=sample / "python", entrypoint="summary_tool:execute",
                     params=json.loads((sample / "params.json").read_text()),
                     inputs={"features": sample / "input.geojson"}, output=output) == 0
    result = receipt(output)
    summary = json.loads((output / result["runId"] / "artifacts" / "summary.json").read_text())
    assert summary == {"count": 2, "sum": 30, "mean": 15}


def test_local_concurrent_callbacks_keep_sequence_and_cap(tmp_path):
    source = tool(tmp_path,
        "    from concurrent.futures import ThreadPoolExecutor\n"
        "    def emit(i):\n"
        "        context.progress.report(50, 'working')\n"
        "        context.log.info(str(i))\n"
        "    with ThreadPoolExecutor(max_workers=16) as pool:\n"
        "        list(pool.map(emit, range(1000)))\n"
        "    return GpResult.succeeded()\n")
    output = tmp_path / "runs"
    assert run_local(source=source, entrypoint="local_tool:execute", params={}, inputs={}, output=output) == 0
    result = receipt(output)
    events = [json.loads(line) for line in (output / result["runId"] / "events.jsonl").read_text().splitlines()]
    assert result["eventsTruncated"] is True
    assert len(events) == 1001
    assert [event["sequence"] for event in events] == list(range(1001))
    assert events[-1]["phase"] == "succeeded"
