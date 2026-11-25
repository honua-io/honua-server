#!/usr/bin/env python3
"""25-task MCP geospatial agent evaluation CLI and live JSON-RPC runner."""

from __future__ import annotations

import argparse
import json
import sys
import urllib.error
import urllib.request
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any

SCRIPT_DIR = str(Path(__file__).resolve().parent)
if SCRIPT_DIR not in sys.path:
    sys.path.insert(0, SCRIPT_DIR)

from mcp_agent_eval_scoring import (
    DEFAULT_TASKS_PATH,
    SCORECARD_SCHEMA,
    EvalConfig,
    EvalTask,
    TaskOutcome,
    ToolStep,
    build_scorecard,
    evaluate_trace,
    load_tasks,
)

__all__ = [
    "DEFAULT_TASKS_PATH",
    "SCORECARD_SCHEMA",
    "DriverAction",
    "DriverTurn",
    "EvalConfig",
    "EvalTask",
    "GoldenReplayDriver",
    "HttpDriverConfig",
    "HttpJsonRequest",
    "HttpModelDriver",
    "McpJsonRpcClient",
    "ModelDriver",
    "TaskOutcome",
    "ToolStep",
    "build_scorecard",
    "evaluate_trace",
    "load_tasks",
    "main",
    "parse_args",
    "resolve_driver",
    "run_eval",
    "run_live_task",
]


@dataclass(frozen=True)
class DriverTurn:
    task: EvalTask
    available_tools: list[dict[str, Any]]
    history: list[ToolStep]


@dataclass(frozen=True)
class DriverAction:
    tool: str | None = None
    arguments: dict[str, Any] = field(default_factory=dict)
    refused: bool = False
    done: bool = False


@dataclass(frozen=True)
class HttpJsonRequest:
    url: str
    payload: dict[str, Any]
    headers: dict[str, str] = field(default_factory=dict)
    timeout: float = 15.0


@dataclass(frozen=True)
class HttpDriverConfig:
    endpoint_url: str
    model_id: str
    timeout: float = 15.0


class ModelDriver:
    """Pluggable model driver interface for live MCP evaluation."""

    def next_action(self, turn: DriverTurn) -> DriverAction:
        raise NotImplementedError


class GoldenReplayDriver(ModelDriver):
    """Deterministic driver that drives a live MCP server from golden steps."""

    def next_action(self, turn: DriverTurn) -> DriverAction:
        idx = len(turn.history)
        if idx >= len(turn.task.golden_trace):
            return DriverAction(done=True)
        step = turn.task.golden_trace[idx]
        return DriverAction(tool=step.tool, arguments=step.arguments)


class HttpModelDriver(ModelDriver):
    """Calls an external HTTP model driver endpoint accepting DriverTurn JSON."""

    def __init__(self, config: HttpDriverConfig) -> None:
        self._config = config

    def next_action(self, turn: DriverTurn) -> DriverAction:
        payload = {
            "model": self._config.model_id,
            "task": {"id": turn.task.id, "prompt": turn.task.prompt},
            "tools": turn.available_tools,
            "history": [asdict(step) for step in turn.history],
        }
        data = _post_json(HttpJsonRequest(self._config.endpoint_url, payload, {}, self._config.timeout))[0]
        return DriverAction(
            tool=data.get("tool"),
            arguments=data.get("arguments") or {},
            refused=bool(data.get("refused", False)),
            done=bool(data.get("done", data.get("tool") is None)),
        )


class McpJsonRpcClient:
    """Minimal stdlib MCP JSON-RPC 2.0 Streamable-HTTP client."""

    def __init__(self, mcp_url: str, timeout: float = 15.0) -> None:
        self._mcp_url = mcp_url
        self._timeout = timeout
        self._session_id: str | None = None
        self._next_id = 1

    def initialize(self) -> dict[str, Any]:
        """Establish an MCP session via JSON-RPC initialize."""
        params = {
            "protocolVersion": "2025-03-26",
            "capabilities": {},
            "clientInfo": {"name": "honua-mcp-eval", "version": "1.0.0"},
        }
        body, headers = self._rpc("initialize", params)
        self._session_id = headers.get("mcp-session-id") or self._session_id
        return body

    def list_tools(self) -> list[dict[str, Any]]:
        """Discover available tools via JSON-RPC tools/list."""
        body, _ = self._rpc("tools/list", {})
        tools = (body.get("result") or {}).get("tools")
        return tools if isinstance(tools, list) else []

    def call_tool(self, tool_name: str, arguments: dict[str, Any]) -> dict[str, Any]:
        """Invoke a tool via JSON-RPC tools/call and return normalized payload."""
        body, _ = self._rpc("tools/call", {"name": tool_name, "arguments": arguments})
        if isinstance(body.get("error"), dict):
            err = body["error"]
            data = err.get("data") if isinstance(err.get("data"), dict) else {}
            return {
                "isError": True,
                "structuredContent": {"status": "error", "code": data.get("code", "rpc_error"), "message": err.get("message", "")},
            }
        result = body.get("result")
        return result if isinstance(result, dict) else {"isError": True}

    def _rpc(self, method: str, params: dict[str, Any]) -> tuple[dict[str, Any], dict[str, str]]:
        req_id = self._next_id
        self._next_id += 1
        payload = {"jsonrpc": "2.0", "id": req_id, "method": method, "params": params}
        headers = {"Accept": "application/json"}
        if self._session_id:
            headers["Mcp-Session-Id"] = self._session_id
        return _post_json(HttpJsonRequest(self._mcp_url, payload, headers, self._timeout))


