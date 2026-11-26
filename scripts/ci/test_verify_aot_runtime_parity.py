#!/usr/bin/env python3
"""Offline unit tests for scripts/ci/verify-aot-runtime-parity.py."""

from __future__ import annotations

import importlib.util
import json
import struct
import tempfile
import threading
import unittest
import zlib
from dataclasses import dataclass
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path


def _load_module():
    import sys

    ci_dir = str(Path(__file__).resolve().parent)
    if ci_dir not in sys.path:
        sys.path.insert(0, ci_dir)
    path = Path(__file__).with_name("verify-aot-runtime-parity.py")
    spec = importlib.util.spec_from_file_location("verify_aot_runtime_parity", path)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def _make_valid_png() -> bytes:
    sig = b"\x89PNG\r\n\x1a\n"
    ihdr_data = struct.pack(">IIBBBBB", 2, 2, 8, 2, 0, 0, 0)
    raw_scanlines = b"\x00" + (b"\x10\x20\x30" * 2)
    idat_data = zlib.compress(raw_scanlines * 2)
    return (
        sig
        + _png_chunk(b"IHDR", ihdr_data)
        + _png_chunk(b"IDAT", idat_data)
        + _png_chunk(b"IEND", b"")
    )


def _png_chunk(kind: bytes, payload: bytes) -> bytes:
    crc = zlib.crc32(kind + payload) & 0xFFFFFFFF
    return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", crc)


VALID_PNG = _make_valid_png()
VALID_PARQUET = b"PAR1" + (b"\x00" * 16) + b"PAR1"


@dataclass
class FakeServerState:
    """Mutable fault-injection switches for the fake Honua HTTP server."""

    corrupt_png: bool = False
    fail_odata_500: bool = False
    leak_reflection_in_error: bool = False
    mcp_sse_response: bool = False


def _make_handler(state: FakeServerState):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, format: str, *args: object) -> None:
            return

        def do_GET(self) -> None:
            if_none_match = self.headers.get("If-None-Match")
            self._reply(_route_get(self.path, state, if_none_match))

        def do_POST(self) -> None:
            length = int(self.headers.get("Content-Length", "0"))
            raw = self.rfile.read(length) if length > 0 else b""
            ctype = self.headers.get("Content-Type", "")
            status, out_ctype, body = _route_post(PostRequest(self.path, ctype, raw), state)
            self._reply((status, out_ctype, body, None))

        def _reply(self, response: tuple[int, str, bytes, str | None]) -> None:
            status, ctype, body, etag = response
            self.send_response(status)
            self.send_header("Content-Type", ctype)
            if etag:
                self.send_header("ETag", etag)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

    return Handler


def _route_conditional(
    path: str, if_none_match: str | None
) -> tuple[int, str, bytes, str | None] | None:
    if path.startswith("/ogc/features/collections/1000/items"):
        etag = 'W/"ogc-1000-v1"'
        if if_none_match == etag:
            return 304, "application/geo+json", b"", etag
        return 200, "application/geo+json", b'{"type":"FeatureCollection","features":[]}', etag
    if path == "/odata/Layers(1000)/Features(1)":
        etag = '"odata-1000-1"'
        if if_none_match == etag:
            return 304, "application/json", b"", etag
        return 200, "application/json", b'{"ObjectId":1}', etag
    return None


