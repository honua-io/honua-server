"""Run a lane's cells in order and write the lane's client identity.

Usage: python -m runlane <lane-name> <module> [<module> ...]

Each module exposes ``CELLS``; every cell writes its own observation. A cell that
raises outside a check (a harness defect, not a client verdict) aborts the lane
with a non-zero exit so the run cannot publish a partial roster as complete.
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
    sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "lanes" / lane.split("-")[0]))
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
    for name in modules:
        module = importlib.import_module(name)
        for cell in module.CELLS:
            identity["cells"].append(f"{name}.{cell.__name__}")
            cell()
    identity["finished_at"] = utc_now()
    out = Path(os.environ.get("ROSTER_OBSERVATIONS", "/run/observations")) / "lanes"
    out.mkdir(parents=True, exist_ok=True)
    (out / f"{lane}.json").write_text(json.dumps(identity, indent=2) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
