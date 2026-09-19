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

from cellkit import utc_now


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
    }
    freeze = Path("/opt/lane-freeze.txt")
    if freeze.is_file():
        identity["packages"] = freeze.read_text(encoding="utf-8").split()
    extra = os.environ.get("ROSTER_LANE_IDENTITY_COMMAND")
    if extra:
        identity["client_identity"] = subprocess.run(
            extra, shell=True, check=False, capture_output=True, text=True).stdout.strip()
    failures = []
    for name in modules:
        module = importlib.import_module(name)
        for cell in module.CELLS:
            identity["cells"].append(f"{name}.{cell.__name__}")
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
