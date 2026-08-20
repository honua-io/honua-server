#!/usr/bin/env python3
"""Read Honua-produced CNG artifacts with canonical client libraries and emit evidence."""
from __future__ import annotations

import argparse
import json
from datetime import datetime, timezone
from pathlib import Path

CLIENTS = {
    "GeoPandas": "1.1.4",
    "PyArrow": "25.0.1",
    "Pyogrio": "0.13.0",
    "pmtiles": "3.7.0",
}


def _now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def _observation(surface: str, operation: str, client: str, lane: str, started: str,
                 args: argparse.Namespace) -> dict:
    return {
        "surface": surface,
        "operation": operation,
        "canonical_client": client,
        "client_version": CLIENTS[client],
        "deployment_target": "local-docker",
        "result": "pass",
        "skip_reason": None,
        "source_sha": args.source_sha,
        "image_digest": args.image_digest,
        "fixture_revision": args.fixture_revision,
        "evidence_uri": args.evidence_uri,
        "started_at": started,
        "completed_at": _now(),
    }


def validate_geoparquet(path: Path, args: argparse.Namespace) -> list[dict]:
    import geopandas
    import pyarrow.parquet

    started = _now()
    table = pyarrow.parquet.read_table(path)
    if table.num_rows < 1:
        raise ValueError("PyArrow read zero GeoParquet rows")
    metadata = table.schema.metadata or {}
    if b"geo" not in metadata:
        raise ValueError("PyArrow schema has no GeoParquet 'geo' metadata")

    frame = geopandas.read_parquet(path)
    if frame.empty or frame.geometry.isna().any():
        raise ValueError("GeoPandas did not recover non-null geometries")
    if frame.crs is None:
        raise ValueError("GeoPandas did not recover a CRS")
    return [
        _observation("geoparquet", "feature-read", "PyArrow", "pyarrow-geoparquet", started, args),
        _observation("geoparquet", "geometry-read", "GeoPandas", "geopandas-geoparquet", started, args),
    ]


def validate_flatgeobuf(path: Path, args: argparse.Namespace) -> list[dict]:
    import pyogrio

    started = _now()
    frame = pyogrio.read_dataframe(path)
    if frame.empty or frame.geometry.isna().any():
        raise ValueError("Pyogrio did not recover non-null FlatGeobuf geometries")
    if frame.crs is None:
        raise ValueError("Pyogrio did not recover a FlatGeobuf CRS")
    return [_observation("flatgeobuf", "feature-read", "Pyogrio", "pyogrio-flatgeobuf", started, args)]


def validate_pmtiles(path: Path, args: argparse.Namespace) -> list[dict]:
    from pmtiles.reader import MmapSource, Reader, all_tiles

    started = _now()
    with path.open("rb") as stream:
        source = MmapSource(stream)
        reader = Reader(source)
        header = reader.header()
        metadata = reader.metadata()
        first = next(iter(all_tiles(source)), None)
    if header.get("spec_version") != 3:
        raise ValueError(f"PMTiles reader reported spec_version={header.get('spec_version')!r}, expected 3")
    if not isinstance(metadata, dict):
        raise ValueError("PMTiles metadata is not an object")
    if first is None or not first[1]:
        raise ValueError("PMTiles reader found no non-empty tiles")
    return [_observation("pmtiles", "archive-read", "pmtiles", "python-pmtiles", started, args)]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifacts", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--image-digest", required=True)
    parser.add_argument("--candidate-cut-at", required=True)
    parser.add_argument("--fixture-revision", required=True)
    parser.add_argument("--evidence-uri", required=True)
    args = parser.parse_args()

    observations: list[dict] = []
    observations.extend(validate_geoparquet(args.artifacts / "cng.parquet", args))
    observations.extend(validate_flatgeobuf(args.artifacts / "cng.fgb", args))
    observations.extend(validate_pmtiles(args.artifacts / "honua.pmtiles", args))
    fragment = {
        "schema": "honua.protocol-certification-fragment/v1",
        "producer": "honua-server-cng",
        "generated_at": _now(),
        "candidate": {
            "source_sha": args.source_sha,
            "image_digest": args.image_digest,
            "cut_at": args.candidate_cut_at,
        },
        "observations": observations,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(fragment, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(f"canonical clients passed: {len(observations)} normalized observation(s)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
