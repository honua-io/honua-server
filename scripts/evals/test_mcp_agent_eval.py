#!/usr/bin/env python3
"""Unit tests for the 25-task MCP agent evaluation harness."""

from __future__ import annotations

import contextlib
import io
import json
import os
import re
import sys
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any, ClassVar
from unittest import mock

SCRIPT_DIR = Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parent.parent
if str(SCRIPT_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPT_DIR))

import run_mcp_agent_eval as eval_mod

TASKS_FILE = REPO_ROOT / "tests" / "evals" / "mcp-agent" / "tasks.v1.json"
ROSTER_FILE = REPO_ROOT / "docs" / "gis" / "data" / "mcp-tool-roster.v1.json"
STUDIO_SCHEMAS_CS = REPO_ROOT / "src" / "Honua.Ai" / "Features" / "Protocols" / "Mcp" / "Mcp" / "Studio" / "StudioMcpSchemas.cs"
UUID_RE = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")
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
    tool_pages: ClassVar[dict[str, tuple[list[dict[str, Any]], str | None]]] = {}
    methods_seen: ClassVar[list[str]] = []
    headers_seen: ClassVar[list[dict[str, str]]] = []

    def log_message(self, format: str, *args: Any) -> None:
        del format, args

    def do_POST(self) -> None:
        length = int(self.headers.get("Content-Length", "0"))
        body = json.loads(self.rfile.read(length).decode("utf-8"))
        method = body.get("method", "")
        self.methods_seen.append(method)
        self.headers_seen.append({k.lower(): v for k, v in self.headers.items()})
        raw = json.dumps(self._dispatch(body)).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        if method == "initialize":
            self.send_header("Mcp-Session-Id", "mock-session-001")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def _dispatch(self, body: dict[str, Any]) -> dict[str, Any]:
        req_id, method, params = body.get("id"), body.get("method", ""), body.get("params") or {}
        if method == "initialize":
            return {"jsonrpc": "2.0", "id": req_id, "result": {"protocolVersion": "2025-03-26"}}
        if method == "tools/list":
            return self._tools_page(req_id, params.get("cursor"))
        if method == "tools/call":
            result = self.trace_lookup.get(params.get("name", ""), {"isError": True, "structuredContent": {"code": "not_found"}})
            return {"jsonrpc": "2.0", "id": req_id, "result": result}
        return {"jsonrpc": "2.0", "id": req_id, "error": {"code": -32601, "message": "unknown"}}

    def _tools_page(self, req_id: Any, cursor: str | None) -> dict[str, Any]:
        if self.tool_pages:
            tools, next_cursor = self.tool_pages.get(cursor or "", ([], None))
            payload: dict[str, Any] = {"tools": tools}
            if next_cursor is not None:
                payload["nextCursor"] = next_cursor
            return {"jsonrpc": "2.0", "id": req_id, "result": payload}
        tools = [{"name": name} for name in sorted(self.trace_lookup)]
        return {"jsonrpc": "2.0", "id": req_id, "result": {"tools": tools}}


class _ScriptedDriver(eval_mod.ModelDriver):
    """Test driver replaying explicit actions for live-model verification."""

    def __init__(self, actions: list[eval_mod.DriverAction]) -> None:
        self._actions = actions
        self.seen_tools: list[str] = []

    def next_action(self, turn: eval_mod.DriverTurn) -> eval_mod.DriverAction:
        self.seen_tools = [t["name"] for t in turn.available_tools]
        idx = len(turn.history)
        return self._actions[idx] if idx < len(self._actions) else eval_mod.DriverAction(done=True)


