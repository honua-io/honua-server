#!/usr/bin/env python3
"""Capture immutable ETS image identities used by the aggregate CITE run."""

import argparse
import json
import re
import subprocess
from pathlib import Path

SUITES = {
    "ogcapi-features": ("ogccite/ets-ogcapi-features10:1.9-teamengine-6.0.0-RC2", "1.0", "default", "/ogc/features", "docker/cite/ogc-api-features/seed.sql"),
    "ogcapi-tiles": ("ogccite/ets-ogcapi-tiles10:1.2-teamengine-6.0.0-RC2", "1.0", "default", "/ogc/tiles", "docker/cite/ogc-api-tiles/seed.sql"),
    "wfs10": ("ogccite/ets-wfs10:latest", "1.0", "basic", "/wfs", "docker/cite/shared/test-data"),
    "wfs11": ("ogccite/ets-wfs11:latest", "1.1", "basic", "/wfs", "docker/cite/shared/test-data"),
    "wfs20": ("ogccite/ets-wfs20:latest", "2.0", "basic", "/wfs", "docker/cite/shared/test-data+docker/cite/wfs20/test-data"),
    "wfs20-transactional": ("ogccite/ets-wfs20:latest", "2.0", "transactional", "/wfs", "docker/cite/shared/test-data+docker/cite/wfs20/test-data"),
    "wms11": ("ogccite/ets-wms11:1.23-teamengine-6.0.0-RC2", "1.1.1", "default", "/rest/services/cite/MapServer/WMS", "docker/cite/shared/seed/mapserver.sql"),
    "wms13": ("ogccite/ets-wms13:1.34-teamengine-6.0.0-RC2", "1.3", "default", "/rest/services/cite/MapServer/WMS", "docker/cite/shared/seed/mapserver.sql"),
    "wmts10": ("ogccite/ets-wmts10:1.11-teamengine-6.0.0-RC2", "1.0", "default", "/rest/services/cite/MapServer/WMTS", "docker/cite/shared/seed/mapserver.sql"),
    "wcs20": ("ogccite/ets-wcs20:1.22-teamengine-6.0.0-RC2", "2.0", "core", "/ogc/services/cite/wcs", "docker/cite/wcs20/seed.sql"),
    "wps20": ("honua-cite-wps20-ets:1.1", "2.0", "basic-async", "/wps", "docker/cite/wps20"),
    "gml32": ("ogccite/ets-gml32:latest", "3.2", "applicable", "/wfs", "docker/cite/shared/seed/mapserver.sql"),
    "gpkg12": ("ogccite/ets-gpkg12:latest", "1.2", "applicable", "/ogc/features", "docker/cite/shared/seed/mapserver.sql"),
    "kml22": ("ogccite/ets-kml22:latest", "2.2", "applicable", "/kml", "docker/cite/shared/seed/mapserver.sql"),
}


def team_engine_version(image: str, inspected: dict) -> str:
    labels = inspected.get("Config", {}).get("Labels") or {}
    for key in ("org.opencontainers.image.teamengine.version", "teamengine.version", "TEAMENGINE_VERSION"):
        value = labels.get(key)
        if isinstance(value, str) and value:
            return value
    for value in inspected.get("Config", {}).get("Env") or []:
        if value.startswith("TEAMENGINE_VERSION=") and value.partition("=")[2]:
            return value.partition("=")[2]
    tags = inspected.get("RepoTags") or []
    if image in tags:
        match = re.search(r"teamengine-([A-Za-z0-9][A-Za-z0-9.-]*)$", image)
        if match:
            return match.group(1)
    raise ValueError(f"cannot establish TEAM Engine version from inspected image metadata: {image}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    suites = {}
    for suite_id, (image, protocol, profile, path, fixture_path) in SUITES.items():
        missing_fixtures = [item for item in fixture_path.split("+") if not Path(item).exists()]
        if missing_fixtures:
            raise FileNotFoundError(
                f"configured fixture provenance for {suite_id} does not exist: {missing_fixtures}"
            )
        inspected = subprocess.run(
            ["docker", "image", "inspect", image, "--format", "{{json .}}"],
            check=False, capture_output=True, text=True,
        )
        if inspected.returncode != 0:
            raise RuntimeError(f"cannot inspect CITE image {image}: {inspected.stderr.strip()}")
        value = json.loads(inspected.stdout)
        image_id = value.get("Id")
        if not isinstance(image_id, str) or not image_id.startswith("sha256:"):
            raise ValueError(f"inspected CITE image has no content identity: {image}")
        suite = {
            "suite_version": f"{image}@{image_id}",
            "protocol_version": protocol,
            "protocol_profile": profile,
            "request_path": path,
            "fixture_path": fixture_path,
        }
        if suite_id != "wps20":
            suite["team_engine_version"] = team_engine_version(image, value)
        suites[suite_id] = suite
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps({"schema": "honua.cite-suite-provenance/v1", "suites": suites}, indent=2) + "\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
