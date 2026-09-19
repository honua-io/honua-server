#!/usr/bin/env python3
"""Publish a PMTiles archive so the pmtiles/archive-read certification cell can close.

The archive cannot simply be written to disk. LocalFileStorage builds its object
index once at construction (src/Honua.Io/Features/FileStorage/LocalFileStorage.cs),
so a file appearing after the server starts is invisible to the tile proxy. It has
to be published through the running server.

The object key is deterministic - pmtiles/{serviceId}/{layerId}/{tms}.pmtiles -
and the job sets no TimeToLive, so one publish serves the whole lane.

Stdlib only, and the admin credential is read from the environment at call time:
it is never logged, never written to a file, and never echoed into the job body
that ends up in server logs.

Exits 0 when the archive is live, 1 otherwise. The caller treats a failure as
non-fatal: only one certification cell depends on this, the remaining lanes must
still run, and the cell's own test fails loudly on the 404 rather than skipping -
so a silent gap is not possible either way.
"""

from __future__ import annotations

import json
import os
import sys
import time
import urllib.error
import urllib.request

SERVICE_ID = "test_service"
LAYER_ID = 0
TILE_MATRIX_SET = "WebMercatorQuad"
MIN_ZOOM = 0
MAX_ZOOM = 3

# The publish job is asynchronous and completes in about four seconds. Poll the
# artifact rather than the job, so the wait ends on the thing under test.
POLL_ATTEMPTS = 15
POLL_INTERVAL_SECONDS = 2


def _archive_url(base_url: str) -> str:
    return (
        f"{base_url}/api/v1/tiles/pmtiles/pmtiles/"
        f"{SERVICE_ID}/{LAYER_ID}/{TILE_MATRIX_SET}.pmtiles"
    )


def _status(url: str, *, method: str = "GET", headers: dict | None = None,
            body: bytes | None = None) -> tuple[int, bytes]:
    request = urllib.request.Request(
        url, method=method, data=body, headers=headers or {})
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as error:
        return error.code, error.read()
    except urllib.error.URLError as error:
        print(f"  PMTiles: {method} {url} failed: {error.reason}", file=sys.stderr)
        return 0, b""


def main() -> int:
    base_url = os.environ.get("HONUA_BASE_URL", "http://honua:5000").rstrip("/")
    password = os.environ.get("HONUA_ADMIN_PASSWORD")
    if not password:
        print("  PMTiles: HONUA_ADMIN_PASSWORD unset; skipping publish. The "
              "pmtiles certification cell will report 404.")
        return 1

    payload = json.dumps({
        "operation": "publish",
        "serviceId": SERVICE_ID,
        "layerId": LAYER_ID,
        "tileMatrixSetId": TILE_MATRIX_SET,
        "minZoom": MIN_ZOOM,
        "maxZoom": MAX_ZOOM,
    }).encode("utf-8")

    status, body = _status(
        f"{base_url}/api/v1/admin/tile-operations/jobs",
        method="POST",
        headers={"Content-Type": "application/json", "X-API-Key": password},
        body=payload,
    )
    if status not in (200, 202):
        # The body can echo the request but never the key, which travels in a
        # header. Truncated because a server error page is not useful at length.
        print(f"  PMTiles: publish job returned {status}: {body[:300]!r}",
              file=sys.stderr)
        return 1

    archive_url = _archive_url(base_url)
    for _ in range(POLL_ATTEMPTS):
        archive_status, _ = _status(archive_url)
        if archive_status == 200:
            print(f"  PMTiles: archive published at {archive_url}")
            return 0
        time.sleep(POLL_INTERVAL_SECONDS)

    print(f"  PMTiles: job accepted but no archive appeared after "
          f"{POLL_ATTEMPTS * POLL_INTERVAL_SECONDS}s at {archive_url}",
          file=sys.stderr)
    return 1


if __name__ == "__main__":
    sys.exit(main())
