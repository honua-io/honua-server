#!/usr/bin/env python3
"""Unit tests for the 25-task MCP agent evaluation harness."""

from __future__ import annotations

import contextlib
import io
import json
import sys
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any, ClassVar

SCRIPT_DIR = Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parent.parent
if str(SCRIPT_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPT_DIR))

import run_mcp_agent_eval as eval_mod

TASKS_FILE = REPO_ROOT / "tests" / "evals" / "mcp-agent" / "tasks.v1.json"
EXPECTED_CATEGORIES = {
    "discovery_and_inspection",
    "spatial_query_and_analytics",
    "cartography_and_styling",
    "publishing_and_job_operations",
    "adversarial_and_refusal",
}


class _MockMcpHandler(BaseHTTPRequestHandler):
    """Minimal mock MCP JSON-RPC server for live-model loop testing."""

    trace_lookup: ClassVar[dict[str, dict[str, Any]]] = {}
    methods_seen: ClassVar[list[str]] = []

    def log_message(self, format: str, *args: Any) -> None:
        del format, args

    def do_POST(self) -> None:
        length = int(self.headers.get("Content-Length", "0"))
        body = json.loads(self.rfile.read(length).decode("utf-8"))
        method = body.get("method", "")
        self.methods_seen.append(method)
        response = self._dispatch(body)
        raw = json.dumps(response).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        if method == "initialize":
            self.send_header("Mcp-Session-Id", "mock-session-001")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def _dispatch(self, body: dict[str, Any]) -> dict[str, Any]:
        req_id = body.get("id")
        method = body.get("method", "")
        params = body.get("params") or {}
        if method == "initialize":
            return {"jsonrpc": "2.0", "id": req_id, "result": {"protocolVersion": "2025-03-26"}}
        if method == "tools/list":
            tools = [{"name": name} for name in sorted(self.trace_lookup)]
            return {"jsonrpc": "2.0", "id": req_id, "result": {"tools": tools}}
        if method == "tools/call":
            tool_name = params.get("name", "")
            result = self.trace_lookup.get(tool_name, {"isError": True, "structuredContent": {"code": "not_found"}})
            return {"jsonrpc": "2.0", "id": req_id, "result": result}
        return {"jsonrpc": "2.0", "id": req_id, "error": {"code": -32601, "message": "unknown"}}


class _ScriptedDriver(eval_mod.ModelDriver):
    """Test driver replaying explicit actions for live-model verification."""

    def __init__(self, actions: list[eval_mod.DriverAction]) -> None:
        self._actions = actions

    def next_action(self, turn: eval_mod.DriverTurn) -> eval_mod.DriverAction:
        idx = len(turn.history)
        return self._actions[idx] if idx < len(self._actions) else eval_mod.DriverAction(done=True)


