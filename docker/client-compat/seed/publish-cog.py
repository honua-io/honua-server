#!/usr/bin/env python3
"""Publish test_service layer 0's raster as a Cloud Optimized GeoTIFF so cog/range-read can close.

The COG cannot simply be written to disk: LocalFileStorage builds its object index once at
construction (src/Honua.Io/Features/FileStorage/LocalFileStorage.cs), so it has to be
published through the running server, exactly like the PMTiles archive.

The object key is deterministic - cog/{layerId}/{rasterId}.tif, served under the range
proxy's own /api/v1/rasters/cog/ prefix - and the artifact has no TimeToLive, so one publish
serves the whole lane.

Stdlib only; the admin credential is read from the environment at call time and is never
logged, written, or echoed.

Exits 0 when the artifact answers a HEAD, 1 otherwise. The caller treats a failure as
non-fatal: only the cog cells depend on this, and their own tests fail loudly on a 404.
"""

from __future__ import annotations

import json
import os
import sys
import time
import urllib.error
import urllib.request

LAYER_ID = 0
PROXY_PREFIX = "/api/v1/rasters/cog/"

POLL_ATTEMPTS = 15
POLL_INTERVAL_SECONDS = 2


def _request(url: str, *, method: str = "GET", headers: dict | None = None,
             body: bytes | None = None) -> tuple[int, bytes]:
    request = urllib.request.Request(url, method=method, data=body, headers=headers or {})
    try:
        with urllib.request.urlopen(request, timeout=120) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as error:
        return error.code, error.read()
    except urllib.error.URLError as error:
        print(f"  COG: {method} {url} failed: {error.reason}", file=sys.stderr)
        return 0, b""


def main() -> int:
    base_url = os.environ.get("HONUA_BASE_URL", "http://honua:5000").rstrip("/")
    password = os.environ.get("HONUA_ADMIN_PASSWORD")
    if not password:
        print("  COG: HONUA_ADMIN_PASSWORD unset; skipping publish. The cog "
              "certification cells will report 404.")
        return 1

    status, body = _request(
        f"{base_url}/api/v1/admin/raster-artifacts/cog",
        method="POST",
        headers={"Content-Type": "application/json", "X-API-Key": password},
        body=json.dumps({"layerId": LAYER_ID}).encode("utf-8"),
    )
    if status != 201:
        print(f"  COG: publish returned {status}: {body[:300]!r}", file=sys.stderr)
        return 1

    try:
        descriptor = json.loads(body)
        url = descriptor["url"]
    except (ValueError, KeyError, TypeError):
        print(f"  COG: publish response was not a descriptor: {body[:300]!r}", file=sys.stderr)
        return 1

    artifact_url = f"{base_url}{url}"
    for _ in range(POLL_ATTEMPTS):
        head_status, _ = _request(artifact_url, method="HEAD")
        if head_status == 200:
            print(f"  COG: artifact published at {artifact_url} "
                  f"({descriptor.get('sizeBytes')} bytes, {descriptor.get('width')}x{descriptor.get('height')})")
            return 0
        time.sleep(POLL_INTERVAL_SECONDS)

    print(f"  COG: publish accepted but no artifact answered after "
          f"{POLL_ATTEMPTS * POLL_INTERVAL_SECONDS}s at {artifact_url}", file=sys.stderr)
    return 1


if __name__ == "__main__":
    sys.exit(main())
