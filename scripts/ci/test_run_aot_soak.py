#!/usr/bin/env python3
"""Offline unit tests for scripts/ci/run-aot-soak.py."""

from __future__ import annotations

import importlib.util
import json
import sys
import tempfile
import threading
import unittest
from dataclasses import dataclass
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path


def _load_module():
    ci_dir = str(Path(__file__).resolve().parent)
    if ci_dir not in sys.path:
        sys.path.insert(0, ci_dir)
    path = Path(__file__).with_name("run-aot-soak.py")
    spec = importlib.util.spec_from_file_location("run_aot_soak", path)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


PNG_SAMPLE = b"\x89PNG\r\n\x1a\n" + (b"\x00" * 64)


@dataclass
class FakeSoakState:
    """Fault switches for the local soak test HTTP server."""

    return_500: bool = False


def _make_handler(state: FakeSoakState):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, format: str, *args: object) -> None:
            return

        def do_GET(self) -> None:
            if state.return_500 and self.path == "/odata":
                self._reply(500, "application/json", b'{"error":"boom"}')
                return
            if "/MapServer/export" in self.path:
                self._reply(200, "image/png", PNG_SAMPLE)
                return
            if self.path == "/healthz/ready":
                self._reply(200, "text/plain", b"Ready")
                return
            self._reply(200, "application/json", b'{"ok":true}')

        def _reply(self, status: int, ctype: str, body: bytes) -> None:
            self.send_response(status)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

    return Handler


class RunAotSoakTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.mod = _load_module()

    def _start_server(self, state: FakeSoakState) -> str:
        server = ThreadingHTTPServer(("127.0.0.1", 0), _make_handler(state))
        thread = threading.Thread(target=server.serve_forever, args=(0.02,), daemon=True)
        thread.start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        port = server.server_address[1]
        return f"http://127.0.0.1:{port}"

    def test_happy_path_emits_valid_soak_scorecard_and_exits_zero(self) -> None:
        base_url = self._start_server(FakeSoakState())
        with tempfile.TemporaryDirectory() as tmp:
            scorecard_path = Path(tmp) / "soak.json"
            code = self.mod.main([
                "--base-url", base_url,
                "--duration", "0.25",
                "--concurrency", "4",
                "--scorecard", str(scorecard_path),
            ])
            self.assertEqual(code, 0)
            data = json.loads(scorecard_path.read_text(encoding="utf-8"))
        self.assertEqual(data["schema"], "honua.eval.aot-soak.v1")
        self.assertTrue(data["summary"]["passed"])
        self.assertGreater(data["summary"]["total_requests"], 0)
        self.assertEqual(data["summary"]["error_count"], 0)
        self.assertIn("p50", data["summary"]["latency_ms"])
        self.assertIn("p95", data["summary"]["latency_ms"])
        self.assertIn("p99", data["summary"]["latency_ms"])
        self.assertTrue(data["gates"]["error_rate_ok"])
        self.assertTrue(data["gates"]["rss_drift_ok"])
        self.assertTrue(data["gates"]["zero_restarts_ok"])

    def test_5xx_failure_gates_soak_exit_code(self) -> None:
        base_url = self._start_server(FakeSoakState(return_500=True))
        with tempfile.TemporaryDirectory() as tmp:
            scorecard_path = Path(tmp) / "soak-fail.json"
            code = self.mod.main([
                "--base-url", base_url,
                "--duration", "0.25",
                "--concurrency", "4",
                "--max-error-rate", "0.0",
                "--scorecard", str(scorecard_path),
            ])
            self.assertEqual(code, 1)
            data = json.loads(scorecard_path.read_text(encoding="utf-8"))
        self.assertFalse(data["summary"]["passed"])
        self.assertFalse(data["gates"]["error_rate_ok"])
        self.assertGreater(data["summary"]["error_count"], 0)

    def test_rss_drift_leak_is_detected_and_fails_gate(self) -> None:
        samples = [
            self.mod.RssSample(120.0, 100.0),
            self.mod.RssSample(150.0, 101.0),
            self.mod.RssSample(180.0, 102.0),
            self.mod.RssSample(360.0, 112.0),
            self.mod.RssSample(500.0, 120.0),
            self.mod.RssSample(560.0, 124.0),
            self.mod.RssSample(600.0, 126.0),
        ]
        cfg = self.mod.SoakConfig(
            base_url="http://127.0.0.1:8080",
            container_name="honua-aot",
            duration=600.0,
            max_rss_drift_pct=5.0,
        )
        outcome = self.mod.SoakOutcome(
            samples=[self.mod.RequestSample("health_ready", 200, 1.5, True)],
            rss_samples=samples,
            restart_count=0,
            elapsed_s=600.0,
        )
        scorecard = self.mod.build_scorecard(cfg, outcome)
        self.assertFalse(scorecard["summary"]["passed"])
        self.assertFalse(scorecard["gates"]["rss_drift_ok"])
        self.assertGreater(scorecard["summary"]["rss"]["drift_pct"], 20.0)
        self.assertGreater(scorecard["summary"]["rss"]["slope_mb_per_min"], 0.0)

    def test_stable_rss_and_zero_restarts_pass_gate(self) -> None:
        base_url = self._start_server(FakeSoakState())
        readings = iter([120.0, 120.5, 121.0, 120.8, 121.2, 121.0])
        deps = self.mod.SoakDeps(
            rss_reader=lambda _name: next(readings, 121.0),
            restart_reader=lambda _name: 0,
        )
        cfg = self.mod.SoakConfig(
            base_url=base_url,
            container_name="honua-aot",
            duration=0.3,
            concurrency=2,
            sample_interval=0.05,
            max_rss_drift_pct=5.0,
        )
        outcome = self.mod.run_soak(cfg, deps)
        scorecard = self.mod.build_scorecard(cfg, outcome)
        self.assertTrue(scorecard["summary"]["passed"])
        self.assertTrue(scorecard["gates"]["rss_drift_ok"])
        self.assertGreaterEqual(scorecard["summary"]["rss"]["sample_count"], 2)

    def test_container_restart_fails_zero_restarts_gate(self) -> None:
        cfg = self.mod.SoakConfig(base_url="http://127.0.0.1:8080", container_name="aot")
        outcome = self.mod.SoakOutcome(
            samples=[self.mod.RequestSample("health_ready", 200, 1.0, True)],
            rss_samples=[self.mod.RssSample(10.0, 100.0)],
            restart_count=1,
            elapsed_s=10.0,
        )
        scorecard = self.mod.build_scorecard(cfg, outcome)
        self.assertFalse(scorecard["summary"]["passed"])
        self.assertFalse(scorecard["gates"]["zero_restarts_ok"])

    def test_parse_mem_usage_mb_handles_docker_units(self) -> None:
        self.assertEqual(self.mod.parse_mem_usage_mb("128.5MiB / 512MiB"), 128.5)
        self.assertEqual(self.mod.parse_mem_usage_mb("0.5GiB / 2GiB"), 512.0)
        self.assertEqual(self.mod.parse_mem_usage_mb("512KiB / 512MiB"), 0.5)
        self.assertIsNone(self.mod.parse_mem_usage_mb("invalid"))


if __name__ == "__main__":
    unittest.main()
