#!/usr/bin/env python3
"""Stage named or single flattened interop artifacts without guessing lane identity."""
from __future__ import annotations

import argparse
import json
import shutil
import sys
from pathlib import Path

LANES = {
    "gdal": "cli", "pyqgis": "desktop-qgis", "openlayers": "js",
    "cesium": "js-cesium", "geoservices-stub": "geoservices-stub",
    "geopandas": "py-geopandas", "owslib": "py-owslib", "duckdb": "duckdb",
    "r-sf": "r-sf", "pystac": "py-pystac",
}
ARTIFACT_MARKER = "evidence-client-compat-"


def stage(artifact_root: Path, output_root: Path, selection: str = "") -> int:
    expected = selection.split(",") if selection else []
    if any(lane not in LANES for lane in expected) or len(expected) != len(set(expected)):
        raise ValueError("Expected lanes must be distinct supported Compose lane names")
    if artifact_root.is_symlink() or any(path.is_symlink() for path in artifact_root.rglob("*")):
        raise ValueError("Symbolic links are not lane artifacts")
    children = sorted(artifact_root.iterdir()) if artifact_root.is_dir() else []
    if not children:
        raise ValueError("No lane artifacts downloaded; the artifact filter or upstream upload contract is broken.")
    named = [path for path in children if path.is_dir() and ARTIFACT_MARKER in path.name]
    sources: dict[str, Path] = {}
    if named:
        if len(named) != len(children):
            raise ValueError("Mixed named and flattened artifact layout is ambiguous")
        for path in named:
            lane = path.name.rsplit(ARTIFACT_MARKER, 1)[1]
            if lane not in LANES or lane in sources:
                raise ValueError(f"Unsupported or duplicate lane artifact: {path.name}")
            sources[lane] = path
        if expected and set(sources) != set(expected):
            raise ValueError("Downloaded lane artifacts do not match the declared selection")
    else:
        # download-artifact v8 flattens a single match even when merge-multiple
        # is false. Only the validated workflow selection may name that lane;
        # payload fields must agree, but cannot select their own destination.
        if len(expected) != 1:
            raise ValueError("A flattened artifact requires exactly one declared supported lane")
        lane = expected[0]
        envelopes = sorted(artifact_root.rglob("*.cert.json"))
        if not envelopes:
            raise ValueError("Downloaded artifact staged zero .cert.json envelopes")
        for path in envelopes:
            envelope = json.loads(path.read_text(encoding="utf-8"))
            if not isinstance(envelope, dict) or envelope.get("client_lane") != LANES[lane]:
                raise ValueError(f"{path.name}: receipt client_lane does not match declared {lane}")
        sources[lane] = artifact_root
    empty = [lane for lane, source in sources.items() if not any(source.rglob("*.cert.json"))]
    if empty:
        raise ValueError(f"Downloaded lane artifact(s) staged zero .cert.json envelopes: {', '.join(empty)}")
    total = 0
    for lane, source in sources.items():
        destination = output_root / lane
        shutil.copytree(source, destination, dirs_exist_ok=True)
        count = sum(1 for _ in source.rglob("*.cert.json"))
        total += count
        print(f"lane={lane} staged ({count} cert envelope(s))")
    return total


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("artifact_root", nargs="?", type=Path, default=Path("artifacts"))
    parser.add_argument("output_root", nargs="?", type=Path, default=Path("docker/client-compat/output"))
    parser.add_argument("expected_lanes", nargs="?", default="")
    args = parser.parse_args()
    try:
        stage(args.artifact_root, args.output_root, args.expected_lanes)
    except (OSError, ValueError) as error:
        print(f"::error::{error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
