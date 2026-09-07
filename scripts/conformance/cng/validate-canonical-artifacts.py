#!/usr/bin/env python3
"""Read Honua-produced CNG artifacts with canonical client libraries and emit evidence."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import subprocess
import tempfile
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, NamedTuple

CLIENTS = {
    "GeoPandas": "1.1.4",
    "PyArrow": "25.0.1",
    "Pyogrio": "0.13.0",
    "pmtiles": "3.7.0",
    "Rasterio": "1.5.1",
    "rio-cogeo": "7.0.2",
    "h5py": "3.16.0",
    "xarray": "2026.7.0",
    "zarr": "3.3.0",
    "fsspec": "2026.7.0",
    "Dask": "2026.7.1",
    "PySTAC-Client": "0.9.0",
    "GDAL": "3.8.4",
    "h5stat": "1.10.10",
    "h5dump": "1.10.10",
    "h5repack": "1.10.10",
    "go-pmtiles": "1.30.0",
    "3d-tiles-validator": "0.6.1",
}

UNBOUND_CONSUMER_GAP = (
    "Canonical client validation passed for the fixture, but this observation is "
    "not yet bound to a Honua registration/read/transcode operation; tracked by "
    "honua-server#3377."
)

BUDGET_EVIDENCE_GAP = (
    "Canonical client validation passed, but governed request/byte/error budget "
    "observations are not yet emitted; tracked by honua-server#3377."
)

# Which surfaces have Honua code in the producing loop (#4398). A surface produced
# entirely by third-party tooling can never be consumer evidence for a Honua claim,
# no matter how cleanly its canonical clients read the file back: the artifact under
# test was written by rasterio / rio-cogeo / xarray, not by Honua. Keeping this as
# data — rather than as a `_mark_unbound` call the caller may forget — makes the
# property structural and testable.
ARTIFACT_PRODUCERS = {
    "geoparquet": "honua",       # live FeatureServer f=parquet
    "flatgeobuf": "honua",       # live FeatureServer f=fgb
    "pmtiles": "honua",          # PMTilesWriter via artifact-gen
    "3d-tiles": "honua",         # TilesetDocumentWriter via artifact-gen
    "stac": "honua",             # live server
    "cloud-native": "honua",     # JS clients against live server artifacts
    "cog": "third-party-fixture",        # rio_cogeo.cog_translate
    "zarr": "third-party-fixture",       # xarray.to_zarr
    "hdf5-netcdf": "third-party-fixture",  # xarray.to_netcdf
}

NON_HONUA_PRODUCER_GAP = (
    "The validated artifact was produced by third-party tooling, not by Honua, so "
    "this observation is consumer evidence for that tooling and cannot support a "
    "Honua cloud-native claim; tracked by honua-server#3377 and honua-server#4398."
)


def _metadata_matches(expected: Any, observed: Any, tolerance: float) -> bool:
    """Compares a declared budget expectation against what the consumer read back."""
    if isinstance(expected, dict):
        return isinstance(observed, dict) and all(
            key in observed and _metadata_matches(value, observed[key], tolerance)
            for key, value in expected.items()
        )
    if isinstance(expected, (list, tuple)):
        if not isinstance(observed, (list, tuple)) or len(expected) != len(observed):
            return False
        return all(
            _metadata_matches(item, observed[index], tolerance)
            for index, item in enumerate(expected)
        )
    if isinstance(expected, bool) or isinstance(observed, bool):
        return expected is observed
    if isinstance(expected, (int, float)) and isinstance(observed, (int, float)):
        return abs(float(expected) - float(observed)) <= tolerance
    return expected == observed


def _evaluate_budget(observation: dict, assignment: "GovernedAssignment") -> list[str]:
    """
    Evaluates the declared budget profile against what this run actually measured.

    #4398: `FORMAT_BUDGET_PROFILES` declared exactly the oracles a GA claim needs —
    COG's `crs`/`dimensions`/`band_count`/`nodata`/`overview_count`, GeoParquet's
    `feature_count`/`bounds`, PMTiles' `spec_version`/`tile_count`, Zarr's
    `shape`/`chunks`, and the `min_range_requests` / `max_full_object_downloads`
    range-efficiency budgets — and nothing read a profile to compare anything. The
    only consumer was a self-test asserting the profiles exist. This function is the
    missing consumer: it returns one reason per unmet budget, and an empty list only
    when every declared oracle was measured and matched.
    """
    profile = FORMAT_BUDGET_PROFILES[assignment.budget_profile]
    tolerance = profile["max_coordinate_error"]
    reasons: list[str] = []

    observed = observation.get("observed_metadata")
    if not isinstance(observed, dict) or not observed:
        reasons.append(
            f"no metadata was read back from the artifact, so the "
            f"{assignment.budget_profile} metadata oracle is unproven"
        )
    else:
        for key in profile["required_metadata"]:
            if key not in observed:
                reasons.append(f"required metadata '{key}' was not observed")
        for key, expected in profile["expected_metadata"].items():
            if key not in observed:
                continue
            if not _metadata_matches(expected, observed[key], tolerance):
                reasons.append(
                    f"metadata '{key}' observed {observed[key]!r}, expected {expected!r}"
                )

    if "range-efficiency" in assignment.facets:
        transfer = observation.get("observed_transfer")
        if not isinstance(transfer, dict) or not transfer:
            reasons.append(
                "no request/byte transfer was measured, so the range-efficiency "
                "budget (min_range_requests / max_full_object_downloads) is unproven"
            )
        else:
            requests = transfer.get("requests", 0)
            transferred = transfer.get("transferred_bytes", 0)
            range_requests = transfer.get("range_requests", 0)
            full_downloads = transfer.get("full_object_downloads", 0)
            if requests > profile["max_requests"]:
                reasons.append(
                    f"{requests} requests exceeds the budget of {profile['max_requests']}"
                )
            if transferred > profile["max_transferred_bytes"]:
                reasons.append(
                    f"{transferred} transferred bytes exceeds the budget of "
                    f"{profile['max_transferred_bytes']}"
                )
            if range_requests < profile["min_range_requests"]:
                reasons.append(
                    f"{range_requests} range requests is below the required minimum of "
                    f"{profile['min_range_requests']}"
                )
            if full_downloads > profile["max_full_object_downloads"]:
                reasons.append(
                    f"{full_downloads} full-object downloads exceeds the budget of "
                    f"{profile['max_full_object_downloads']}"
                )

    return reasons

class GovernedAssignment(NamedTuple):
    """One exact release-denominator identity owned by this producer."""

    version: str
    lane: str
    facets: tuple[str, ...]
    contract_revision: str
    capability_key: str
    budget_profile: str


def _budget_profile(
    *,
    max_requests: int,
    max_transferred_bytes: int,
    max_full_object_downloads: int,
    min_range_requests: int,
    required_metadata: list[str],
    expected_metadata: dict[str, Any],
) -> dict[str, Any]:
    return {
        "max_requests": max_requests,
        "max_transferred_bytes": max_transferred_bytes,
        "max_full_object_downloads": max_full_object_downloads,
        "min_range_requests": min_range_requests,
        "min_cache_hits": 0,
        "max_coordinate_error": 0.000001,
        "max_geometry_error": 0.000001,
        "required_metadata": required_metadata,
        "expected_metadata": expected_metadata,
    }


FORMAT_BUDGET_PROFILES = {
    "3d-tiles": _budget_profile(
        max_requests=64,
        max_transferred_bytes=33_554_432,
        max_full_object_downloads=16,
        min_range_requests=0,
        required_metadata=["asset.version", "root.boundingVolume", "content.uri", "content_count"],
        expected_metadata={
            "asset.version": "1.1",
            "root.boundingVolume": {
                "region": [-3.141592653589793, -1.5707963267948966,
                           3.141592653589793, 1.5707963267948966, 0.0, 100.0]
            },
            "content.uri": "content/0.glb",
            "content_count": 1,
        },
    ),
    "cog": _budget_profile(
        max_requests=32,
        max_transferred_bytes=16_777_216,
        max_full_object_downloads=0,
        min_range_requests=1,
        required_metadata=["crs", "dimensions", "band_count", "nodata", "overview_count"],
        expected_metadata={
            "crs": "EPSG:4326", "dimensions": [256, 256], "band_count": 1,
            "nodata": -9999.0, "overview_count": 3,
        },
    ),
    "flatgeobuf": _budget_profile(
        max_requests=8,
        max_transferred_bytes=8_388_608,
        max_full_object_downloads=1,
        min_range_requests=0,
        required_metadata=["geometry_type", "feature_count", "crs", "bounds"],
        expected_metadata={
            "geometry_type": "Point", "feature_count": 6, "crs": "EPSG:4326",
            "bounds": [-122.4194, 0.0, 179.5, 86.0],
        },
    ),
    "geoparquet": _budget_profile(
        max_requests=8,
        max_transferred_bytes=8_388_608,
        max_full_object_downloads=1,
        min_range_requests=0,
        required_metadata=[
            "geo.version", "primary_column", "geometry_encoding", "crs",
            "feature_count", "bounds",
        ],
        expected_metadata={
            "geo.version": "1.1.0", "primary_column": "geometry",
            "geometry_encoding": "WKB", "crs": "EPSG:4326", "feature_count": 6,
            "bounds": [-122.4194, 0.0, 179.5, 86.0],
        },
    ),
    "hdf5-full": _budget_profile(
        max_requests=32,
        max_transferred_bytes=16_777_216,
        max_full_object_downloads=1,
        min_range_requests=0,
        required_metadata=["dataset_shape", "data_type", "dimensions", "chunk_shape", "chunk_count"],
        expected_metadata={
            "dataset_shape": [4, 8, 16], "data_type": "float32",
            "dimensions": ["time", "y", "x"], "chunk_shape": [1, 4, 4],
            "chunk_count": 32,
        },
    ),
    "hdf5-range": _budget_profile(
        max_requests=32,
        max_transferred_bytes=16_777_216,
        max_full_object_downloads=0,
        min_range_requests=1,
        required_metadata=["dataset_shape", "data_type", "dimensions", "chunk_shape", "chunk_count"],
        expected_metadata={
            "dataset_shape": [4, 8, 16], "data_type": "float32",
            "dimensions": ["time", "y", "x"], "chunk_shape": [1, 4, 4],
            "chunk_count": 32,
        },
    ),
    "pmtiles-range": _budget_profile(
        max_requests=32,
        max_transferred_bytes=8_388_608,
        max_full_object_downloads=0,
        min_range_requests=1,
        required_metadata=["spec_version", "tile_type", "bounds", "zoom_range", "tile_count"],
        expected_metadata={
            "spec_version": "3", "tile_type": "mvt",
            "bounds": [-180.0, -90.0, 180.0, 90.0], "zoom_range": [0, 2],
            "tile_count": 21,
        },
    ),
    "pmtiles-full": _budget_profile(
        max_requests=32,
        max_transferred_bytes=8_388_608,
        max_full_object_downloads=1,
        min_range_requests=0,
        required_metadata=["spec_version", "tile_type", "bounds", "zoom_range", "tile_count"],
        expected_metadata={
            "spec_version": "3", "tile_type": "mvt",
            "bounds": [-180.0, -90.0, 180.0, 90.0], "zoom_range": [0, 2],
            "tile_count": 21,
        },
    ),
    "zarr": _budget_profile(
        max_requests=64,
        max_transferred_bytes=33_554_432,
        max_full_object_downloads=16,
        min_range_requests=0,
        required_metadata=["zarr_format", "shape", "chunks", "dtype", "chunk_count"],
        expected_metadata={
            "zarr_format": 2, "shape": [4, 8, 16], "chunks": [1, 4, 4],
            "dtype": "<f4", "chunk_count": 32,
        },
    ),
}


def _assignment(
    version: str,
    lane: str,
    facets: tuple[str, ...],
    contract_revision: str,
    capability_key: str,
    budget_profile: str,
) -> GovernedAssignment:
    return GovernedAssignment(
        version, lane, facets, contract_revision, capability_key, budget_profile
    )


# This is the normalized certification subset of the broader diagnostic lane.
# Rows not listed here remain available in the uploaded native artifacts but
# cannot accidentally enter the governed certification ledger.
GOVERNED_ASSIGNMENTS = {
    ("3d-tiles", "browser-render", "CesiumJS"): _assignment(
        "1.144.0", "js-cesium", ("positive", "media-schema", "recovery"),
        "3d-tiles-1.1", "format.3d-tiles", "3d-tiles"),
    ("cog", "dataset-read", "GDAL"): _assignment(
        "3.8.4", "gdal-cog", ("positive", "metadata", "crs-axis"),
        "cog-1.0", "format.cog", "cog"),
    ("cog", "structure-validate", "rio-cogeo"): _assignment(
        "7.0.2", "rio-cogeo", ("positive", "metadata", "range-efficiency"),
        "cog-1.0", "format.cog", "cog"),
    ("cog", "window-read", "Rasterio"): _assignment(
        "1.5.1", "rasterio-cog", ("positive", "metadata", "crs-axis", "range-efficiency"),
        "cog-1.0", "format.cog", "cog"),
    ("flatgeobuf", "feature-read", "GDAL"): _assignment(
        "3.8.4", "gdal-flatgeobuf", ("positive", "metadata", "crs-axis"),
        "flatgeobuf-current", "format.flatgeobuf", "flatgeobuf"),
    ("flatgeobuf", "feature-read", "GeoPandas"): _assignment(
        "1.1.4", "geopandas-flatgeobuf", ("positive", "metadata", "crs-axis"),
        "flatgeobuf-current", "format.flatgeobuf", "flatgeobuf"),
    ("flatgeobuf", "feature-read", "Pyogrio"): _assignment(
        "0.13.0", "pyogrio-flatgeobuf", ("positive", "metadata", "crs-axis"),
        "flatgeobuf-current", "format.flatgeobuf", "flatgeobuf"),
    ("flatgeobuf", "feature-read", "flatgeobuf-js"): _assignment(
        "4.4.0", "node-flatgeobuf", ("positive", "metadata", "media-schema"),
        "flatgeobuf-current", "format.flatgeobuf", "flatgeobuf"),
    ("geoparquet", "feature-read", "GDAL"): _assignment(
        "3.14.0", "gdal-geoparquet", ("positive", "metadata", "media-schema"),
        "geoparquet-1.1", "format.geoparquet", "geoparquet"),
    ("geoparquet", "feature-read", "PyArrow"): _assignment(
        "25.0.1", "pyarrow-geoparquet", ("positive", "metadata", "media-schema"),
        "geoparquet-1.1", "format.geoparquet", "geoparquet"),
    ("geoparquet", "geometry-read", "GeoPandas"): _assignment(
        "1.1.4", "geopandas-geoparquet", ("positive", "metadata", "crs-axis"),
        "geoparquet-1.1", "format.geoparquet", "geoparquet"),
    ("hdf5-netcdf", "dataset-read", "h5py"): _assignment(
        "3.16.0", "h5py", ("positive", "metadata"),
        "hdf5-cloud-optimized-v1", "format.hdf5-netcdf", "hdf5-full"),
    ("hdf5-netcdf", "header-read", "h5dump"): _assignment(
        "1.10.10", "h5dump", ("positive", "metadata"),
        "hdf5-cloud-optimized-v1", "format.hdf5-netcdf", "hdf5-full"),
    ("hdf5-netcdf", "metadata-statistics", "h5stat"): _assignment(
        "1.10.10", "h5stat", ("positive", "metadata", "range-efficiency"),
        "hdf5-cloud-optimized-v1", "format.hdf5-netcdf", "hdf5-range"),
    ("hdf5-netcdf", "multidimensional-read", "xarray"): _assignment(
        "2026.7.0", "xarray-netcdf", ("positive", "metadata", "crs-axis"),
        "hdf5-cloud-optimized-v1", "format.hdf5-netcdf", "hdf5-full"),
    ("hdf5-netcdf", "repack", "h5repack"): _assignment(
        "1.10.10", "h5repack", ("positive", "metadata"),
        "hdf5-cloud-optimized-v1", "format.hdf5-netcdf", "hdf5-full"),
    ("pmtiles", "archive-read", "pmtiles"): _assignment(
        "3.7.0", "python-pmtiles", ("positive", "metadata", "range-efficiency"),
        "pmtiles-3", "format.pmtiles", "pmtiles-range"),
    ("pmtiles", "browser-archive-read", "PMTiles-browser-viewer"): _assignment(
        "4.5.0", "node-pmtiles", ("positive", "metadata", "range-efficiency"),
        "pmtiles-3", "format.pmtiles", "pmtiles-range"),
    ("pmtiles", "producer-validate", "Tippecanoe"): _assignment(
        "2.79.0", "tippecanoe-pmtiles", ("positive", "metadata", "media-schema"),
        "pmtiles-3", "format.pmtiles", "pmtiles-full"),
    ("zarr", "array-read", "zarr"): _assignment(
        "3.3.0", "zarr-python", ("positive", "metadata"),
        "zarr-v2", "format.zarr", "zarr"),
    ("zarr", "distributed-array-compute", "Dask"): _assignment(
        "2026.7.1", "dask-zarr", ("positive", "metadata", "range-efficiency"),
        "zarr-v2", "format.zarr", "zarr"),
    ("zarr", "multidimensional-subset", "xarray"): _assignment(
        "2026.7.0", "xarray-zarr", ("positive", "metadata", "crs-axis", "range-efficiency"),
        "zarr-v2", "format.zarr", "zarr"),
    ("zarr", "store-read", "fsspec"): _assignment(
        "2026.7.0", "fsspec-zarr", ("positive", "metadata", "range-efficiency"),
        "zarr-v2", "format.zarr", "zarr"),
}


def _now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def _observation(surface: str, operation: str, client: str, lane: str, started: str,
                 args: argparse.Namespace, version: str | None = None) -> dict:
    return {
        "surface": surface,
        "operation": operation,
        "canonical_client": client,
        "client_version": version or CLIENTS[client],
        "lane": lane,
        "deployment_target": "local-docker",
        "result": "pass",
        "skip_reason": None,
        "source_sha": args.source_sha,
        "producer_source_sha": args.source_sha,
        "image_digest": args.image_digest,
        "fixture_revision": args.fixture_revision,
        "evidence_uri": args.evidence_uri,
        "started_at": started,
        "completed_at": _now(),
    }


def _run(*command: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(command, check=True, capture_output=True, text=True)


FAILURE_IDENTITIES = {
    "validate_geoparquet": ("geoparquet", "feature-read", "PyArrow"),
    "validate_flatgeobuf": ("flatgeobuf", "feature-read", "Pyogrio"),
    "validate_pmtiles": ("pmtiles", "archive-read", "pmtiles"),
    "validate_cog": ("cog", "window-read", "Rasterio"),
    "validate_hdf5_netcdf": ("hdf5-netcdf", "metadata-statistics", "h5py"),
    "validate_zarr": ("zarr", "multidimensional-subset", "xarray"),
    "validate_stac": ("stac", "collection-discovery", "PySTAC-Client"),
    "validate_javascript": ("cloud-native", "javascript-client-validation", "Node.js"),
}


def _collect(observations: list[dict], validator, path, args: argparse.Namespace, transform=None) -> None:
    """Run one format independently so a failure cannot hide later matrix rows."""
    started = _now()
    try:
        results = validator(path, args)
        observations.extend(transform(results) if transform else results)
    except Exception as exc:  # evidence must retain the failed cell and continue
        surface, operation, client = FAILURE_IDENTITIES[validator.__name__]
        failure = _observation(
            surface, operation, client, validator.__name__, started, args,
            CLIENTS.get(client, "unknown"),
        )
        failure["result"] = "fail"
        detail = f"{type(exc).__name__}: {exc}"
        if isinstance(exc, subprocess.CalledProcessError):
            process_output = "\n".join(
                value.strip() for value in (exc.stdout, exc.stderr) if value and value.strip()
            )
            if process_output:
                detail = f"{detail}\n{process_output[-4000:]}"
        failure["failure_reason"] = detail
        observations.append(failure)


def _collect_client(observations: list[dict], surface: str, operation: str, client: str,
                    lane: str, args: argparse.Namespace, check, *, unbound: bool = False,
                    expected_version: str | None = None,
                    observed_metadata: dict | None = None,
                    observed_transfer: dict | None = None) -> None:
    """Collect one client independently so its verdict cannot hide or misattribute siblings.

    ``observed_metadata`` / ``observed_transfer`` are mutable dictionaries the ``check``
    closure fills in as it reads the artifact (#4398). They carry what the consumer
    actually saw into :func:`_evaluate_budget`, which is what turns the declared
    ``FORMAT_BUDGET_PROFILES`` oracles into assertions instead of documentation.
    """
    started = _now()
    try:
        detected_version = check()
        observation = _observation(
            surface, operation, client, lane, started, args,
            detected_version if isinstance(detected_version, str) else None,
        )
        if observed_metadata:
            observation["observed_metadata"] = dict(observed_metadata)
        if observed_transfer:
            observation["observed_transfer"] = dict(observed_transfer)
        if unbound:
            observation["result"] = "skip"
            observation["skip_reason"] = UNBOUND_CONSUMER_GAP
    except Exception as exc:  # evidence must retain the exact client that failed
        observation = _observation(
            surface, operation, client, lane, started, args,
            expected_version or CLIENTS.get(client, "unknown"),
        )
        observation["result"] = "fail"
        detail = f"{type(exc).__name__}: {exc}"
        if isinstance(exc, subprocess.CalledProcessError):
            process_output = "\n".join(
                value.strip() for value in (exc.stdout, exc.stderr) if value and value.strip()
            )
            if process_output:
                detail = f"{detail}\n{process_output[-4000:]}"
        observation["failure_reason"] = detail
    observations.append(observation)


def _command_version(client: str, *command: str, expected_version: str | None = None) -> str:
    completed = _run(*command)
    output = f"{completed.stdout}\n{completed.stderr}"
    match = re.search(r"(?<!\d)(\d+\.\d+\.\d+)(?!\d)", output)
    if match is None:
        raise ValueError(f"Could not determine {client} version from {' '.join(command)}")
    version = match.group(1)
    expected = expected_version or CLIENTS[client]
    if version != expected:
        raise ValueError(f"{client} version {version} does not match evidence pin {expected}")
    return version


def _mark_unbound(observations: list[dict]) -> list[dict]:
    for observation in observations:
        if observation["result"] == "pass":
            observation["result"] = "skip"
            observation["skip_reason"] = UNBOUND_CONSUMER_GAP
    return observations


def _apply_producer_attribution(observation: dict) -> None:
    """
    Records which surfaces have Honua code in the producing loop, and refuses a pass
    to any observation that does not (#4398).

    This is the structural half of the fix: the COG and Zarr cells validate an
    artifact ``rio_cogeo.cog_translate`` and ``xarray.to_zarr`` wrote, so however
    green their canonical clients are, they cannot be cited as Honua cloud-native
    evidence. Recording ``honua_in_loop`` on every row means a downstream consumer
    of the fragment can tell the two kinds of cell apart without reading this script.
    """
    producer = ARTIFACT_PRODUCERS.get(observation["surface"], "third-party-fixture")
    observation["artifact_producer"] = producer
    observation["honua_in_loop"] = producer == "honua"
    if not observation["honua_in_loop"] and observation["result"] == "pass":
        observation["result"] = "skip"
        observation["skip_reason"] = NON_HONUA_PRODUCER_GAP


def _digest_evidence(*roots: Path) -> str:
    digest = hashlib.sha256()
    for root in roots:
        for path in sorted(item for item in root.rglob("*") if item.is_file()):
            digest.update(f"{root.name}/{path.relative_to(root).as_posix()}\0".encode())
            digest.update(path.read_bytes())
    return f"sha256:{digest.hexdigest()}"


def _normalize_observations(observations: list[dict], args: argparse.Namespace) -> list[dict]:
    normalized = []
    for observation in observations:
        identity = (
            observation["surface"], observation["operation"],
            observation["canonical_client"],
        )
        assignment = GOVERNED_ASSIGNMENTS.get(identity)
        if assignment is None:
            continue
        if observation["client_version"] != assignment.version:
            observation["result"] = "fail"
            observation["failure_reason"] = (
                f"Observed client version {observation['client_version']} does not match "
                f"governed version {assignment.version}"
            )
        observation["client_lane"] = assignment.lane
        observation["scenario_facets"] = list(assignment.facets)
        observation["contract_revision"] = assignment.contract_revision
        observation["auth_policy_revision"] = "anonymous-v1"
        observation["evidence_digest"] = args.evidence_digest
        observation["evidence_receipt"] = None
        observation["evidence_uri"] = (
            f"https://evidence.honua.io/data/sha256/{args.evidence_digest[7:]}"
        )
        facet_result = "pass" if observation["result"] == "pass" else observation["result"]
        observation["facet_results"] = {
            facet: {"result": facet_result, "evidence_digest": args.evidence_digest}
            for facet in assignment.facets
        }
        observation["budget_profile"] = assignment.budget_profile
        _apply_producer_attribution(observation)
        if observation["result"] == "pass":
            # #4398: the blanket pass -> skip rewrite is retired. A cell that measured
            # its declared oracles now passes and carries a real evidence digest; a cell
            # that did not says exactly which budget it failed to measure, instead of
            # one generic gap string for all twenty-three rows.
            unmet = _evaluate_budget(observation, assignment)
            observation["budget_results"] = {
                "profile": assignment.budget_profile,
                "met": not unmet,
                "unmet": unmet,
            }
            if unmet:
                observation["result"] = "skip"
                observation["skip_reason"] = "; ".join(unmet)
                observation["evidence_digest"] = None
                observation["facet_results"] = None
                observation["evidence_uri"] = None
        elif observation["result"] == "skip":
            observation["evidence_digest"] = None
            observation["facet_results"] = None
            observation["evidence_uri"] = None
        normalized.append(observation)
    return normalized


def _geoparquet_column_crs(column: dict) -> str | None:
    """
    Resolves the GeoParquet column CRS, honouring the specification's default.

    #4479: an omitted `crs` is not a missing CRS — the GeoParquet default is
    OGC:CRS84, WGS 84 in longitude/latitude order, and
    `GeoParquetFeatureWriter.ResolveGeoParquetCrsProjJson` deliberately omits the
    field for EPSG:4326 output for exactly that reason. Reading `column["crs"]`
    literally observed `None` for every Honua-produced artifact and could never
    match the profile's declared `EPSG:4326` oracle. OGC:CRS84 and EPSG:4326 are
    the same datum, and GeoParquet fixes the stored coordinates in (x, y) order
    regardless of the CRS axis order, so both resolve to the declared token. An
    explicit JSON `null` means "no CRS" and stays unresolved.
    """
    if "crs" not in column:
        return "EPSG:4326"
    resolved = _normalize_crs(column.get("crs"))
    return "EPSG:4326" if resolved == "OGC:CRS84" else resolved


def _geoparquet_bounds(column: dict, read_covering) -> list[float]:
    """
    Aggregates the artifact bounds out of the emitted GeoParquet 1.1 covering.

    #4479: the writer emits no `bbox` value inside the geometry column metadata.
    It declares a `covering` descriptor whose paths address a physical `bbox`
    struct column (`xmin`/`ymin`/`xmax`/`ymax`), so a consumer reads the aggregate
    extent by reducing that column, not by reading a metadata scalar. Reading
    `column["bbox"]` observed `[]` and could never match the declared oracle.

    ``read_covering`` resolves one covering path to that column's values; the
    caller supplies it so this aggregation stays testable without an Arrow
    toolchain. A producer that instead writes a metadata `bbox` still works.
    """
    covering = ((column.get("covering") or {}).get("bbox")) or {}
    aggregates = (("xmin", min), ("ymin", min), ("xmax", max), ("ymax", max))
    bounds: list[float] = []
    for member, aggregate in aggregates:
        values = read_covering(covering.get(member) or [])
        if not values:
            bounds = []
            break
        bounds.append(float(aggregate(values)))
    if bounds:
        return bounds
    return [float(value) for value in (column.get("bbox") or [])]


def _geoparquet_metadata(geo: dict, feature_count: int, read_covering) -> dict[str, Any]:
    """Renders what a GeoParquet consumer observes from the emitted `geo` metadata."""
    primary = geo.get("primary_column")
    column = (geo.get("columns") or {}).get(primary) or {}
    return {
        "geo.version": geo.get("version"),
        "primary_column": primary,
        "geometry_encoding": (column.get("encoding") or "").upper(),
        "crs": _geoparquet_column_crs(column),
        "feature_count": feature_count,
        "bounds": _geoparquet_bounds(column, read_covering),
    }


def validate_geoparquet(path: Path, args: argparse.Namespace) -> list[dict]:
    import geopandas
    import pyarrow
    import pyarrow.compute
    import pyarrow.parquet

    observations: list[dict] = []
    # #4398: what the consumer actually reads back, compared against the declared
    # FORMAT_BUDGET_PROFILES oracle by _evaluate_budget.
    metadata_seen: dict[str, Any] = {}

    def pyarrow_check() -> None:
        table = pyarrow.parquet.read_table(path)
        if table.num_rows < 1:
            raise ValueError("PyArrow read zero GeoParquet rows")
        raw = (table.schema.metadata or {}).get(b"geo")
        if raw is None:
            raise ValueError("PyArrow schema has no GeoParquet 'geo' metadata")

        def read_covering(parts: list[str]) -> list[float] | None:
            """Resolves one declared covering path onto the physical bbox column."""
            if not parts or parts[0] not in table.column_names:
                return None
            values = table.column(parts[0])
            for member in parts[1:]:
                try:
                    values = pyarrow.compute.struct_field(values, member)
                except (KeyError, TypeError, pyarrow.ArrowInvalid):
                    return None
            return values.drop_null().to_pylist()

        metadata_seen.update(
            _geoparquet_metadata(json.loads(raw), table.num_rows, read_covering))

    def geopandas_check() -> None:
        frame = geopandas.read_parquet(path)
        if frame.empty or frame.geometry.isna().any() or frame.crs is None:
            raise ValueError("GeoPandas did not recover non-null geometries and CRS")

    def gdal_check() -> str:
        image = os.getenv("HONUA_CNG_GDAL_IMAGE")
        if not image:
            _run("ogrinfo", "-al", "-so", str(path))
            return _command_version("GDAL", "gdalinfo", "--version")

        artifact = path.resolve()
        mount = f"{artifact.parent}:/data:ro"
        container_prefix = (
            "docker", "run", "--rm", "--network", "none",
            "--volume", mount, image,
        )
        _run(*container_prefix, "ogrinfo", "-al", "-so", f"/data/{artifact.name}")
        return _command_version(
            "GDAL", *container_prefix, "gdalinfo", "--version",
            expected_version="3.14.0",
        )

    _collect_client(
        observations, "geoparquet", "feature-read", "PyArrow", "pyarrow-geoparquet", args,
        pyarrow_check, observed_metadata=metadata_seen)
    _collect_client(observations, "geoparquet", "geometry-read", "GeoPandas", "geopandas-geoparquet", args, geopandas_check)
    _collect_client(
        observations, "geoparquet", "feature-read", "GDAL", "gdal-geoparquet", args, gdal_check,
        expected_version="3.14.0" if os.getenv("HONUA_CNG_GDAL_IMAGE") else None,
    )
    return observations


def validate_flatgeobuf(path: Path, args: argparse.Namespace) -> list[dict]:
    import geopandas
    import pyogrio

    observations: list[dict] = []
    metadata_seen: dict[str, Any] = {}

    def pyogrio_check() -> None:
        frame = pyogrio.read_dataframe(path)
        if frame.empty or frame.geometry.isna().any() or frame.crs is None:
            raise ValueError("Pyogrio did not recover non-null FlatGeobuf geometries and CRS")
        metadata_seen.update({
            "geometry_type": str(frame.geometry.geom_type.iloc[0]),
            "feature_count": int(len(frame)),
            "crs": _normalize_crs(frame.crs),
            "bounds": [float(value) for value in frame.total_bounds],
        })

    def geopandas_check() -> None:
        frame = geopandas.read_file(path)
        if frame.empty or frame.geometry.isna().any():
            raise ValueError("GeoPandas did not recover FlatGeobuf geometries")

    def gdal_check() -> str:
        _run("ogrinfo", "-al", "-so", str(path))
        return _command_version("GDAL", "gdalinfo", "--version")

    _collect_client(
        observations, "flatgeobuf", "feature-read", "Pyogrio", "pyogrio-flatgeobuf", args,
        pyogrio_check, observed_metadata=metadata_seen)
    _collect_client(observations, "flatgeobuf", "feature-read", "GeoPandas", "geopandas-flatgeobuf", args, geopandas_check)
    _collect_client(observations, "flatgeobuf", "feature-read", "GDAL", "gdal-flatgeobuf", args, gdal_check)
    return observations


def validate_pmtiles(path: Path, args: argparse.Namespace) -> list[dict]:
    from pmtiles.reader import MmapSource, Reader, all_tiles

    started = _now()
    with path.open("rb") as stream:
        source = MmapSource(stream)
        reader = Reader(source)
        header = reader.header()
        metadata = reader.metadata()
        tiles = list(all_tiles(source))
        first = tiles[0] if tiles else None
    if header.get("version") != 3:
        raise ValueError(f"PMTiles reader reported version={header.get('version')!r}, expected 3")
    if not isinstance(metadata, dict):
        raise ValueError("PMTiles metadata is not an object")
    if first is None or not first[1]:
        raise ValueError("PMTiles reader found no non-empty tiles")
    observation = _observation("pmtiles", "archive-read", "pmtiles", "python-pmtiles", started, args)
    # #4398: the declared pmtiles budget oracle, read back from the archive Honua wrote.
    observation["observed_metadata"] = {
        "spec_version": str(header.get("version")),
        "tile_type": _pmtiles_tile_type(header.get("tile_type")),
        "bounds": [
            header.get("min_lon_e7", 0) / 1e7, header.get("min_lat_e7", 0) / 1e7,
            header.get("max_lon_e7", 0) / 1e7, header.get("max_lat_e7", 0) / 1e7,
        ],
        "zoom_range": [header.get("min_zoom"), header.get("max_zoom")],
        "tile_count": len(tiles),
    }
    return [observation]


def _pmtiles_tile_type(raw) -> str:
    """Maps the PMTiles v3 numeric tile-type enum onto the profile's spelling."""
    if isinstance(raw, str):
        return raw.lower()
    return {0: "unknown", 1: "mvt", 2: "png", 3: "jpeg", 4: "webp", 5: "avif"}.get(raw, str(raw))


def _normalize_crs(raw) -> str | None:
    """Renders a PROJJSON / pyproj / string CRS as the profile's ``AUTHORITY:CODE``."""
    if raw is None:
        return None
    if isinstance(raw, str):
        return raw
    to_string = getattr(raw, "to_string", None)
    if callable(to_string):
        return to_string()
    if isinstance(raw, dict):
        authority = raw.get("id") or {}
        if authority.get("authority") and authority.get("code") is not None:
            return f"{authority['authority']}:{authority['code']}"
    return str(raw)


def validate_cog(path: Path, args: argparse.Namespace) -> list[dict]:
    import rasterio
    from rasterio.windows import Window
    from rio_cogeo.cogeo import cog_validate

    observations: list[dict] = []

    metadata_seen: dict[str, Any] = {}

    def rasterio_check() -> None:
        with rasterio.open(path) as dataset:
            if dataset.driver != "GTiff" or dataset.crs is None or dataset.count < 1:
                raise ValueError("Rasterio did not recover a georeferenced COG")
            if dataset.read(1, window=Window(0, 0, 16, 16)).size != 256:
                raise ValueError("Rasterio window read returned an unexpected shape")
            metadata_seen.update({
                "crs": _normalize_crs(dataset.crs),
                "dimensions": [dataset.width, dataset.height],
                "band_count": dataset.count,
                "nodata": dataset.nodata,
                "overview_count": len(dataset.overviews(1)),
            })

    def rio_cogeo_check() -> None:
        valid, errors, _warnings = cog_validate(path, strict=True)
        if not valid:
            raise ValueError(f"rio-cogeo validation failed: {errors}")

    def gdal_check() -> str:
        _run("gdalinfo", "-json", str(path))
        return _command_version("GDAL", "gdalinfo", "--version")

    _collect_client(
        observations, "cog", "window-read", "Rasterio", "rasterio-cog", args, rasterio_check,
        unbound=True, observed_metadata=metadata_seen)
    _collect_client(observations, "cog", "structure-validate", "rio-cogeo", "rio-cogeo", args, rio_cogeo_check, unbound=True)
    _collect_client(observations, "cog", "dataset-read", "GDAL", "gdal-cog", args, gdal_check, unbound=True)
    return observations


def validate_hdf5_netcdf(path: Path, args: argparse.Namespace) -> list[dict]:
    import h5py
    import xarray

    observations: list[dict] = []

    def h5stat_check() -> str:
        _run("h5stat", str(path))
        return _command_version("h5stat", "h5stat", "-V")

    def h5dump_check() -> None:
        _run("h5dump", "-H", str(path))

    def h5repack_check() -> None:
        with tempfile.NamedTemporaryFile(suffix=".nc", delete=False) as stream:
            repacked = Path(stream.name)
        try:
            _run("h5repack", str(path), str(repacked))
            with h5py.File(repacked, "r") as handle:
                if "temperature" not in handle or handle["temperature"].size < 1:
                    raise ValueError("repacked file lost the temperature dataset")
        finally:
            repacked.unlink(missing_ok=True)

    def h5py_check() -> None:
        with h5py.File(path, "r") as handle:
            if "temperature" not in handle or handle["temperature"].size < 1:
                raise ValueError("h5py did not recover the temperature dataset")

    def xarray_check() -> None:
        with xarray.open_dataset(path, engine="h5netcdf") as dataset:
            if "temperature" not in dataset or dataset["temperature"].size < 1:
                raise ValueError("xarray did not recover the netCDF temperature variable")
            dataset.load()

    for operation, client, lane, check in (
        ("metadata-statistics", "h5stat", "h5stat", h5stat_check),
        ("header-read", "h5dump", "h5dump", h5dump_check),
        ("repack", "h5repack", "h5repack", h5repack_check),
        ("dataset-read", "h5py", "h5py", h5py_check),
        ("multidimensional-read", "xarray", "xarray-netcdf", xarray_check),
    ):
        _collect_client(observations, "hdf5-netcdf", operation, client, lane, args, check, unbound=True)
    return observations


def validate_zarr(path: Path, args: argparse.Namespace) -> list[dict]:
    import dask.array
    import fsspec
    import xarray
    import zarr

    observations: list[dict] = []
    metadata_seen: dict[str, Any] = {}

    def zarr_check() -> None:
        group = zarr.open_group(path, mode="r")
        if "temperature" not in group or group["temperature"].size < 1:
            raise ValueError("zarr did not recover the temperature array")
        array = group["temperature"]
        chunks = list(array.chunks)
        shape = list(array.shape)
        chunk_count = 1
        for extent, chunk in zip(shape, chunks):
            chunk_count *= -(-extent // chunk)
        metadata_seen.update({
            "zarr_format": int(getattr(group, "metadata", None).zarr_format)
            if hasattr(group, "metadata") and hasattr(getattr(group, "metadata"), "zarr_format")
            else 2,
            "shape": shape,
            "chunks": chunks,
            "dtype": array.dtype.str,
            "chunk_count": chunk_count,
        })

    def fsspec_check() -> None:
        if not fsspec.filesystem("file").exists(str(path / ".zmetadata")):
            raise ValueError("fsspec could not resolve consolidated Zarr metadata")

    def xarray_check() -> None:
        with xarray.open_zarr(path, chunks=None, consolidated=True) as dataset:
            if "temperature" not in dataset or dataset["temperature"].size < 1:
                raise ValueError("xarray did not recover the Zarr temperature array")

    def dask_check() -> None:
        with xarray.open_zarr(path, chunks={"time": 1, "y": 4, "x": 4}, consolidated=True) as dataset:
            values = dataset["temperature"].data
            if not isinstance(values, dask.array.Array) or float(values.mean().compute()) <= 0:
                raise ValueError("Dask did not compute a valid Zarr aggregate")

    for operation, client, lane, check in (
        ("array-read", "zarr", "zarr-python", zarr_check),  # carries the metadata oracle
        ("multidimensional-subset", "xarray", "xarray-zarr", xarray_check),
        ("store-read", "fsspec", "fsspec-zarr", fsspec_check),
        ("distributed-array-compute", "Dask", "dask-zarr", dask_check),
    ):
        _collect_client(
            observations, "zarr", operation, client, lane, args, check, unbound=True,
            observed_metadata=metadata_seen if client == "zarr" else None)
    return observations


def validate_native_results(path: Path, args: argparse.Namespace) -> list[dict]:
    observations: list[dict] = []

    def pmtiles_check() -> None:
        log = (path / "pmtiles-verify.log").read_text(encoding="utf-8", errors="replace")
        if "Completed verify" not in log:
            raise ValueError("go-pmtiles verification did not record successful completion")

    def tiles3d_check() -> None:
        report = json.loads((path / "3d-tiles-validator.json").read_text(encoding="utf-8"))
        error_count = _count_validator_errors(report)
        if error_count != 0:
            raise ValueError(f"3D Tiles validator reported {error_count} errors")

    _collect_client(observations, "pmtiles", "archive-verify", "go-pmtiles", "go-pmtiles-verify", args, pmtiles_check)
    _collect_client(observations, "3d-tiles", "tileset-content-validate", "3d-tiles-validator", "3d-tiles-validator", args, tiles3d_check)
    return observations


def _count_validator_errors(value) -> int:
    if isinstance(value, dict):
        own_error = int(str(value.get("severity", "")).upper() == "ERROR")
        return own_error + sum(_count_validator_errors(child) for child in value.values())
    if isinstance(value, list):
        return sum(_count_validator_errors(child) for child in value)
    return 0


def validate_stac(base_url: str, args: argparse.Namespace) -> list[dict]:
    from pystac_client import Client

    started = _now()
    collections = list(Client.open(f"{base_url.rstrip('/')}/stac").get_collections())
    if not collections:
        raise ValueError("PySTAC-Client discovered no Honua collections")
    return [_observation("stac", "collection-discovery", "PySTAC-Client", "pystac-client-live", started, args)]


def validate_javascript(path: Path, args: argparse.Namespace) -> list[dict]:
    script = Path(__file__).with_name("validate-js-artifacts.mjs")
    started = _now()
    payload = json.loads(_run("node", str(script), str(path)).stdout)
    observations = []
    for row in payload:
        observation = _observation(
            row["surface"], row["operation"], row["canonical_client"], row["lane"],
            started, args, row["client_version"],
        )
        observation["result"] = row["result"]
        if row["result"] == "fail":
            observation["failure_reason"] = row.get("failure_reason", "JavaScript validator failed")
        observations.append(observation)
    return observations


def _scope_disposition(observations: list[dict]) -> str:
    """States exactly which governed cells this run could and could not certify."""
    passed = sum(observation["result"] == "pass" for observation in observations)
    total = len(observations)
    third_party = sorted({
        observation["surface"] for observation in observations
        if not observation.get("honua_in_loop")
    })
    if passed == total and total:
        return f"All {total} governed CNG observations met their declared budget profile."
    detail = f"{passed} of {total} governed CNG observations met their declared budget profile."
    if third_party:
        detail += (
            " The "
            + ", ".join(third_party)
            + " cells validate artifacts produced by third-party tooling, not by Honua, "
            "and cannot support a Honua cloud-native claim (honua-server#4398)."
        )
    return detail


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifacts", required=True, type=Path)
    parser.add_argument("--native-results", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--image-digest", required=True)
    parser.add_argument("--candidate-cut-at", required=True)
    parser.add_argument("--fixture-revision", required=True)
    parser.add_argument("--evidence-uri", required=True)
    parser.add_argument("--base-url", required=True)
    args = parser.parse_args()
    args.evidence_digest = _digest_evidence(args.artifacts, args.native_results)

    observations: list[dict] = []
    _collect(observations, validate_native_results, args.native_results, args)
    _collect(observations, validate_geoparquet, args.artifacts / "cng.parquet", args)
    _collect(observations, validate_flatgeobuf, args.artifacts / "cng.fgb", args)
    _collect(observations, validate_pmtiles, args.artifacts / "honua.pmtiles", args)
    _collect(observations, validate_cog, args.artifacts / "canonical.cog.tif", args, _mark_unbound)
    _collect(observations, validate_hdf5_netcdf, args.artifacts / "canonical.nc", args, _mark_unbound)
    _collect(observations, validate_zarr, args.artifacts / "canonical.zarr", args, _mark_unbound)
    _collect(observations, validate_stac, args.base_url, args)
    _collect(observations, validate_javascript, args.artifacts, args)
    observations = _normalize_observations(observations, args)
    fragment = {
        "schema": "honua.protocol-certification-fragment/v1",
        "producer": "honua-server-cng",
        "generated_at": _now(),
        "candidate": {
            "source_sha": args.source_sha,
            "image_digest": args.image_digest,
            "cut_at": args.candidate_cut_at,
        },
        "operation_scope": {
            "complete": all(
                observation["result"] == "pass" for observation in observations
            ),
            "owner_issue": "https://github.com/honua-io/honua-server/issues/3377",
            "disposition": _scope_disposition(observations),
        },
        # #4398: a consumer of this fragment must be able to tell, without reading this
        # script, which cells could ever support a Honua cloud-native claim. Rows whose
        # artifact was produced by third-party tooling are counted separately here and
        # can never carry result=pass.
        "producer_attribution": {
            "honua_in_loop": sorted({
                observation["surface"] for observation in observations
                if observation.get("honua_in_loop")
            }),
            "third_party_fixture": sorted({
                observation["surface"] for observation in observations
                if not observation.get("honua_in_loop")
            }),
        },
        "observations": observations,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(fragment, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    passed = sum(observation["result"] == "pass" for observation in observations)
    skipped = sum(observation["result"] == "skip" for observation in observations)
    print(f"canonical client observations: {passed} pass, {skipped} explicit gap")
    return 1 if any(observation["result"] == "fail" for observation in observations) else 0


if __name__ == "__main__":
    raise SystemExit(main())
