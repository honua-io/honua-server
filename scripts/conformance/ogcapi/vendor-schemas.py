#!/usr/bin/env python3
"""Vendor the OGC schemas + CQL2 example corpus for the OGC API building-block
validators (hermetic CI). Pins exact commits/versions so the gate is reproducible
and does not reach the network at test time.

Sources:
  * CQL2 example corpus + cql2.json schema -> opengeospatial/ogcapi-features (pinned commit)
  * TMS 2.0 JSON Schemas (tileSet.json, tileMatrixSet.json) -> schemas.opengis.net
  * OGC API - Maps Part 1 bundled OpenAPI -> schemas.opengis.net

Re-run this script to refresh the vendored copies; commit the result. The
validators never download anything themselves.
"""
from __future__ import annotations

import json
import sys
import urllib.error
import urllib.request
from pathlib import Path

# --- Pins ---------------------------------------------------------------------
# opengeospatial/ogcapi-features master @ 2026-06; bump deliberately + re-vendor.
OGCAPI_FEATURES_COMMIT = "94e814bfa9af6ad308c474621ad18548357b0bc8"
CQL2_BASE = (
    f"https://raw.githubusercontent.com/opengeospatial/ogcapi-features/"
    f"{OGCAPI_FEATURES_COMMIT}/cql2/standard/schema"
)
GH_API_BASE = "https://api.github.com/repos/opengeospatial/ogcapi-features/contents/cql2/standard/schema"

TMS_BASE = "https://schemas.opengis.net/tms/2.0/json"
MAPS_BUNDLED = "https://schemas.opengis.net/ogcapi/maps/part1/1.0/openapi/ogcapi-maps-1.bundled.json"

HERE = Path(__file__).resolve().parent
VENDOR = HERE / "vendor"


def fetch(url: str) -> bytes:
    req = urllib.request.Request(url, headers={"User-Agent": "honua-ogc-vendor"})
    with urllib.request.urlopen(req, timeout=60) as resp:  # noqa: S310 (pinned hosts)
        return resp.read()


def fetch_json(url: str):
    return json.loads(fetch(url).decode("utf-8"))


def write(path: Path, data: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)


def list_corpus(kind: str) -> list[str]:
    """List example file names for kind in {'text','json'} via the GitHub API."""
    items = fetch_json(f"{GH_API_BASE}/examples/{kind}?ref={OGCAPI_FEATURES_COMMIT}")
    ext = ".txt" if kind == "text" else ".json"
    return sorted(it["name"] for it in items if it["name"].endswith(ext))


def main() -> int:
    print(f"Vendoring CQL2 corpus from opengeospatial/ogcapi-features@{OGCAPI_FEATURES_COMMIT[:12]}")

    # 1. cql2.json schema (official validation schema)
    write(VENDOR / "cql2-schema" / "cql2.json", fetch(f"{CQL2_BASE}/cql2.json"))
    print("  cql2-schema/cql2.json")

    # 2. example corpus (paired text/json fixtures)
    counts = {}
    for kind in ("text", "json"):
        names = list_corpus(kind)
        for name in names:
            write(VENDOR / "cql2-corpus" / kind / name, fetch(f"{CQL2_BASE}/examples/{kind}/{name}"))
        counts[kind] = len(names)
        print(f"  cql2-corpus/{kind}: {len(names)} fixtures")

    # 3. TMS 2.0 JSON schemas. tileSet.json / tileMatrixSet.json $ref sibling
    #    schemas, so vendor the whole set for offline ref resolution.
    tms_files = (
        "2DBoundingBox.json",
        "2DPoint.json",
        "crs.json",
        "dataType.json",
        "geospatialData.json",
        "link.json",
        "projJSON.json",
        "propertiesSchema.json",
        "style.json",
        "tileMatrix.json",
        "tileMatrixLimits.json",
        "tileMatrixSet.json",
        "tilePoint.json",
        "tileSet.json",
        "timeStamp.json",
        "variableMatrixWidth.json",
    )
    for name in tms_files:
        write(VENDOR / "tms-2.0" / name, fetch(f"{TMS_BASE}/{name}"))
    print(f"  tms-2.0: {len(tms_files)} schemas")

    # 4. OGC API - Maps bundled OpenAPI
    write(VENDOR / "ogcapi-maps" / "ogcapi-maps-1.bundled.json", fetch(MAPS_BUNDLED))
    print("  ogcapi-maps/ogcapi-maps-1.bundled.json")

    # 5. provenance manifest
    manifest = {
        "ogcapi_features_commit": OGCAPI_FEATURES_COMMIT,
        "cql2_corpus_text_count": counts["text"],
        "cql2_corpus_json_count": counts["json"],
        "tms_schemas": list(tms_files),
        "tms_source": TMS_BASE,
        "maps_openapi_source": MAPS_BUNDLED,
    }
    write(VENDOR / "MANIFEST.json", (json.dumps(manifest, indent=2) + "\n").encode("utf-8"))
    print("  MANIFEST.json")
    print("Done.")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except urllib.error.URLError as exc:  # pragma: no cover
        print(f"ERROR: network fetch failed: {exc}", file=sys.stderr)
        sys.exit(1)
