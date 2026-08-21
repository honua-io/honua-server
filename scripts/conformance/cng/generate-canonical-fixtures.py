#!/usr/bin/env python3
"""Generate deterministic cloud-native fixtures for canonical client checks."""
from __future__ import annotations

import argparse
import shutil
from pathlib import Path

import numpy as np
import rasterio
import xarray as xr
from rasterio.transform import from_origin
from rio_cogeo.cogeo import cog_translate
from rio_cogeo.profiles import cog_profiles


def generate_cog(output: Path) -> None:
    source = output.with_suffix(".source.tif")
    pixels = np.arange(256 * 256, dtype=np.uint16).reshape(256, 256)
    with rasterio.open(
        source,
        "w",
        driver="GTiff",
        width=256,
        height=256,
        count=1,
        dtype=pixels.dtype,
        crs="EPSG:4326",
        transform=from_origin(-180.0, 90.0, 360.0 / 256, 180.0 / 256),
        tiled=True,
        blockxsize=128,
        blockysize=128,
    ) as dataset:
        dataset.write(pixels, 1)
    cog_translate(source, output, cog_profiles.get("deflate"), quiet=True)
    source.unlink()


def canonical_dataset() -> xr.Dataset:
    return xr.Dataset(
        data_vars={
            "temperature": (
                ("time", "lat", "lon"),
                np.arange(2 * 4 * 4, dtype=np.float32).reshape(2, 4, 4),
                {"units": "degC", "standard_name": "sea_surface_temperature"},
            )
        },
        coords={
            "time": np.array(["2026-01-01", "2026-01-02"], dtype="datetime64[ns]"),
            "lat": np.linspace(18.0, 22.0, 4),
            "lon": np.linspace(-160.0, -156.0, 4),
        },
        attrs={"Conventions": "CF-1.10", "title": "Honua canonical multidimensional fixture"},
    )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)

    generate_cog(args.output / "canonical.cog.tif")
    dataset = canonical_dataset()
    dataset.to_netcdf(args.output / "canonical.nc", engine="h5netcdf")

    zarr_path = args.output / "canonical.zarr"
    if zarr_path.exists():
        shutil.rmtree(zarr_path)
    dataset.chunk({"time": 1, "lat": 2, "lon": 2}).to_zarr(
        zarr_path,
        mode="w",
        consolidated=True,
        zarr_format=2,
    )
    print(f"generated canonical fixtures in {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
