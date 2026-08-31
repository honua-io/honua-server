#!/usr/bin/env python3
"""Capture immutable ETS image identities used by the aggregate CITE run."""

import argparse
import json
import subprocess
from pathlib import Path

SUITES = {
    "ogcapi-features": ("ogccite/ets-ogcapi-features10:1.9-teamengine-6.0.0-RC2", "1.0", "default", "/ogc/features"),
    "ogcapi-tiles": ("ogccite/ets-ogcapi-tiles10:1.2-teamengine-6.0.0-RC2", "1.0", "default", "/ogc/tiles"),
    "wfs10": ("ogccite/ets-wfs10:latest", "1.0", "basic", "/wfs"),
    "wfs11": ("ogccite/ets-wfs11:latest", "1.1", "basic", "/wfs"),
    "wfs20": ("ogccite/ets-wfs20:latest", "2.0", "basic", "/wfs"),
    "wfs20-transactional": ("ogccite/ets-wfs20:latest", "2.0", "transactional", "/wfs"),
    "wms11": ("ogccite/ets-wms11:1.23-teamengine-6.0.0-RC2", "1.1.1", "default", "/wms"),
    "wms13": ("ogccite/ets-wms13:1.34-teamengine-6.0.0-RC2", "1.3", "default", "/wms"),
    "wmts10": ("ogccite/ets-wmts10:1.11-teamengine-6.0.0-RC2", "1.0", "default", "/wmts"),
    "wcs20": ("ogccite/ets-wcs20:1.22-teamengine-6.0.0-RC2", "2.0", "core", "/wcs"),
    "wps20": ("honua-cite-wps20-ets:1.1", "2.0", "basic-async", "/wps"),
    "gml32": ("ogccite/ets-gml32:latest", "3.2", "applicable", "/wfs"),
    "gpkg12": ("ogccite/ets-gpkg12:latest", "1.2", "applicable", "/ogc/features"),
    "kml22": ("ogccite/ets-kml22:latest", "2.2", "applicable", "/kml"),
}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    suites = {}
    for suite_id, (image, protocol, profile, path) in SUITES.items():
        inspected = subprocess.run(
            ["docker", "image", "inspect", image, "--format", "{{json .}}"],
            check=False, capture_output=True, text=True,
        )
        if inspected.returncode != 0:
            continue
        value = json.loads(inspected.stdout)
        image_id = value.get("Id")
        if not isinstance(image_id, str) or not image_id.startswith("sha256:"):
            continue
        suites[suite_id] = {
            "suite_version": f"{image}@{image_id}",
            "team_engine_version": "6.0.0-RC2",
            "protocol_version": protocol,
            "protocol_profile": profile,
            "request_path": path,
        }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps({"schema": "honua.cite-suite-provenance/v1", "suites": suites}, indent=2) + "\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
