#!/usr/bin/env python3
"""Generate deterministic cloud-native fixtures for canonical client checks.

These fixtures are third-party *inputs*, not evidence. `canonical.webmercator.cog.tif`
and `canonical.zarr` are the objects Honua's own COG and Zarr readers consume over
HTTP range requests in the artifact generator; the canonical clients then validate
what **Honua** emitted from them (#4398). Every sample value follows a declared
formula so the expected result can be recomputed independently of this script.
"""
from __future__ import annotations

import argparse
import shutil
from pathlib import Path

import numcodecs
import numpy as np
import rasterio
import xarray as xr
from rasterio.transform import from_origin
from rio_cogeo.cogeo import cog_translate
from rio_cogeo.profiles import cog_profiles

# Web Mercator world extent, so a tile carved out of the base level is a real
# EPSG:3857 tile rather than a 4326 grid relabelled as one.
WEB_MERCATOR_HALF_SPAN = 20037508.342789244

# canonical.webmercator.cog.tif sample formula: value(row, col) = row * 512 + col,
# with a single nodata cell at (3, 7). Honua transcodes the north-west 256x256
# base-level tile, so the transcoded tile carries exactly value(row, col) for
# 0 <= row, col < 256.
WEB_MERCATOR_SIZE = 512
WEB_MERCATOR_BLOCK = 256
NODATA = -9999.0
NODATA_ROW, NODATA_COL = 3, 7


def generate_web_mercator_cog(output: Path) -> None:
    """Writes the EPSG:3857 COG Honua's tile reader consumes and transcodes.

    Honua's GeoTIFF tile encoder emits Web Mercator tiles, so the source has to be
    Web Mercator for the transcode to be honestly georeferenced. The block size
    matches the tile size the encoder writes, so base-level tile 0 is exactly the
    north-west quadrant.
    """
    source = output.with_suffix(".source.tif")
    pixels = (
        np.arange(WEB_MERCATOR_SIZE * WEB_MERCATOR_SIZE, dtype=np.float32)
        .reshape(WEB_MERCATOR_SIZE, WEB_MERCATOR_SIZE)
    )
    pixels[NODATA_ROW, NODATA_COL] = NODATA
    resolution = (2 * WEB_MERCATOR_HALF_SPAN) / WEB_MERCATOR_SIZE
    with rasterio.open(
        source,
        "w",
        driver="GTiff",
        width=WEB_MERCATOR_SIZE,
        height=WEB_MERCATOR_SIZE,
        count=1,
        dtype=pixels.dtype,
        crs="EPSG:3857",
        transform=from_origin(-WEB_MERCATOR_HALF_SPAN, WEB_MERCATOR_HALF_SPAN, resolution, resolution),
        nodata=NODATA,
        tiled=True,
        blockxsize=WEB_MERCATOR_BLOCK,
        blockysize=WEB_MERCATOR_BLOCK,
    ) as dataset:
        dataset.write(pixels, 1)
    profile = cog_profiles.get("deflate")
    profile.update(blockxsize=WEB_MERCATOR_BLOCK, blockysize=WEB_MERCATOR_BLOCK)
    cog_translate(source, output, profile, overview_level=1, quiet=True)
    source.unlink()


def canonical_dataset() -> xr.Dataset:
    return xr.Dataset(
        data_vars={
            "temperature": (
                ("time", "y", "x"),
                np.arange(4 * 8 * 16, dtype=np.float32).reshape(4, 8, 16),
                {"units": "degC", "standard_name": "sea_surface_temperature"},
            )
        },
        coords={
            "time": np.array(
                ["2026-01-01", "2026-01-02", "2026-01-03", "2026-01-04"],
                dtype="datetime64[ns]",
            ),
            "y": (
                "y",
                np.linspace(22.0, 18.0, 8),
                {"standard_name": "latitude", "units": "degrees_north", "axis": "Y"},
            ),
            "x": (
                "x",
                np.linspace(-160.0, -156.0, 16),
                {"standard_name": "longitude", "units": "degrees_east", "axis": "X"},
            ),
        },
        attrs={"Conventions": "CF-1.10", "title": "Honua canonical multidimensional fixture"},
    )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)

    generate_web_mercator_cog(args.output / "canonical.webmercator.cog.tif")
    dataset = canonical_dataset()
    dataset.to_netcdf(
        args.output / "canonical.nc",
        engine="h5netcdf",
        encoding={"temperature": {"chunksizes": (1, 4, 4), "dtype": "float32"}},
    )

    zarr_path = args.output / "canonical.zarr"
    if zarr_path.exists():
        shutil.rmtree(zarr_path)
    # zlib rather than the zarr default: Honua's AOT-safe Zarr subset reader
    # implements the uncompressed and zlib/gzip codecs, and a fixture it cannot
    # decode would leave the COG/Zarr cells validating third-party output again.
    dataset.chunk({"time": 1, "y": 4, "x": 4}).to_zarr(
        zarr_path,
        mode="w",
        consolidated=True,
        zarr_format=2,
        encoding={
            name: {"compressor": numcodecs.Zlib(level=1)}
            for name in (*dataset.data_vars, *dataset.coords)
        },
    )
    print(f"generated canonical fixtures in {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