def _route_get(
    path: str, state: FakeServerState, if_none_match: str | None = None
) -> tuple[int, str, bytes, str | None]:
    conditional = _route_conditional(path, if_none_match)
    if conditional is not None:
        return conditional
    static_ok = {
        "/healthz/live": ("text/plain", b"Healthy"),
        "/healthz/ready": ("text/plain", b"Ready"),
        "/openapi.json": ("application/json", b'{"ok":true}'),
        "/ogc/features/conformance": ("application/json", b'{"ok":true}'),
        "/ogc/features/collections": ("application/json", b'{"ok":true}'),
        "/ogc/tiles/tileMatrixSets": ("application/json", b'{"ok":true}'),
        "/ogc/maps": ("application/json", b'{"ok":true}'),
        "/ogc/coverages": ("application/json", b'{"ok":true}'),
    }
    if path in static_ok:
        ctype, body = static_ok[path]
        return 200, ctype, body, None
    if path == "/odata":
        return (500, "application/json", b'{"error":"internal"}', None) if state.fail_odata_500 else (200, "application/json", b'{"value":[]}', None)
    if path == "/sharing/rest/oauth2/callback":
        return 400, "application/json", b'{"error":{"code":400,"message":"missing code"}}', None
    status, ctype, body = _route_get_extended(path, state)
    return status, ctype, body, None


def _route_get_extended(path: str, state: FakeServerState) -> tuple[int, str, bytes]:
    if "/FeatureServer/" in path:
        return _route_featureserver(path)
    if "/MapServer/export" in path:
        body = b"NOT_A_PNG_PAYLOAD" if state.corrupt_png else VALID_PNG
        return 200, "image/png", body
    if "/WMS?" in path and "REQUEST=GetMap" in path:
        return 200, "image/png", VALID_PNG
    if "/WMS?" in path or "/WMTS?" in path or "/wcs?" in path or path.startswith("/wfs?"):
        return 200, "application/xml", b"<Capabilities/>"
    if path == "/api/v1/admin/connections":
        return 401, "application/json", b'{"title":"Unauthorized","status":401}'
    if "nonexistent-aot-probe-collection" in path:
        if state.leak_reflection_in_error:
            msg = b'{"error":"System.NotSupportedException: JsonSerializerIsReflectionEnabledByDefault"}'
            return 404, "application/json", msg
        return 404, "application/json", b'{"code":"NotFound","description":"missing"}'
    return 404, "application/json", b'{"error":"not found"}'


def _route_featureserver(path: str) -> tuple[int, str, bytes]:
    if "f=parquet" in path:
        return 200, "application/vnd.apache.parquet", VALID_PARQUET
    if "f=pbf" in path:
        return 200, "application/x-protobuf", b"\x08\x01\x12\x04test"
    return 200, "application/json", b'{"type":"FeatureCollection","features":[]}'


@dataclass(frozen=True)
class PostRequest:
    """Captured POST request inputs for fake server routing."""

    path: str
    content_type: str
    body: bytes


def _route_post(req: PostRequest, state: FakeServerState) -> tuple[int, str, bytes]:
    if req.path == "/mcp":
        payload = b'{"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"list_layers"}]}}'
        if state.mcp_sse_response:
            sse = b"event: message\ndata: " + payload + b"\n\n"
            return 200, "text/event-stream", sse
        return 200, "application/json", payload
    if req.path == "/stac/search":
        if "text/plain" in req.content_type:
            return 415, "application/json", b'{"title":"Unsupported Media Type","status":415}'
        if req.body.startswith(b"{invalid"):
            return 400, "application/json", b'{"title":"Bad Request","status":400}'
        return 200, "application/json", b'{"type":"FeatureCollection","features":[]}'
    return 404, "application/json", b'{"error":"not found"}'


class VerifyAotRuntimeParityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.mod = _load_module()

    def _start_server(self, state: FakeServerState) -> str:
        server = ThreadingHTTPServer(("127.0.0.1", 0), _make_handler(state))
        thread = threading.Thread(target=server.serve_forever, args=(0.02,), daemon=True)
        thread.start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        port = server.server_address[1]
        return f"http://127.0.0.1:{port}"

    def test_happy_path_emits_valid_scorecard_and_exits_zero(self) -> None:
        base_url = self._start_server(FakeServerState())
        with tempfile.TemporaryDirectory() as tmp:
            scorecard_path = Path(tmp) / "parity.json"
            code = self.mod.main(
                ["--base-url", base_url, "--scorecard", str(scorecard_path)]
            )
            self.assertEqual(code, 0)
            data = json.loads(scorecard_path.read_text(encoding="utf-8"))
        self.assertEqual(data["schema"], "honua.eval.aot-runtime-parity.v1")
        self.assertTrue(data["summary"]["passed"])
        self.assertEqual(data["summary"]["failed_probes"], 0)
        self.assertEqual(data["summary"]["reflection_error_count"], 0)
        self.assertTrue(data["gates"]["all_probes_passed"])
        self.assertTrue(data["gates"]["zero_reflection_errors"])
        self.assertGreaterEqual(data["summary"]["total_probes"], 20)

    def test_reflection_error_in_container_log_fails_gate(self) -> None:
        base_url = self._start_server(FakeServerState())
        with tempfile.TemporaryDirectory() as tmp:
            log_path = Path(tmp) / "container.stderr.log"
            log_path.write_text(
                "info: started\n"
                "fail: System.NotSupportedException: JsonSerializerIsReflectionEnabledByDefault\n"
                "warn: IL3050 dynamic code failure\n",
                encoding="utf-8",
            )
            cfg = self.mod.ParityConfig(base_url=base_url, log_file=log_path)
            scorecard = self.mod.run_parity_suite(cfg)
        self.assertFalse(scorecard["summary"]["passed"])
        self.assertFalse(scorecard["gates"]["zero_reflection_errors"])
        self.assertEqual(scorecard["summary"]["reflection_error_count"], 2)

    def test_invalid_png_magic_fails_mapserver_probe(self) -> None:
        base_url = self._start_server(FakeServerState(corrupt_png=True))
        cfg = self.mod.ParityConfig(base_url=base_url)
        scorecard = self.mod.run_parity_suite(cfg)
        self.assertFalse(scorecard["summary"]["passed"])
        failed = [p for p in scorecard["probes"] if not p["passed"]]
        self.assertEqual([p["name"] for p in failed], ["mapserver_png_export"])
        self.assertIn("PNG magic", failed[0]["detail"])

    def test_http_500_fails_parity_gate(self) -> None:
        base_url = self._start_server(FakeServerState(fail_odata_500=True))
        code = self.mod.main(["--base-url", base_url])
        self.assertEqual(code, 1)

    def test_reflection_leak_in_error_body_is_caught(self) -> None:
        base_url = self._start_server(FakeServerState(leak_reflection_in_error=True))
        cfg = self.mod.ParityConfig(base_url=base_url)
        scorecard = self.mod.run_parity_suite(cfg)
        self.assertFalse(scorecard["summary"]["passed"])
        self.assertFalse(scorecard["gates"]["zero_reflection_errors"])
        self.assertGreaterEqual(scorecard["summary"]["reflection_error_count"], 1)

    def test_mcp_sse_event_framed_response_passes_parity(self) -> None:
        base_url = self._start_server(FakeServerState(mcp_sse_response=True))
        cfg = self.mod.ParityConfig(base_url=base_url)
        scorecard = self.mod.run_parity_suite(cfg)
        self.assertTrue(scorecard["summary"]["passed"])
        mcp_probe = next(p for p in scorecard["probes"] if p["name"] == "mcp_tools_list")
        self.assertTrue(mcp_probe["passed"])
        self.assertIn("1 tools", mcp_probe["detail"])

    def test_scan_reflection_errors_detects_all_required_patterns(self) -> None:
        sample = (
            "System.NotSupportedException: boom\n"
            "MissingMetadataException in type Foo\n"
            "JsonSerializerIsReflectionEnabledByDefault was false\n"
            "warning IL2026 trim issue\n"
            "warning IL3050 aot issue\n"
            "benign info line"
        )
        found = self.mod.scan_reflection_errors(sample)
        self.assertEqual(len(found), 5)


if __name__ == "__main__":
    unittest.main()
