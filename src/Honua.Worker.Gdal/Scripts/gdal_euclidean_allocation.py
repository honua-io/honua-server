#!/usr/bin/env python3
# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
"""Euclidean allocation worker step (honua-server #2255).

Computes the nearest-source *allocation* raster that is the companion of the
Euclidean-distance/proximity raster: for every cell it outputs the VALUE (id)
of the nearest source cell, i.e. a discrete Voronoi tessellation keyed by the
source pixel values.

Stock GDAL ``gdal_proximity.py`` computes distance only and has NO nearest-source
allocation mode, so ``proximity.euclidean-allocation`` is implemented here as a
small custom worker step layered on the GDAL Python bindings (raster I/O,
geotransform/CRS preservation) plus SciPy's exact Euclidean distance transform
with nearest-feature index return.

CLI contract (invoked by ``GdalProximityJobExecutor`` via ``python3``):

    gdal_euclidean_allocation.py SRC DST
        [--band N] [--dist-units GEO|PIXEL]
        [--max-distance D] [--values v1,v2,...] [--http-if-match ETAG]

The output GeoTIFF preserves the source extent, cell size, CRS and band data
type; allocated values are the source pixel values. Cells whose nearest source
is farther than ``--max-distance`` (when supplied) take the nodata value.
"""

from __future__ import annotations

import argparse
import sys
from typing import Optional, Sequence

import numpy as np
from scipy import ndimage


def build_source_mask(
    arr: np.ndarray,
    nodata: Optional[float],
    values: Optional[Sequence[float]],
) -> np.ndarray:
    """Return the boolean mask of cells that are proximity *sources*.

    A source is any non-zero cell that is not the nodata value. When ``values``
    is supplied, only cells whose value is in that set are treated as sources
    (mirroring ``gdal_proximity``'s ``-values`` behavior).
    """
    mask = arr != 0
    if nodata is not None and not (isinstance(nodata, float) and np.isnan(nodata)):
        mask &= arr != nodata
    if values:
        mask &= np.isin(arr, np.asarray(list(values), dtype=arr.dtype if np.issubdtype(arr.dtype, np.integer) else float))
    return mask


def compute_allocation(
    arr: np.ndarray,
    source_mask: np.ndarray,
    sampling: tuple[float, float],
    max_distance: Optional[float],
    out_nodata: float,
) -> np.ndarray:
    """Compute the nearest-source allocation for every cell.

    ``scipy.ndimage.distance_transform_edt`` measures, for each non-zero
    (foreground) cell, the distance/index to the nearest zero (background) cell.
    We therefore feed it the COMPLEMENT of the source mask so the "background"
    is the source set: every cell then resolves to the indices of its nearest
    source, and the allocation is the source array sampled at those indices.

    ``sampling`` is the per-axis cell spacing ``(y, x)`` so GEO-unit distances
    honor anisotropic pixels; pass ``(1.0, 1.0)`` for PIXEL units.
    """
    background = ~source_mask
    distance, indices = ndimage.distance_transform_edt(
        background, sampling=sampling, return_indices=True
    )
    alloc = arr[tuple(indices)]

    if max_distance is not None:
        alloc = np.where(distance <= max_distance, alloc, np.asarray(out_nodata, dtype=arr.dtype))

    return alloc.astype(arr.dtype, copy=False)


def _read_source(src: str, band_index: int, http_if_match: Optional[str] = None):
    from osgeo import gdal  # Imported lazily so the pure algorithm stays GDAL-free.

    gdal.UseExceptions()
    if http_if_match:
        gdal.SetConfigOption("GDAL_HTTP_HEADERS", f"If-Match: {http_if_match}")
    dataset = gdal.Open(src, gdal.GA_ReadOnly)
    if dataset is None:
        raise RuntimeError(f"could not open source raster '{src}'")
    band = dataset.GetRasterBand(band_index)
    if band is None:
        raise RuntimeError(f"source raster has no band {band_index}")
    arr = band.ReadAsArray()
    if arr is None:
        raise RuntimeError("could not read source band data")
    return dataset, band, arr


def _write_output(dst: str, template_dataset, data_type: int, alloc: np.ndarray, out_nodata: float) -> None:
    from osgeo import gdal

    driver = gdal.GetDriverByName("GTiff")
    out = driver.Create(dst, template_dataset.RasterXSize, template_dataset.RasterYSize, 1, data_type)
    if out is None:
        raise RuntimeError(f"could not create output raster '{dst}'")
    out.SetGeoTransform(template_dataset.GetGeoTransform())
    projection = template_dataset.GetProjection()
    if projection:
        out.SetProjection(projection)
    out_band = out.GetRasterBand(1)
    out_band.WriteArray(alloc)
    out_band.SetNoDataValue(float(out_nodata))
    out_band.FlushCache()
    out.FlushCache()


def main(argv: Sequence[str]) -> int:
    parser = argparse.ArgumentParser(description="Euclidean allocation (nearest-source) raster.")
    parser.add_argument("src", help="Source GeoTIFF path.")
    parser.add_argument("dst", help="Destination GeoTIFF path.")
    parser.add_argument("--band", type=int, default=1, help="1-based source band index (default: 1).")
    parser.add_argument("--dist-units", choices=["GEO", "PIXEL"], default="GEO", help="Distance units for --max-distance.")
    parser.add_argument("--max-distance", type=float, default=None, help="Optional maximum allocation distance.")
    parser.add_argument("--values", default=None, help="Optional comma-separated source pixel values to allocate from.")
    parser.add_argument("--http-if-match", default=None, help="Conditional object-store ETag pin.")
    args = parser.parse_args(argv)

    values: Optional[list[float]] = None
    if args.values:
        values = [float(v) for v in args.values.split(",") if v.strip() != ""]

    try:
        dataset, band, arr = _read_source(args.src, args.band, args.http_if_match)
    except RuntimeError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2

    nodata = band.GetNoDataValue()
    source_mask = build_source_mask(arr, nodata, values)
    if not source_mask.any():
        print("error: source raster has no source cells to allocate from", file=sys.stderr)
        return 3

    geotransform = dataset.GetGeoTransform()
    pixel_w = abs(geotransform[1]) if geotransform else 1.0
    pixel_h = abs(geotransform[5]) if geotransform else 1.0
    sampling = (pixel_h, pixel_w) if args.dist_units == "GEO" else (1.0, 1.0)

    out_nodata = nodata if nodata is not None else 0
    alloc = compute_allocation(arr, source_mask, sampling, args.max_distance, out_nodata)

    try:
        _write_output(args.dst, dataset, band.DataType, alloc, out_nodata)
    except RuntimeError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 4

    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