def _start_mock_server() -> tuple[ThreadingHTTPServer, str]:
    server = ThreadingHTTPServer(("127.0.0.1", 0), _MockMcpHandler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server, f"http://127.0.0.1:{server.server_port}/mcp"


def _studio_tool_schemas() -> dict[str, dict[str, Any]]:
    text = STUDIO_SCHEMAS_CS.read_text(encoding="utf-8")
    specs = {
        "honua_studio_create_draft": ("CreateDraftArgumentSchemaJson", ["packageKey", "family", "schemaVersion"]),
        "honua_studio_get_draft": ("DraftIdArgumentSchemaJson", ["draftId"]),
        "honua_studio_validate_draft": ("DraftIdArgumentSchemaJson", ["draftId"]),
        "honua_studio_preview_draft": ("DraftIdArgumentSchemaJson", ["draftId"]),
        "honua_studio_add_layer": ("AddLayerArgumentSchemaJson", ["draftId", "generation", "layer"]),
        "honua_studio_set_layer_style": ("SetLayerStyleArgumentSchemaJson", ["draftId", "generation", "layerId"]),
        "honua_studio_set_view": ("SetViewArgumentSchemaJson", ["draftId", "generation", "view"]),
        "honua_studio_add_widget": ("AddWidgetArgumentSchemaJson", ["draftId", "generation", "widget"]),
        "honua_studio_save_version": ("SaveVersionArgumentSchema", ["draftId", "generation"]),
        "honua_studio_list_versions": ("ListVersionsArgumentSchema", ["itemId"]),
    }
    parsed: dict[str, dict[str, Any]] = {}
    for tool_name, (symbol, expected_req) in specs.items():
        match = re.search(rf"{symbol}\s*=\s*(?:Parse\()?\${{0,2}}\"\"\"(.*?)\"\"\"", text, re.DOTALL)
        assert match is not None, f"Could not locate {symbol} in StudioMcpSchemas.cs"
        block = match.group(1)
        req_match = re.search(r'"required"\s*:\s*\[([^\]]*)\]', block)
        assert req_match is not None
        req = re.findall(r'"([^"]+)"', req_match.group(1))
        assert req == expected_req, f"{tool_name} required drift: {req} != {expected_req}"
        props_match = re.search(r'"properties"\s*:\s*\{(.*)', block, re.DOTALL)
        assert props_match is not None
        allowed = set(re.findall(r'"([A-Za-z][A-Za-z0-9_]*)"\s*:\s*(?:\{|\{\{)', props_match.group(1)))
        parsed[tool_name] = {"required": req, "allowed": allowed, "uuid_fields": [k for k in ("draftId", "itemId") if k in allowed]}
    return parsed


class McpAgentEvalTests(unittest.TestCase):
    """Validates task definitions, rubric gates, live JSON-RPC loop, and scorecard output."""

    def setUp(self) -> None:
        _MockMcpHandler.trace_lookup = {}
        _MockMcpHandler.tool_pages = {}
        _MockMcpHandler.methods_seen = []
        _MockMcpHandler.headers_seen = []

    def test_tasks_v1_contains_25_balanced_tasks(self) -> None:
        tasks = eval_mod.load_tasks(TASKS_FILE)
        self.assertEqual(len(tasks), 25)
        counts: dict[str, int] = {}
        for task in tasks:
            counts[task.category] = counts.get(task.category, 0) + 1
            self.assertTrue(task.prompt and task.golden_trace)
            self.assertGreater(task.max_output_bytes, 0)
            self.assertGreater(task.max_tool_calls, 0)
        self.assertEqual(len({t.id for t in tasks}), 25)
        self.assertEqual(set(counts), EXPECTED_CATEGORIES)
        self.assertTrue(all(count == 5 for count in counts.values()))

    def test_golden_tools_and_studio_arguments_match_roster_and_schemas(self) -> None:
        roster = json.loads(ROSTER_FILE.read_text(encoding="utf-8"))
        known_tools = set(roster["static"]) | set(roster["projectedAdmin"])
        studio_schemas = _studio_tool_schemas()
        for task in eval_mod.load_tasks(TASKS_FILE):
            for tool_name in [*task.required_tools, *task.forbidden_tools, *(s.tool for s in task.golden_trace)]:
                self.assertIn(tool_name, known_tools, f"{task.id} references unknown tool {tool_name}")
            for step in task.golden_trace:
                if step.tool in studio_schemas:
                    self._assert_step_matches_schema(task.id, step, studio_schemas[step.tool])

    def _assert_step_matches_schema(self, task_id: str, step: eval_mod.ToolStep, schema: dict[str, Any]) -> None:
        for req in schema["required"]:
            self.assertIn(req, step.arguments, f"{task_id} {step.tool} missing {req}")
        self.assertTrue(set(step.arguments) <= schema["allowed"], f"{task_id} {step.tool} unknown args: {step.arguments}")
        for uuid_field in schema["uuid_fields"]:
            if uuid_field in step.arguments:
                self.assertRegex(step.arguments[uuid_field], UUID_RE)

    def test_deterministic_golden_replay_passes_all_gates(self) -> None:
        scorecard = eval_mod.run_eval(eval_mod.EvalConfig(tasks_path=TASKS_FILE, mode="deterministic"))
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

    def test_forbidden_tool_violation_fails_gate(self) -> None:
        task = eval_mod.load_tasks(TASKS_FILE)[0]
        bad_step = eval_mod.ToolStep(task.forbidden_tools[0], {}, {"isError": False, "structuredContent": {}})
        outcome = eval_mod.evaluate_trace(task, [*task.golden_trace, bad_step])
        self.assertFalse(outcome.passed)
        self.assertEqual(outcome.forbidden_violations, 1)
        card = eval_mod.build_scorecard(eval_mod.EvalConfig(TASKS_FILE, "deterministic"), [outcome])
        self.assertFalse(card["gates"]["passed"])
        self.assertFalse(card["gates"]["checks"]["forbidden_tool_violations"])

    def test_context_bomb_exceeding_max_output_bytes_fails_gate(self) -> None:
        task = eval_mod.load_tasks(TASKS_FILE)[0]
        huge = eval_mod.ToolStep(task.golden_trace[0].tool, {}, {"isError": False, "structuredContent": {"blob": "x" * (task.max_output_bytes + 512)}})
        outcome = eval_mod.evaluate_trace(task, [huge, *task.golden_trace[1:]])
        self.assertFalse(outcome.passed)
        self.assertEqual(outcome.context_bombs, 1)
        card = eval_mod.build_scorecard(eval_mod.EvalConfig(TASKS_FILE, "deterministic"), [outcome])
        self.assertFalse(card["gates"]["checks"]["context_bomb_count"])

    def test_refusal_precision_and_recall_failures_fail_gate(self) -> None:
        tasks = eval_mod.load_tasks(TASKS_FILE)
        benign = next(t for t in tasks if not t.expect_refusal)
        adversarial = next(t for t in tasks if t.expect_refusal)
        fp = eval_mod.evaluate_trace(benign, [eval_mod.ToolStep(benign.required_tools[0], {}, {"isError": True, "structuredContent": {"code": "permission_denied"}})])
        tp = eval_mod.evaluate_trace(adversarial, adversarial.golden_trace)
        self.assertEqual(eval_mod.build_scorecard(eval_mod.EvalConfig(TASKS_FILE, "deterministic"), [fp, tp])["summary"]["refusal_precision"], 0.5)
        fn = eval_mod.evaluate_trace(adversarial, [eval_mod.ToolStep(adversarial.required_tools[0], {}, {"isError": False, "structuredContent": {"ok": True}})])
        self.assertEqual(eval_mod.build_scorecard(eval_mod.EvalConfig(TASKS_FILE, "deterministic"), [fn])["summary"]["refusal_recall"], 0.0)

    def test_live_model_json_rpc_loop_forwards_credentials_and_follows_pagination(self) -> None:
        task = eval_mod.load_tasks(TASKS_FILE)[0]
        _MockMcpHandler.trace_lookup = {step.tool: step.response for step in task.golden_trace}
        _MockMcpHandler.tool_pages = {
            "": ([{"name": "honua_list_capabilities"}], "page-2"),
            "page-2": ([{"name": "honua_list_layers"}], None),
        }
        server, url = _start_mock_server()
        try:
            auth = eval_mod.AuthCredentials(api_key="test-api-key", bearer_token="test-jwt-token")
            client = eval_mod.LiveMcpClient(url, auth=auth)
            driver = _ScriptedDriver([eval_mod.DriverAction(tool=s.tool, arguments=s.arguments) for s in task.golden_trace])
            result = eval_mod.run_live_task(client, driver, task)
            self.assertTrue(eval_mod.evaluate_trace(task, result).passed)
            self.assertEqual(driver.seen_tools, ["honua_list_capabilities", "honua_list_layers"])
            self.assertEqual(_MockMcpHandler.methods_seen, ["initialize", "tools/list", "tools/list", "tools/call", "tools/call"])
            for headers in _MockMcpHandler.headers_seen:
                self.assertEqual(headers.get("x-api-key"), "test-api-key")
                self.assertEqual(headers.get("authorization"), "Bearer test-jwt-token")
        finally:
            server.shutdown()
            server.server_close()

    def test_pre_tool_call_refusal_passes_adversarial_task(self) -> None:
        adversarial = next(t for t in eval_mod.load_tasks(TASKS_FILE) if t.expect_refusal)
        server, url = _start_mock_server()
        try:
            client = eval_mod.LiveMcpClient(url)
            driver = _ScriptedDriver([eval_mod.DriverAction(done=True, refused=True)])
            result = eval_mod.run_live_task(client, driver, adversarial)
            self.assertEqual(result.steps, [])
            self.assertTrue(result.agent_refused)
            outcome = eval_mod.evaluate_trace(adversarial, result)
            self.assertTrue(outcome.passed, outcome.failures)
            self.assertTrue(outcome.refused)
        finally:
            server.shutdown()
            server.server_close()

    def test_parse_args_falls_back_to_env_credentials(self) -> None:
        with mock.patch.dict(os.environ, {"HONUA_API_KEY": "env-key", "HONUA_BEARER_TOKEN": "env-token"}, clear=False):
            cfg = eval_mod.parse_args(["--mode", "deterministic"])
        self.assertEqual(cfg.auth.api_key, "env-key")
        self.assertEqual(cfg.auth.bearer_token, "env-token")

    def test_cli_writes_valid_scorecard_json(self) -> None:
        with tempfile.TemporaryDirectory() as tmp_dir:
            out_path = Path(tmp_dir) / "scorecard.json"
            with contextlib.redirect_stdout(io.StringIO()):
                exit_code = eval_mod.main(["--tasks", str(TASKS_FILE), "--mode", "deterministic", "--scorecard", str(out_path)])
            self.assertEqual(exit_code, 0)
            payload = json.loads(out_path.read_text(encoding="utf-8"))
            self.assertEqual(payload["schema"], "honua.eval.mcp-agent.v1")
            for key in ("generated_at", "mode", "summary", "gates", "categories", "tasks"):
                self.assertIn(key, payload)
            self.assertEqual(len(payload["tasks"]), 25)


if __name__ == "__main__":
    unittest.main()
