"""Exercise the author-facing entrypoint with real tools and filesystem artifacts."""
from __future__ import annotations

import json
import os
import signal
import subprocess
import sys
import time
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


@pytest.mark.skipif(os.name == "nt", reason="POSIX cooperative SIGTERM contract")
def test_local_sigterm_writes_cancel_receipt(tmp_path):
    source = tool(tmp_path,
        "    import time\n"
        "    (context.workdir / 'ready').write_text('ready')\n"
        "    try:\n"
        "        while True:\n"
        "            context.cancellation.raise_if_cancelled()\n"
        "            time.sleep(0.01)\n"
        "    finally:\n"
        "        (context.workdir / 'cleanup').write_text('cleaned')\n")
    output = tmp_path / "runs"
    process = subprocess.Popen([sys.executable, "-c",
        "from honua_customcode_harness.local import main; raise SystemExit(main())",
        "--source", str(source), "--entrypoint", "local_tool:execute", "--output", str(output)],
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
        env={**os.environ, "PYTHONPATH": str(Path(__file__).resolve().parents[1])})
    try:
        deadline = time.monotonic() + 15
        while not list(output.glob("*/work/ready")):
            assert process.poll() is None
            assert time.monotonic() < deadline
            time.sleep(0.01)
        process.send_signal(signal.SIGTERM)
        stdout, stderr = process.communicate(timeout=15)
        assert process.returncode == 3, (stdout, stderr)
        result = receipt(output)
        assert result["status"] == "cancelled"
        assert result["failedPhase"] == "execute"
        assert result["artifacts"] == []
        assert (output / result["runId"] / "work" / "cleanup").read_text() == "cleaned"
    finally:
        if process.poll() is None:
            process.kill()
            process.communicate()


def test_local_restores_signal_handlers(tmp_path):
    previous = {signum: signal.getsignal(signum) for signum in (signal.SIGTERM, signal.SIGINT)}
    source = tool(tmp_path, "    return GpResult.succeeded()\n")
    assert run_local(source=source, entrypoint="local_tool:execute", params={}, inputs={}, output=tmp_path / "runs") == 0
    assert {signum: signal.getsignal(signum) for signum in previous} == previous
