"""Run a lane's cells in order and write the lane's client identity.

Usage: python -m runlane <lane-name> <module> [<module> ...]

Each module exposes ``CELLS``; every cell writes its own observation. Each cell
runs in its own interpreter so no client state (QGIS capability and tile caches,
GDAL's curl and WCS caches, OWSLib's process-global headers) can carry evidence
from one cell into another. A cell process that exits non-zero (a harness defect,
not a client verdict) fails the lane so a partial roster cannot pass as complete.
"""
from __future__ import annotations

import importlib
import json
import os
import platform
import subprocess
import sys
from pathlib import Path

from cellkit import load_requirements, utc_now

# Explicit release-denominator .13 retirements. Unknown/missing cells still execute
# and fail: this is not a generic missing-requirement suppression mechanism.
RETIRED_CELLS = {
    "owslib_cells.coverages": "client-cert/owslib/ogc-api-coverages/serve.ogc-api-coverages",
    "owslib_cells.edr": "client-cert/owslib/ogc-api-edr/serve.ogc-api-edr",
    "gdal_ogc_cells.coverages_service": "client-cert/gdal/ogc-api-coverages/serve.ogc-api-coverages",
    "gdal_ogc_cells.features_conformance": "client-cert/gdal-ogr/ogc/OGC-OP-OGC-API-FEATURES-CONFORMANCE",
    "gdal_ogc_cells.features_transactions": "client-cert/gdal-ogr/ogc/OGC-OP-OGC-API-FEATURES-TRANSACTIONS",
    "qgis_cells.wmts_service": "client-cert/qgis/wmts/serve.wmts",
    "maplibre_cells.wmts_service": "client-cert/maplibre-gl-js/wmts/serve.wmts",
}


def retired_cell(name: str, requirements: list[dict]) -> str | None:
    test_id = RETIRED_CELLS.get(name)
    if test_id and not any(test_id in row.get("test_ids", []) for row in requirements):
        return test_id
    return None


def main(argv: list[str]) -> int:
    lane, modules = argv[0], argv[1:]
    lane_directory = str(Path(__file__).resolve().parent.parent / "lanes" / lane.split("-")[0])
    sys.path.insert(0, lane_directory)
    os.environ["PYTHONPATH"] = os.pathsep.join(filter(None, [lane_directory, os.environ.get("PYTHONPATH")]))
    identity = {
        "lane": lane,
        "started_at": utc_now(),
        "python": platform.python_version(),
        "platform": platform.platform(),
        "cells": [],
        "excluded_cells": [],
    }
    freeze = Path("/opt/lane-freeze.txt")
    if freeze.is_file():
        identity["packages"] = freeze.read_text(encoding="utf-8").split()
    extra = os.environ.get("ROSTER_LANE_IDENTITY_COMMAND")
    if extra:
        identity["client_identity"] = subprocess.run(
            extra, shell=True, check=False, capture_output=True, text=True).stdout.strip()
    failures = []
    requirements = load_requirements()
    for name in modules:
        module = importlib.import_module(name)
        for cell in module.CELLS:
            cell_name = f"{name}.{cell.__name__}"
            excluded = retired_cell(cell_name, requirements)
            if excluded:
                identity["excluded_cells"].append({"test_case_id": excluded,
                    "reason": "Not required by pinned release denominator .13 (release#351/#359, PR #361)"})
                continue
            identity["cells"].append(cell_name)
            completed = subprocess.run(
                [sys.executable, "-c", f"import {name} as m; m.{cell.__name__}()"], check=False)
            if completed.returncode != 0:
                failures.append(f"{name}.{cell.__name__} exited {completed.returncode}")
    identity["finished_at"] = utc_now()
    identity["harness_failures"] = failures
    out = Path(os.environ.get("ROSTER_OBSERVATIONS", "/run/observations")) / "lanes"
    out.mkdir(parents=True, exist_ok=True)
    (out / f"{lane}.json").write_text(json.dumps(identity, indent=2) + "\n", encoding="utf-8")
    for failure in failures:
        print(f"HARNESS FAILURE: {failure}", file=sys.stderr)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
