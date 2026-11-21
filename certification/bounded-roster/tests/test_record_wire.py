"""The recording wire proxy preserves HEAD Content-Length for GDAL /vsicurl."""
import importlib
import io
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "proxy"))
record_wire = importlib.import_module("record_wire")


class _CaptureHandler(record_wire.Handler):
    def __init__(self, command: str) -> None:
        self.command = command
        self.wfile = io.BytesIO()
        self.captured_status: int | None = None
        self.captured_headers: list[tuple[str, str]] = []
        self.close_connection = False

    def send_response_only(self, code: int, message: str | None = None) -> None:
        self.captured_status = code

    def send_header(self, keyword: str, value: str) -> None:
        self.captured_headers.append((keyword, value))

    def end_headers(self) -> None:
        pass


class WireProxyHeadTests(unittest.TestCase):
    def test_head_preserves_upstream_content_length_and_accept_ranges(self) -> None:
        handler = _CaptureHandler("HEAD")
        handler._send(
            200,
            [("Content-Type", "image/tiff"), ("Accept-Ranges", "bytes"), ("Content-Length", "66038")],
            b"",
        )
        headers = {k.lower(): v for k, v in handler.captured_headers}
        self.assertEqual(200, handler.captured_status)
        self.assertEqual("66038", headers.get("content-length"))
        self.assertEqual("bytes", headers.get("accept-ranges"))
        self.assertEqual(b"", handler.wfile.getvalue())

    def test_get_uses_body_length(self) -> None:
        handler = _CaptureHandler("GET")
        handler._send(
            206,
            [("Content-Type", "image/tiff"), ("Content-Range", "bytes 0-3/66038"), ("Content-Length", "99999")],
            b"II*\x00",
        )
        headers = {k.lower(): v for k, v in handler.captured_headers}
        self.assertEqual(206, handler.captured_status)
        self.assertEqual("4", headers.get("content-length"))
        self.assertEqual("bytes 0-3/66038", headers.get("content-range"))
        self.assertEqual(b"II*\x00", handler.wfile.getvalue())
