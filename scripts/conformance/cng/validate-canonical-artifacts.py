#!/usr/bin/env python3
"""Read Honua-produced CNG artifacts with canonical client libraries and emit evidence."""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import tempfile
from datetime import datetime, timezone
from pathlib import Path

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
}

UNBOUND_CONSUMER_GAP = (
    "Canonical client validation passed for the fixture, but this observation is "
    "not yet bound to a Honua registration/read/transcode operation; tracked by "
    "honua-server#3377."
)


def _now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def _observation(surface: str, operation: str, client: str, lane: str, started: str,
                 args: argparse.Namespace, version: str | None = None) -> dict:
    return {
        "surface": surface,
        "operation": operation,
        "canonical_client": client,
        "client_version": version or CLIENTS[client],
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


def _run(*command: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(command, check=True, capture_output=True, text=True)


def _command_version(client: str, *command: str) -> str:
    completed = _run(*command)
    output = f"{completed.stdout}\n{completed.stderr}"
    match = re.search(r"(?<!\d)(\d+\.\d+\.\d+)(?!\d)", output)
    if match is None:
        raise ValueError(f"Could not determine {client} version from {' '.join(command)}")
    version = match.group(1)
    expected = CLIENTS[client]
    if version != expected:
        raise ValueError(f"{client} version {version} does not match evidence pin {expected}")
    return version


def _mark_unbound(observations: list[dict]) -> list[dict]:
    for observation in observations:
        observation["result"] = "skip"
        observation["skip_reason"] = UNBOUND_CONSUMER_GAP
    return observations


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
    _run("ogrinfo", "-al", "-so", str(path))
    return [
        _observation("geoparquet", "feature-read", "PyArrow", "pyarrow-geoparquet", started, args),
        _observation("geoparquet", "geometry-read", "GeoPandas", "geopandas-geoparquet", started, args),
        _observation("geoparquet", "feature-read", "GDAL", "gdal-geoparquet", started, args,
                     _command_version("GDAL", "gdalinfo", "--version")),
    ]


def validate_flatgeobuf(path: Path, args: argparse.Namespace) -> list[dict]:
    import geopandas
    import pyogrio

    started = _now()
    frame = pyogrio.read_dataframe(path)
    if frame.empty or frame.geometry.isna().any():
        raise ValueError("Pyogrio did not recover non-null FlatGeobuf geometries")
    if frame.crs is None:
        raise ValueError("Pyogrio did not recover a FlatGeobuf CRS")
    geopandas_frame = geopandas.read_file(path)
    if geopandas_frame.empty or geopandas_frame.geometry.isna().any():
        raise ValueError("GeoPandas did not recover FlatGeobuf geometries")
    _run("ogrinfo", "-al", "-so", str(path))
    return [
        _observation("flatgeobuf", "feature-read", "Pyogrio", "pyogrio-flatgeobuf", started, args),
        _observation("flatgeobuf", "feature-read", "GeoPandas", "geopandas-flatgeobuf", started, args),
        _observation("flatgeobuf", "feature-read", "GDAL", "gdal-flatgeobuf", started, args,
                     _command_version("GDAL", "gdalinfo", "--version")),
    ]


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


def validate_cog(path: Path, args: argparse.Namespace) -> list[dict]:
    import rasterio
    from rasterio.windows import Window
    from rio_cogeo.cogeo import cog_validate

    started = _now()
    with rasterio.open(path) as dataset:
        if dataset.driver != "GTiff" or dataset.crs is None or dataset.count < 1:
            raise ValueError("Rasterio did not recover a georeferenced COG")
        if not dataset.overviews(1):
            raise ValueError("Rasterio found no COG overviews")
        if dataset.read(1, window=Window(0, 0, 16, 16)).size != 256:
            raise ValueError("Rasterio window read returned an unexpected shape")
    valid, errors, _warnings = cog_validate(path, strict=True)
    if not valid:
        raise ValueError(f"rio-cogeo validation failed: {errors}")
    _run("gdalinfo", "-json", str(path))
    return [
        _observation("cog", "window-read", "Rasterio", "rasterio-cog", started, args),
        _observation("cog", "structure-validate", "rio-cogeo", "rio-cogeo", started, args),
        _observation("cog", "dataset-read", "GDAL", "gdal-cog", started, args,
                     _command_version("GDAL", "gdalinfo", "--version")),
    ]


def validate_hdf5_netcdf(path: Path, args: argparse.Namespace) -> list[dict]:
    import h5py
    import xarray

    started = _now()
    _run("h5stat", str(path))
    _run("h5dump", "-H", str(path))
    with tempfile.NamedTemporaryFile(suffix=".nc", delete=False) as stream:
        repacked = Path(stream.name)
    try:
        _run("h5repack", str(path), str(repacked))
        with h5py.File(repacked, "r") as handle:
            if "temperature" not in handle or handle["temperature"].size < 1:
                raise ValueError("h5py did not recover the temperature dataset")
    finally:
        repacked.unlink(missing_ok=True)
    with xarray.open_dataset(path, engine="h5netcdf") as dataset:
        if "temperature" not in dataset or dataset["temperature"].size < 1:
            raise ValueError("xarray did not recover the netCDF temperature variable")
        dataset.load()
    tool_version = _command_version("h5stat", "h5stat", "-V")
    return [
        _observation("hdf5-netcdf", "metadata-statistics", "h5stat", "h5stat", started, args, tool_version),
        _observation("hdf5-netcdf", "header-read", "h5dump", "h5dump", started, args, tool_version),
        _observation("hdf5-netcdf", "repack", "h5repack", "h5repack", started, args, tool_version),
        _observation("hdf5-netcdf", "dataset-read", "h5py", "h5py", started, args),
        _observation("hdf5-netcdf", "multidimensional-read", "xarray", "xarray-netcdf", started, args),
    ]


def validate_zarr(path: Path, args: argparse.Namespace) -> list[dict]:
    import dask.array
    import fsspec
    import xarray
    import zarr

    started = _now()
    group = zarr.open_group(path, mode="r")
    if "temperature" not in group or group["temperature"].size < 1:
        raise ValueError("zarr did not recover the temperature array")
    if not fsspec.filesystem("file").exists(str(path / ".zmetadata")):
        raise ValueError("fsspec could not resolve consolidated Zarr metadata")
    with xarray.open_zarr(path, chunks={"time": 1, "lat": 2, "lon": 2}, consolidated=True) as dataset:
        values = dataset["temperature"].data
        if not isinstance(values, dask.array.Array):
            raise ValueError("xarray did not expose a Dask-backed Zarr array")
        if float(values.mean().compute()) <= 0:
            raise ValueError("Dask computed an invalid Zarr aggregate")
    return [
        _observation("zarr", "array-read", "zarr", "zarr-python", started, args),
        _observation("zarr", "multidimensional-subset", "xarray", "xarray-zarr", started, args),
        _observation("zarr", "store-read", "fsspec", "fsspec-zarr", started, args),
        _observation("zarr", "distributed-array-compute", "Dask", "dask-zarr", started, args),
    ]


def validate_stac(base_url: str, args: argparse.Namespace) -> list[dict]:
    from pystac_client import Client

    started = _now()
    collections = list(Client.open(f"{base_url.rstrip('/')}/stac").get_collections())
    if not collections:
        raise ValueError("PySTAC-Client discovered no Honua collections")
    return [_observation("stac", "collection-discovery", "PySTAC-Client", "pystac-client-live", started, args)]


def validate_javascript(path: Path, args: argparse.Namespace) -> list[dict]:
    script = Path(__file__).with_name("validate-js-artifacts.mjs")
    payload = json.loads(_run("node", str(script), str(path)).stdout)
    started = _now()
    return [
        _observation(row["surface"], row["operation"], row["canonical_client"], row["lane"],
                     started, args, row["client_version"])
        for row in payload
    ]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifacts", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--image-digest", required=True)
    parser.add_argument("--candidate-cut-at", required=True)
    parser.add_argument("--fixture-revision", required=True)
    parser.add_argument("--evidence-uri", required=True)
    parser.add_argument("--base-url", required=True)
    args = parser.parse_args()

    observations: list[dict] = []
    observations.extend(validate_geoparquet(args.artifacts / "cng.parquet", args))
    observations.extend(validate_flatgeobuf(args.artifacts / "cng.fgb", args))
    observations.extend(validate_pmtiles(args.artifacts / "honua.pmtiles", args))
    observations.extend(_mark_unbound(validate_cog(args.artifacts / "canonical.cog.tif", args)))
    observations.extend(_mark_unbound(validate_hdf5_netcdf(args.artifacts / "canonical.nc", args)))
    observations.extend(_mark_unbound(validate_zarr(args.artifacts / "canonical.zarr", args)))
    observations.extend(validate_stac(args.base_url, args))
    observations.extend(validate_javascript(args.artifacts, args))
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
    passed = sum(observation["result"] == "pass" for observation in observations)
    skipped = sum(observation["result"] == "skip" for observation in observations)
    print(f"canonical client observations: {passed} pass, {skipped} explicit gap")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
