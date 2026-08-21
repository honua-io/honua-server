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
    "go-pmtiles": "1.30.0",
    "3d-tiles-validator": "0.6.1",
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
        "lane": lane,
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


FAILURE_IDENTITIES = {
    "validate_geoparquet": ("geoparquet", "feature-read", "PyArrow"),
    "validate_flatgeobuf": ("flatgeobuf", "feature-read", "Pyogrio"),
    "validate_pmtiles": ("pmtiles", "archive-read", "pmtiles"),
    "validate_cog": ("cog", "window-read", "Rasterio"),
    "validate_hdf5_netcdf": ("hdf5-netcdf", "metadata-statistics", "h5py"),
    "validate_zarr": ("zarr", "multidimensional-subset", "xarray"),
    "validate_stac": ("stac", "asset-discovery", "PySTAC-Client"),
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
                    lane: str, args: argparse.Namespace, check, *, unbound: bool = False) -> None:
    """Collect one client independently so its verdict cannot hide or misattribute siblings."""
    started = _now()
    try:
        detected_version = check()
        observation = _observation(
            surface, operation, client, lane, started, args,
            detected_version if isinstance(detected_version, str) else None,
        )
        if unbound:
            observation["result"] = "skip"
            observation["skip_reason"] = UNBOUND_CONSUMER_GAP
    except Exception as exc:  # evidence must retain the exact client that failed
        observation = _observation(surface, operation, client, lane, started, args, CLIENTS.get(client, "unknown"))
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
        if observation["result"] == "pass":
            observation["result"] = "skip"
            observation["skip_reason"] = UNBOUND_CONSUMER_GAP
    return observations


def validate_geoparquet(path: Path, args: argparse.Namespace) -> list[dict]:
    import geopandas
    import pyarrow.parquet

    observations: list[dict] = []

    def pyarrow_check() -> None:
        table = pyarrow.parquet.read_table(path)
        if table.num_rows < 1:
            raise ValueError("PyArrow read zero GeoParquet rows")
        if b"geo" not in (table.schema.metadata or {}):
            raise ValueError("PyArrow schema has no GeoParquet 'geo' metadata")

    def geopandas_check() -> None:
        frame = geopandas.read_parquet(path)
        if frame.empty or frame.geometry.isna().any() or frame.crs is None:
            raise ValueError("GeoPandas did not recover non-null geometries and CRS")

    def gdal_check() -> str:
        _run("ogrinfo", "-al", "-so", str(path))
        return _command_version("GDAL", "gdalinfo", "--version")

    _collect_client(observations, "geoparquet", "feature-read", "PyArrow", "pyarrow-geoparquet", args, pyarrow_check)
    _collect_client(observations, "geoparquet", "geometry-read", "GeoPandas", "geopandas-geoparquet", args, geopandas_check)
    _collect_client(observations, "geoparquet", "feature-read", "GDAL", "gdal-geoparquet", args, gdal_check)
    return observations


def validate_flatgeobuf(path: Path, args: argparse.Namespace) -> list[dict]:
    import geopandas
    import pyogrio

    observations: list[dict] = []

    def pyogrio_check() -> None:
        frame = pyogrio.read_dataframe(path)
        if frame.empty or frame.geometry.isna().any() or frame.crs is None:
            raise ValueError("Pyogrio did not recover non-null FlatGeobuf geometries and CRS")

    def geopandas_check() -> None:
        frame = geopandas.read_file(path)
        if frame.empty or frame.geometry.isna().any():
            raise ValueError("GeoPandas did not recover FlatGeobuf geometries")

    def gdal_check() -> str:
        _run("ogrinfo", "-al", "-so", str(path))
        return _command_version("GDAL", "gdalinfo", "--version")

    _collect_client(observations, "flatgeobuf", "feature-read", "Pyogrio", "pyogrio-flatgeobuf", args, pyogrio_check)
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

    observations: list[dict] = []

    def rasterio_check() -> None:
        with rasterio.open(path) as dataset:
            if dataset.driver != "GTiff" or dataset.crs is None or dataset.count < 1:
                raise ValueError("Rasterio did not recover a georeferenced COG")
            if dataset.read(1, window=Window(0, 0, 16, 16)).size != 256:
                raise ValueError("Rasterio window read returned an unexpected shape")

    def rio_cogeo_check() -> None:
        valid, errors, _warnings = cog_validate(path, strict=True)
        if not valid:
            raise ValueError(f"rio-cogeo validation failed: {errors}")

    def gdal_check() -> str:
        _run("gdalinfo", "-json", str(path))
        return _command_version("GDAL", "gdalinfo", "--version")

    _collect_client(observations, "cog", "window-read", "Rasterio", "rasterio-cog", args, rasterio_check, unbound=True)
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

    def zarr_check() -> None:
        group = zarr.open_group(path, mode="r")
        if "temperature" not in group or group["temperature"].size < 1:
            raise ValueError("zarr did not recover the temperature array")

    def fsspec_check() -> None:
        if not fsspec.filesystem("file").exists(str(path / ".zmetadata")):
            raise ValueError("fsspec could not resolve consolidated Zarr metadata")

    def xarray_check() -> None:
        with xarray.open_zarr(path, chunks=None, consolidated=True) as dataset:
            if "temperature" not in dataset or dataset["temperature"].size < 1:
                raise ValueError("xarray did not recover the Zarr temperature array")

    def dask_check() -> None:
        with xarray.open_zarr(path, chunks={"time": 1, "lat": 2, "lon": 2}, consolidated=True) as dataset:
            values = dataset["temperature"].data
            if not isinstance(values, dask.array.Array) or float(values.mean().compute()) <= 0:
                raise ValueError("Dask did not compute a valid Zarr aggregate")

    for operation, client, lane, check in (
        ("array-read", "zarr", "zarr-python", zarr_check),
        ("multidimensional-subset", "xarray", "xarray-zarr", xarray_check),
        ("store-read", "fsspec", "fsspec-zarr", fsspec_check),
        ("distributed-array-compute", "Dask", "dask-zarr", dask_check),
    ):
        _collect_client(observations, "zarr", operation, client, lane, args, check, unbound=True)
    return observations


def validate_native_results(path: Path, args: argparse.Namespace) -> list[dict]:
    observations: list[dict] = []

    def pmtiles_check() -> None:
        log = (path / "pmtiles-verify.log").read_text(encoding="utf-8", errors="replace")
        if "Completed verify" not in log:
            raise ValueError("go-pmtiles verification did not record successful completion")

    def tiles3d_check() -> None:
        report = json.loads((path / "3d-tiles-validator.json").read_text(encoding="utf-8"))
        if report.get("numErrors") != 0:
            raise ValueError(f"3D Tiles validator reported {report.get('numErrors')!r} errors")

    _collect_client(observations, "pmtiles", "archive-verify", "go-pmtiles", "go-pmtiles-verify", args, pmtiles_check)
    _collect_client(observations, "3d-tiles", "tileset-content-validate", "3d-tiles-validator", "3d-tiles-validator", args, tiles3d_check)
    return observations


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
    parser.add_argument("--native-results", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--image-digest", required=True)
    parser.add_argument("--candidate-cut-at", required=True)
    parser.add_argument("--fixture-revision", required=True)
    parser.add_argument("--evidence-uri", required=True)
    parser.add_argument("--base-url", required=True)
    args = parser.parse_args()

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
    return 1 if any(observation["result"] == "fail" for observation in observations) else 0


if __name__ == "__main__":
    raise SystemExit(main())