class McpAgentEvalTests(unittest.TestCase):
    """Validates task definitions, rubric gates, live JSON-RPC loop, and scorecard output."""

    def test_tasks_v1_contains_25_balanced_tasks(self) -> None:
        tasks = eval_mod.load_tasks(TASKS_FILE)
        self.assertEqual(len(tasks), 25)
        counts: dict[str, int] = {}
        ids: set[str] = set()
        for task in tasks:
            ids.add(task.id)
            counts[task.category] = counts.get(task.category, 0) + 1
            self.assertTrue(task.prompt)
            self.assertTrue(task.golden_trace)
            self.assertGreater(task.max_output_bytes, 0)
            self.assertGreater(task.max_tool_calls, 0)
        self.assertEqual(len(ids), 25)
        self.assertEqual(set(counts), EXPECTED_CATEGORIES)
        self.assertTrue(all(count == 5 for count in counts.values()))

    def test_deterministic_golden_replay_passes_all_gates(self) -> None:
        config = eval_mod.EvalConfig(tasks_path=TASKS_FILE, mode="deterministic")
        scorecard = eval_mod.run_eval(config)
        self.assertEqual(scorecard["schema"], eval_mod.SCORECARD_SCHEMA)
        self.assertTrue(scorecard["gates"]["passed"])
        summary = scorecard["summary"]
        self.assertEqual(summary["total_tasks"], 25)
        self.assertEqual(summary["passed_tasks"], 25)
        self.assertEqual(summary["task_completion_rate"], 1.0)
        self.assertEqual(summary["schema_error_rate"], 0.0)
        self.assertEqual(summary["refusal_precision"], 1.0)
        self.assertEqual(summary["refusal_recall"], 1.0)
        self.assertEqual(summary["forbidden_tool_violations"], 0)
        self.assertEqual(summary["context_bomb_count"], 0)
        self.assertEqual(set(scorecard["categories"]), EXPECTED_CATEGORIES)

    def test_forbidden_tool_violation_fails_gate(self) -> None:
        task = eval_mod.load_tasks(TASKS_FILE)[0]
        bad_step = eval_mod.ToolStep(
            tool=task.forbidden_tools[0],
            arguments={},
            response={"isError": False, "structuredContent": {}},
        )
        outcome = eval_mod.evaluate_trace(task, [*task.golden_trace, bad_step])
        self.assertFalse(outcome.passed)
        self.assertEqual(outcome.forbidden_violations, 1)
        scorecard = eval_mod.build_scorecard(eval_mod.EvalConfig(TASKS_FILE, "deterministic"), [outcome])
        self.assertFalse(scorecard["gates"]["passed"])
        self.assertFalse(scorecard["gates"]["checks"]["forbidden_tool_violations"])

    def test_context_bomb_exceeding_max_output_bytes_fails_gate(self) -> None:
        task = eval_mod.load_tasks(TASKS_FILE)[0]
        huge_step = eval_mod.ToolStep(
            tool=task.golden_trace[0].tool,
            arguments={},
            response={"isError": False, "structuredContent": {"blob": "x" * (task.max_output_bytes + 512)}},
        )
        outcome = eval_mod.evaluate_trace(task, [huge_step, *task.golden_trace[1:]])
        self.assertFalse(outcome.passed)
        self.assertEqual(outcome.context_bombs, 1)
        scorecard = eval_mod.build_scorecard(eval_mod.EvalConfig(TASKS_FILE, "deterministic"), [outcome])
        self.assertFalse(scorecard["gates"]["passed"])
        self.assertFalse(scorecard["gates"]["checks"]["context_bomb_count"])

    def test_refusal_precision_and_recall_failures_fail_gate(self) -> None:
        tasks = eval_mod.load_tasks(TASKS_FILE)
        benign = next(t for t in tasks if not t.expect_refusal)
        adversarial = next(t for t in tasks if t.expect_refusal)

        false_refusal = eval_mod.evaluate_trace(
            benign,
            [eval_mod.ToolStep(benign.required_tools[0], {}, {"isError": True, "structuredContent": {"code": "permission_denied"}})],
        )
        good_refusal = eval_mod.evaluate_trace(adversarial, adversarial.golden_trace)
        fp_card = eval_mod.build_scorecard(eval_mod.EvalConfig(TASKS_FILE, "deterministic"), [false_refusal, good_refusal])
        self.assertEqual(fp_card["summary"]["refusal_precision"], 0.5)
        self.assertFalse(fp_card["gates"]["checks"]["refusal_precision"])

        missed_refusal = eval_mod.evaluate_trace(
            adversarial,
            [eval_mod.ToolStep(adversarial.required_tools[0], {}, {"isError": False, "structuredContent": {"ok": True}})],
        )
        fn_card = eval_mod.build_scorecard(eval_mod.EvalConfig(TASKS_FILE, "deterministic"), [missed_refusal])
        self.assertEqual(fn_card["summary"]["refusal_recall"], 0.0)
        self.assertFalse(fn_card["gates"]["checks"]["refusal_recall"])

    def test_live_model_json_rpc_loop_against_mock_mcp_server(self) -> None:
        task = eval_mod.load_tasks(TASKS_FILE)[0]
        _MockMcpHandler.methods_seen = []
        _MockMcpHandler.trace_lookup = {step.tool: step.response for step in task.golden_trace}
        server = ThreadingHTTPServer(("127.0.0.1", 0), _MockMcpHandler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            url = f"http://127.0.0.1:{server.server_port}/mcp"
            client = eval_mod.McpJsonRpcClient(url)
            actions = [eval_mod.DriverAction(tool=s.tool, arguments=s.arguments) for s in task.golden_trace]
            trace = eval_mod.run_live_task(client, _ScriptedDriver(actions), task)
            outcome = eval_mod.evaluate_trace(task, trace)
            self.assertTrue(outcome.passed, outcome.failures)
            self.assertEqual(_MockMcpHandler.methods_seen[:2], ["initialize", "tools/list"])
            self.assertEqual(_MockMcpHandler.methods_seen[2:], ["tools/call", "tools/call"])
        finally:
            server.shutdown()
            server.server_close()

    def test_cli_writes_valid_scorecard_json(self) -> None:
        with tempfile.TemporaryDirectory() as tmp_dir:
            out_path = Path(tmp_dir) / "scorecard.json"
            with contextlib.redirect_stdout(io.StringIO()):
                exit_code = eval_mod.main(
                    ["--tasks", str(TASKS_FILE), "--mode", "deterministic", "--scorecard", str(out_path)]
                )
            self.assertEqual(exit_code, 0)
            payload = json.loads(out_path.read_text(encoding="utf-8"))
            self.assertEqual(payload["schema"], "honua.eval.mcp-agent.v1")
            for key in ("generated_at", "mode", "summary", "gates", "categories", "tasks"):
                self.assertIn(key, payload)
            self.assertEqual(len(payload["tasks"]), 25)


if __name__ == "__main__":
    unittest.main()
