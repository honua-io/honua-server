#!/usr/bin/env python3
"""Capture immutable ETS image identities used by the aggregate CITE run."""

import argparse
import json
import re
import subprocess
from pathlib import Path

SUITES = {
    "ogcapi-features": ("ogccite/ets-ogcapi-features10:1.9-teamengine-6.0.0-RC2", "1.0", "default", "/ogc/features", "docker/cite/ogc-api-features/compose.yml"),
    "ogcapi-tiles": ("ogccite/ets-ogcapi-tiles10:1.2-teamengine-6.0.0-RC2", "1.0", "default", "/ogc/tiles", "docker/cite/ogc-api-tiles/compose.yml"),
    "wfs10": ("ogccite/ets-wfs10:latest", "1.0", "basic", "/wfs", "docker/cite/shared/scripts/execute-wfs1-tests.sh"),
    "wfs11": ("ogccite/ets-wfs11:latest", "1.1", "basic", "/wfs", "docker/cite/shared/scripts/execute-wfs1-tests.sh"),
    "wfs20": ("ogccite/ets-wfs20:latest", "2.0", "basic", "/wfs", "docker/cite/shared/scripts/execute-wfs20-tests.sh"),
    "wfs20-transactional": ("ogccite/ets-wfs20:latest", "2.0", "transactional", "/wfs", "docker/cite/shared/scripts/execute-wfs20-tests.sh"),
    "wms11": ("ogccite/ets-wms11:1.23-teamengine-6.0.0-RC2", "1.1.1", "default", "/rest/services/cite/MapServer/WMS", "docker/cite/wms11/compose.yml"),
    "wms13": ("ogccite/ets-wms13:1.34-teamengine-6.0.0-RC2", "1.3", "default", "/rest/services/cite/MapServer/WMS", "docker/cite/wms13/compose.yml"),
    "wmts10": ("ogccite/ets-wmts10:1.11-teamengine-6.0.0-RC2", "1.0", "default", "/rest/services/cite/MapServer/WMTS", "docker/cite/wmts10/compose.yml"),
    "wcs20": ("ogccite/ets-wcs20:1.22-teamengine-6.0.0-RC2", "2.0", "core", "/ogc/services/cite/wcs", "docker/cite/shared/scripts/execute-wcs20-tests.sh"),
    "wps20": ("honua-cite-wps20-ets:1.1", "2.0", "basic-async", "/wps", "docker/cite/wps20/Dockerfile.ets"),
    "gml32": ("ogccite/ets-gml32:latest", "3.2", "applicable", "/wfs", "docker/cite/gml32/compose.yml"),
    "gpkg12": ("ogccite/ets-gpkg12:latest", "1.2", "applicable", "/ogc/features", "docker/cite/gpkg12/compose.yml"),
    "kml22": ("ogccite/ets-kml22:latest", "2.2", "applicable", "/kml", "docker/cite/kml22/compose.yml"),
}

TEAM_ENGINE = re.compile(
    r"TEAMENGINE_CONSOLE_VERSION=([0-9][0-9A-Za-z.-]+)"
    r"|teamengine-console-([0-9][0-9A-Za-z.-]+?)-bin\.zip"
    r"|team_engine=(not-applicable)"
)


def team_engine_version(provenance_path: str) -> str:
    match = TEAM_ENGINE.search(Path(provenance_path).read_text(encoding="utf-8"))
    if match is None:
        raise ValueError(f"cannot establish TEAM Engine version from {provenance_path}")
    return next(group for group in match.groups() if group is not None)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    suites = {}
    for suite_id, (image, protocol, profile, path, version_source) in SUITES.items():
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
            "team_engine_version": team_engine_version(version_source),
            "team_engine_version_source": version_source,
            "protocol_version": protocol,
            "protocol_profile": profile,
            "request_path": path,
        }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps({"schema": "honua.cite-suite-provenance/v1", "suites": suites}, indent=2) + "\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