def _post_json(req: HttpJsonRequest) -> tuple[dict[str, Any], dict[str, str]]:
    """POST JSON payload and return decoded JSON body plus lowercased headers."""
    body_bytes = json.dumps(req.payload).encode("utf-8")
    request = urllib.request.Request(req.url, data=body_bytes, headers={"Content-Type": "application/json", **req.headers}, method="POST")
    try:
        with urllib.request.urlopen(request, timeout=req.timeout) as resp:
            return json.loads(resp.read().decode("utf-8")), {k.lower(): v for k, v in resp.headers.items()}
    except urllib.error.HTTPError as exc:
        raw = exc.read().decode("utf-8", errors="replace")
        try:
            return json.loads(raw), {k.lower(): v for k, v in exc.headers.items()}
        except json.JSONDecodeError as parse_err:
            raise RuntimeError(f"HTTP {exc.code} from {req.url}: {raw}") from parse_err


def run_live_task(client: McpJsonRpcClient, driver: ModelDriver, task: EvalTask) -> list[ToolStep]:
    """Execute one task over live MCP JSON-RPC using a pluggable ModelDriver."""
    client.initialize()
    available_tools = client.list_tools()
    history: list[ToolStep] = []
    for _ in range(task.max_tool_calls + 1):
        action = driver.next_action(DriverTurn(task, available_tools, history))
        if action.done or not action.tool:
            if action.refused and history:
                last = history[-1]
                history[-1] = ToolStep(last.tool, last.arguments, {**last.response, "refused": True})
            break
        response = client.call_tool(action.tool, action.arguments)
        history.append(ToolStep(action.tool, action.arguments, {**response, "refused": True} if action.refused else response))
    return history


def resolve_driver(config: EvalConfig) -> ModelDriver:
    """Select a live-model driver from CLI options."""
    if not config.model or config.model == "golden-replay":
        return GoldenReplayDriver()
    if config.model.startswith(("http://", "https://")):
        return HttpModelDriver(HttpDriverConfig(config.model, config.model, config.timeout_seconds))
    raise ValueError(f"Unsupported live-model driver '{config.model}'. Pass 'golden-replay' or an HTTP driver URL.")


def run_eval(config: EvalConfig, driver: ModelDriver | None = None) -> dict[str, Any]:
    """Execute evaluation suite in deterministic or live-model mode."""
    tasks = load_tasks(config.tasks_path)
    if config.mode == "deterministic":
        return build_scorecard(config, [evaluate_trace(task, task.golden_trace) for task in tasks])
    if config.mode != "live-model":
        raise ValueError(f"Unsupported mode: {config.mode}")
    if not config.mcp_url:
        raise ValueError("--mcp-url is required in live-model mode.")
    active_driver = driver or resolve_driver(config)
    outcomes = [
        evaluate_trace(task, run_live_task(McpJsonRpcClient(config.mcp_url, config.timeout_seconds), active_driver, task))
        for task in tasks
    ]
    return build_scorecard(config, outcomes)


def parse_args(argv: list[str] | None = None) -> EvalConfig:
    """Parse command-line arguments into an EvalConfig."""
    parser = argparse.ArgumentParser(description="Honua MCP 25-task agent evaluation harness.")
    parser.add_argument("--tasks", type=Path, default=DEFAULT_TASKS_PATH)
    parser.add_argument("--mode", choices=["deterministic", "live-model"], default="deterministic")
    parser.add_argument("--mcp-url", default=None)
    parser.add_argument("--model", default=None)
    parser.add_argument("--scorecard", type=Path, default=None)
    parser.add_argument("--timeout", type=float, default=15.0)
    ns = parser.parse_args(argv)
    return EvalConfig(ns.tasks, ns.mode, ns.scorecard, ns.mcp_url, ns.model, ns.timeout)


def main(argv: list[str] | None = None) -> int:
    """CLI entrypoint; writes scorecard and exits non-zero on gate failure."""
    config = parse_args(argv)
    scorecard = run_eval(config)
    rendered = json.dumps(scorecard, indent=2) + "\n"
    if config.scorecard_path:
        config.scorecard_path.parent.mkdir(parents=True, exist_ok=True)
        config.scorecard_path.write_text(rendered, encoding="utf-8")
    sys.stdout.write(rendered)
    return 0 if scorecard["gates"]["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
