"""mitmproxy addon: record every client request that reaches the candidate.

The bounded roster certifies that a *real client* executed a governed operation
against the immutable candidate. The client's own claim is not enough evidence
for that, so every lane talks to the candidate through this reverse proxy and
the proxy writes one JSON line per exchange. The receipt emitter joins those
lines to each cell's execution window, so a cell cannot report a pass without
on-wire requests from the client it names.

Credentials never reach the log: authorization-bearing headers are reduced to
their scheme (``Bearer``/``ApiKey``) and credential query parameters are
replaced with ``<redacted>``.
"""
from __future__ import annotations

import json
import os
import threading
from datetime import datetime, timezone
from urllib.parse import parse_qsl, urlencode, urlsplit, urlunsplit

from mitmproxy import http

LOG_PATH = os.environ.get("WIRE_LOG", "/wire/wire.jsonl")
PUBLIC_ORIGIN = os.environ.get("WIRE_PUBLIC_ORIGIN", "http://honua:5000")
CREDENTIAL_QUERY_KEYS = frozenset({
    "access_token", "api_key", "apikey", "auth", "authorization", "code",
    "id_token", "key", "password", "pwd", "refresh_token", "secret", "session",
    "sig", "signature", "token", "x-api-key",
})

_lock = threading.Lock()


def _public_url(flow: http.HTTPFlow) -> str:
    parts = urlsplit(PUBLIC_ORIGIN + flow.request.path)
    query = [
        (key, "<redacted>" if key.strip().lower() in CREDENTIAL_QUERY_KEYS else value)
        for key, value in parse_qsl(parts.query, keep_blank_values=True)
    ]
    return urlunsplit((parts.scheme, parts.netloc, parts.path, urlencode(query), ""))


def _credential_kind(request: http.Request) -> str | None:
    if "x-api-key" in request.headers:
        return "ApiKey"
    authorization = request.headers.get("authorization")
    if authorization:
        return authorization.split(" ", 1)[0]
    return None


def response(flow: http.HTTPFlow) -> None:
    request, reply = flow.request, flow.response
    record = {
        "at": datetime.fromtimestamp(request.timestamp_start, timezone.utc).isoformat(),
        "completed_at": datetime.fromtimestamp(
            reply.timestamp_end or reply.timestamp_start, timezone.utc).isoformat(),
        "client_address": flow.client_conn.peername[0] if flow.client_conn.peername else None,
        "method": request.method,
        "url": _public_url(flow),
        "user_agent": request.headers.get("user-agent"),
        "credential": _credential_kind(request),
        "range": request.headers.get("range"),
        "accept": request.headers.get("accept"),
        "request_content_type": request.headers.get("content-type"),
        "status": reply.status_code,
        "content_type": reply.headers.get("content-type"),
        "content_range": reply.headers.get("content-range"),
        "response_bytes": len(reply.raw_content or b""),
    }
    with _lock, open(LOG_PATH, "a", encoding="utf-8") as handle:
        handle.write(json.dumps(record, separators=(",", ":")) + "\n")
