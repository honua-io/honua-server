"""Unit tests for the bounded-roster wire recording proxy."""
from __future__ import annotations

import http.client
import importlib
import sys
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "proxy"))
record_wire = importlib.import_module("record_wire")

PAYLOAD = b"0123456789abcdef"


class _UpstreamHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *_args) -> None:
        return

    def do_HEAD(self) -> None:
        if self.path == "/tile":
            self.send_response(200)
            self.send_header("Content-Type", "image/tiff")
            self.send_header("Accept-Ranges", "bytes")
            self.send_header("Content-Length", str(len(PAYLOAD)))
            self.end_headers()
            return
        if self.path == "/no-length":
            self.send_response(204)
            self.end_headers()
            return
        self.send_response(404)
        self.send_header("Content-Length", "0")
        self.end_headers()

    def do_GET(self) -> None:
        if self.path == "/tile" and self.headers.get("Range") == "bytes=2-5":
            chunk = PAYLOAD[2:6]
            self.send_response(206)
            self.send_header("Content-Type", "image/tiff")
            self.send_header("Accept-Ranges", "bytes")
            self.send_header("Content-Range", f"bytes 2-5/{len(PAYLOAD)}")
            self.send_header("Content-Length", str(len(chunk)))
            self.end_headers()
            self.wfile.write(chunk)
            return
        self.send_response(200)
        self.send_header("Content-Type", "image/tiff")
        self.send_header("Content-Length", str(len(PAYLOAD)))
        self.end_headers()
        self.wfile.write(PAYLOAD)


class RecordWireTests(unittest.TestCase):
    def setUp(self) -> None:
        self._tmpdir = tempfile.TemporaryDirectory()
        self._root = Path(self._tmpdir.name)
        self._assets = self._root / "assets"
        self._assets.mkdir()
        (self._assets / "demo.js").write_bytes(b"console.log('ok');")

        self._upstream = ThreadingHTTPServer(("127.0.0.1", 0), _UpstreamHandler)
        self._upstream_thread = threading.Thread(target=self._upstream.serve_forever, daemon=True)
        self._upstream_thread.start()

        self._saved = (record_wire.UPSTREAM, record_wire.LOG_PATH, record_wire.ASSET_ROOT, record_wire.PUBLIC_ORIGIN)
        record_wire.UPSTREAM = urlsplit(f"http://127.0.0.1:{self._upstream.server_port}")
        record_wire.LOG_PATH = self._root / "wire.jsonl"
        record_wire.ASSET_ROOT = self._assets
        record_wire.PUBLIC_ORIGIN = "http://honua:5000"

        self._proxy = ThreadingHTTPServer(("127.0.0.1", 0), record_wire.Handler)
        self._proxy_thread = threading.Thread(target=self._proxy.serve_forever, daemon=True)
        self._proxy_thread.start()

    def tearDown(self) -> None:
        self._proxy.shutdown()
        self._proxy.server_close()
        self._upstream.shutdown()
        self._upstream.server_close()
        record_wire.UPSTREAM, record_wire.LOG_PATH, record_wire.ASSET_ROOT, record_wire.PUBLIC_ORIGIN = self._saved
        self._tmpdir.cleanup()

    def _request(self, method: str, path: str, headers: dict[str, str] | None = None) -> tuple[int, dict[str, str], bytes]:
        conn = http.client.HTTPConnection("127.0.0.1", self._proxy.server_port, timeout=10)
        try:
            conn.request(method, path, headers=headers or {})
            resp = conn.getresponse()
            body = resp.read()
            return resp.status, {k.lower(): v for k, v in resp.getheaders()}, body
        finally:
            conn.close()

    def test_head_preserves_upstream_content_length(self) -> None:
        status, headers, body = self._request("HEAD", "/tile")
        self.assertEqual(status, 200)
        self.assertEqual(headers.get("content-length"), str(len(PAYLOAD)))
        self.assertEqual(headers.get("accept-ranges"), "bytes")
        self.assertEqual(body, b"")

    def test_head_204_omits_content_length(self) -> None:
        status, headers, body = self._request("HEAD", "/no-length")
        self.assertEqual(status, 204)
        self.assertNotIn("content-length", headers)
        self.assertEqual(body, b"")

    def test_head_roster_asset_reports_asset_length(self) -> None:
        status, headers, body = self._request("HEAD", "/__roster/demo.js")
        self.assertEqual(status, 200)
        self.assertEqual(headers.get("content-length"), str(len(b"console.log('ok');")))
        self.assertEqual(body, b"")

    def test_get_range_passes_partial_content_and_body_length(self) -> None:
        status, headers, body = self._request("GET", "/tile", {"Range": "bytes=2-5"})
        self.assertEqual(status, 206)
        self.assertEqual(headers.get("content-range"), f"bytes 2-5/{len(PAYLOAD)}")
        self.assertEqual(headers.get("content-length"), "4")
        self.assertEqual(body, PAYLOAD[2:6])
