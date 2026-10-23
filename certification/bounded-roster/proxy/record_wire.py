#!/usr/bin/env python3
"""Recording reverse proxy between the roster clients and the candidate.

The bounded roster certifies that a *real client* executed a governed operation
against the immutable candidate. The client's own claim is not enough evidence
for that, so every lane reaches the candidate through this proxy and the proxy
writes one JSON line per exchange. The receipt emitter joins those lines to each
cell's execution window, so a cell cannot report a pass without on-wire requests
from the client it names.

Browser lanes load their page and the pinned MapLibre build from ``/__roster/``
on this same origin (the proxy answers those paths itself from ``WIRE_ASSETS``),
so the browser client talks to the candidate same-origin, as a map application
deployed behind the same host would, without a CORS configuration change on the
candidate. Those asset exchanges are logged with ``"roster_asset": true`` and are
never counted as client evidence.

Credentials never reach the log: authorization-bearing headers are reduced to
their scheme (``Bearer``/``ApiKey``) and credential query parameters are
replaced with ``<redacted>``.

Standard library only; the upstream Host header is preserved so the candidate's
host validation and self links see ``honua:5000``.
"""
from __future__ import annotations

import http.client
import json
import mimetypes
import os
import threading
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qsl, urlencode, urlsplit, urlunsplit

LOG_PATH = Path(os.environ.get("WIRE_LOG", "/wire/wire.jsonl"))
PUBLIC_ORIGIN = os.environ.get("WIRE_PUBLIC_ORIGIN", "http://honua:5000")
UPSTREAM = urlsplit(os.environ.get("WIRE_UPSTREAM", "http://candidate-origin:5000"))
LISTEN_PORT = int(os.environ.get("WIRE_PORT", "5000"))
ASSET_PREFIX = "/__roster/"
ASSET_ROOT = Path(os.environ.get("WIRE_ASSETS", "/assets"))
CREDENTIAL_QUERY_KEYS = frozenset({
    "access_token", "api_key", "apikey", "auth", "authorization", "code",
    "id_token", "key", "password", "pwd", "refresh_token", "secret", "session",
    "sig", "signature", "token", "x-api-key",
})
HOP_BY_HOP = frozenset({
    "connection", "keep-alive", "proxy-authenticate", "proxy-authorization", "te", "trailers",
    "transfer-encoding", "upgrade",
})

_lock = threading.Lock()


def _now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="microseconds").replace("+00:00", "Z")


def _public_url(path: str) -> str:
    parts = urlsplit(PUBLIC_ORIGIN + path)
    query = [
        (key, "<redacted>" if key.strip().lower() in CREDENTIAL_QUERY_KEYS else value)
        for key, value in parse_qsl(parts.query, keep_blank_values=True)
    ]
    return urlunsplit((parts.scheme, parts.netloc, parts.path, urlencode(query, safe="{}:/,"), ""))


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *_args) -> None:  # the JSON log is the record
        return

    def _credential(self) -> str | None:
        if self.headers.get("X-API-Key") is not None:
            return "ApiKey"
        authorization = self.headers.get("Authorization")
        return authorization.split(" ", 1)[0] if authorization else None

    def _record(self, started: str, status: int, headers: dict[str, str], size: int, asset: bool) -> None:
        record = {
            "at": started,
            "completed_at": _now(),
            "client_address": self.client_address[0],
            "method": self.command,
            "url": _public_url(self.path),
            "user_agent": self.headers.get("User-Agent"),
            "credential": self._credential(),
            "range": self.headers.get("Range"),
            "accept": self.headers.get("Accept"),
            "request_content_type": self.headers.get("Content-Type"),
            "status": status,
            "content_type": headers.get("content-type"),
            "content_range": headers.get("content-range"),
            "response_bytes": size,
            "roster_asset": asset,
        }
        with _lock, LOG_PATH.open("a", encoding="utf-8") as handle:
            handle.write(json.dumps(record, separators=(",", ":")) + "\n")

    def _send(self, status: int, headers: list[tuple[str, str]], body: bytes) -> None:
        self.send_response_only(status)
        for name, value in headers:
            if name.lower() not in HOP_BY_HOP and name.lower() != "content-length":
                self.send_header(name, value)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Connection", "close")
        self.end_headers()
        if self.command != "HEAD":
            self.wfile.write(body)
        self.close_connection = True

    def _asset(self, started: str) -> None:
        relative = urlsplit(self.path).path[len(ASSET_PREFIX):]
        target = (ASSET_ROOT / relative).resolve()
        if ASSET_ROOT.resolve() not in target.parents or not target.is_file():
            self._send(404, [("Content-Type", "text/plain")], b"not a roster asset")
            self._record(started, 404, {"content-type": "text/plain"}, 18, True)
            return
        media = mimetypes.guess_type(target.name)[0] or "application/octet-stream"
        if target.suffix in (".js", ".mjs"):
            media = "text/javascript"
        body = target.read_bytes()
        self._send(200, [("Content-Type", media)], body)
        self._record(started, 200, {"content-type": media}, len(body), True)

    def _proxy(self) -> None:
        started = _now()
        if urlsplit(self.path).path.startswith(ASSET_PREFIX):
            self._asset(started)
            return
        length = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(length) if length else None
        headers = {name: value for name, value in self.headers.items() if name.lower() not in HOP_BY_HOP}
        connection = http.client.HTTPConnection(UPSTREAM.hostname, UPSTREAM.port or 80, timeout=300)
        try:
            connection.request(self.command, self.path, body=body, headers=headers)
            response = connection.getresponse()
            payload = response.read()
            response_headers = response.getheaders()
            status = response.status
        except OSError as error:
            payload = f"roster wire proxy: upstream error {type(error).__name__}".encode()
            response_headers, status = [("Content-Type", "text/plain")], 502
        finally:
            connection.close()
        self._send(status, response_headers, payload)
        self._record(started, status, {name.lower(): value for name, value in response_headers}, len(payload), False)

    do_GET = do_POST = do_PUT = do_PATCH = do_DELETE = do_HEAD = do_OPTIONS = _proxy


def main() -> None:
    LOG_PATH.parent.mkdir(parents=True, exist_ok=True)
    server = ThreadingHTTPServer(("0.0.0.0", LISTEN_PORT), Handler)
    server.daemon_threads = True
    print(f"roster wire proxy on :{LISTEN_PORT} -> {UPSTREAM.geturl()}, log {LOG_PATH}", flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
