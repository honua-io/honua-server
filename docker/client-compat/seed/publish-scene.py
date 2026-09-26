#!/usr/bin/env python3
"""Generate a 3D Tiles scene so the 3d-tiles/tileset and i3s/scene-layer cells can close.

The seed publishes vector layers only. A scene is a derived artifact: it is
produced from a published polygon layer by the running server's own generator,
POST /api/v1/admin/scenes/generate, and served from /scenes/{sceneId}/tileset.json
(3D Tiles) and the SceneServer facade (I3S). Nothing in the SQL or YAML seeds
can put it there, so it is generated here, at the end of the seed, the same way
the PMTiles archive is published.

browser_compat layer 2002 is the polygon fixture the browser and desktop lanes
already draw, so the scene certifies the same data the flat lanes do.

Stdlib only, and the admin credential is read from the environment at call
time: it is never logged, never written to a file, and never echoed into the
request body that ends up in server logs.

Exits 0 when the tileset is live, 1 otherwise. The caller treats a failure as
non-fatal: only the scene cells depend on this, the remaining lanes must still
run, and those cells' own tests fail loudly on the 404 rather than skipping.
"""

from __future__ import annotations

import json
import os
import sys
import time
import urllib.error
import urllib.request

SOURCE_LAYER_ID = 2002
SCENE_ID = "cert-browser-polygons"
DISPLAY_NAME = "Certification browser polygons"

# Generation is synchronous for a fixture this small, but poll the artifact
# rather than trust the response, so the wait ends on the thing under test.
POLL_ATTEMPTS = 15
POLL_INTERVAL_SECONDS = 2


def _tileset_url(base_url: str) -> str:
    return f"{base_url}/scenes/{SCENE_ID}/tileset.json"


def _status(url: str, *, method: str = "GET", headers: dict | None = None,
            body: bytes | None = None) -> tuple[int, bytes]:
    request = urllib.request.Request(
        url, method=method, data=body, headers=headers or {})
    try:
        with urllib.request.urlopen(request, timeout=120) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as error:
        return error.code, error.read()
    except urllib.error.URLError as error:
        print(f"  Scene: {method} {url} failed: {error.reason}", file=sys.stderr)
        return 0, b""


def main() -> int:
    base_url = os.environ.get("HONUA_BASE_URL", "http://honua:5000").rstrip("/")
    password = os.environ.get("HONUA_ADMIN_PASSWORD")
    if not password:
        print("  Scene: HONUA_ADMIN_PASSWORD unset; skipping generation. The "
              "3d-tiles and i3s certification cells will report 404.")
        return 1

    public_base = os.environ.get("HONUA_CLIENT_COMPAT_PUBLIC_BASE_URL", "").rstrip("/")
    tileset_url = _tileset_url(base_url)
    existing, _ = _status(tileset_url)
    public_status = 200
    if public_base and public_base != base_url:
        public_status, _ = _status(_tileset_url(public_base))
    # An internal 200 is not enough. The advertised TLS host can still be
    # serving a cached 404 for the same scene id (#5218).
    if existing == 200 and public_status == 200:
        print(f"  Scene: tileset already live at {tileset_url}")
        return 0

    payload = json.dumps({
        "layerId": SOURCE_LAYER_ID,
        "sceneId": SCENE_ID,
        "displayName": DISPLAY_NAME,
        "description": "Generated at seed time from browser_compat layer 2002 "
                       "for client certification.",
    }).encode("utf-8")

    status, body = _status(
        f"{base_url}/api/v1/admin/scenes/generate",
        method="POST",
        headers={"Content-Type": "application/json", "X-API-Key": password},
        body=payload,
    )
    if status not in (200, 201, 202):
        # The body can echo the request but never the key, which travels in a
        # header. Truncated because a problem document is not useful at length.
        print(f"  Scene: generate returned {status}: {body[:300]!r}",
              file=sys.stderr)
        return 1

    for _ in range(POLL_ATTEMPTS):
        tileset_status, _ = _status(tileset_url)
        if tileset_status == 200:
            print(f"  Scene: tileset published at {tileset_url}")
            return 0
        time.sleep(POLL_INTERVAL_SECONDS)

    print(f"  Scene: generate accepted but no tileset appeared after "
          f"{POLL_ATTEMPTS * POLL_INTERVAL_SECONDS}s at {tileset_url}",
          file=sys.stderr)
    return 1


if __name__ == "__main__":
    sys.exit(main())
