# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
"""Ordinary kriging over a point layer, solved with the bundled NumPy backend.

Stock GDAL has no kriging algorithm: ``gdal_grid`` offers inverse distance,
moving average and nearest neighbour only. The worker image does, however,
bundle NumPy alongside the GDAL Python bindings, and ordinary kriging is a
linear solve, so this script is the kriging-capable numerical backend the
``raster.interpolate-kriging`` catalog operation routes to. GDAL still owns
every I/O boundary — OGR reads the point layer and its CRS, and the GTiff
driver writes the georeferenced result.

Ordinary kriging, exactly as defined by the normal equations. For a prediction
location ``x0`` and ``n`` samples ``x_i`` the weights ``w`` and the Lagrange
multiplier ``mu`` solve::

    [ G  1 ] [ w  ]   [ g0 ]
    [ 1' 0 ] [ mu ] = [ 1  ]

with ``G[i][j] = gamma(|x_i - x_j|)`` and ``g0[i] = gamma(|x_i - x0|)``. The
prediction is ``sum(w_i * z_i)`` and the kriging variance is
``sum(w_i * g0_i) + mu``; band two publishes its square root, the kriging
standard error, so callers can qualify a prediction rather than trust it
blindly.

``gamma(0) = 0`` by definition even when the model carries a nugget, which is
what makes ordinary kriging an exact interpolator: at a sample location the
prediction reproduces the sample and the kriging variance is zero.

Grid convention matches ``gdal_grid``: the requested extent is divided into
``width x height`` cells and the surface is evaluated at each cell CENTRE, so
the geotransform origin sits on the extent corner.
"""
import argparse
import json
import sys

import numpy as np
from osgeo import gdal, ogr


def semivariogram(model: str, h: np.ndarray, nugget: float, sill: float, rng: float) -> np.ndarray:
    """Isotropic semivariogram gamma(h). ``sill`` is the TOTAL sill (nugget included)."""
    partial = sill - nugget
    ratio = h / rng
    if model == "spherical":
        structured = np.where(h >= rng, 1.0, 1.5 * ratio - 0.5 * ratio**3)
    elif model == "exponential":
        # Practical-range convention: gamma reaches 95% of the sill at h = rng.
        structured = 1.0 - np.exp(-3.0 * ratio)
    elif model == "gaussian":
        structured = 1.0 - np.exp(-3.0 * ratio**2)
    else:
        raise ValueError(f"unsupported variogram model '{model}'")

    gamma = nugget + partial * structured
    # gamma(0) = 0 exactly: the nugget is the limit as h -> 0+, not the value AT
    # zero. Ordinary kriging is only an exact interpolator when this holds.
    return np.where(h == 0.0, 0.0, gamma)


def read_points(path: str, z_field: str | None):
    gdal.UseExceptions()
    source = ogr.Open(path)
    if source is None:
        raise ValueError("the point layer could not be opened")
    layer = source.GetLayer(0)
    spatial_reference = layer.GetSpatialRef()
    wkt = spatial_reference.ExportToWkt() if spatial_reference is not None else ""

    xs: list[float] = []
    ys: list[float] = []
    zs: list[float] = []
    for feature in layer:
        geometry = feature.GetGeometryRef()
        if geometry is None or geometry.GetGeometryType() not in (ogr.wkbPoint, ogr.wkbPoint25D):
            raise ValueError("every input feature must carry a point geometry")
        xs.append(geometry.GetX())
        ys.append(geometry.GetY())
        if z_field:
            value = feature.GetField(z_field)
            if value is None:
                raise ValueError(f"field '{z_field}' is null on at least one feature")
            zs.append(float(value))
        else:
            zs.append(geometry.GetZ())

    extent = layer.GetExtent()
    return np.array(xs), np.array(ys), np.array(zs), wkt, extent


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Ordinary kriging onto a raster grid.")
    parser.add_argument("--points", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--z-field", default="")
    parser.add_argument("--width", type=int, required=True)
    parser.add_argument("--height", type=int, required=True)
    parser.add_argument("--model", default="spherical")
    parser.add_argument("--nugget", type=float, default=0.0)
    parser.add_argument("--sill", type=float, default=1.0)
    parser.add_argument("--range", dest="rng", type=float, required=True)
    parser.add_argument("--nodata", default="nan")
    parser.add_argument("--max-samples", dest="max_samples", type=int, default=2000)
    args = parser.parse_args(argv)

    if args.width <= 0 or args.height <= 0:
        raise ValueError("width and height must be positive")
    if args.rng <= 0:
        raise ValueError("the variogram range must be positive")
    if args.nugget < 0 or args.sill <= args.nugget:
        raise ValueError("the variogram needs 0 <= nugget < sill")

    xs, ys, zs, wkt, extent = read_points(args.points, args.z_field or None)
    if xs.size < 2:
        raise ValueError("ordinary kriging needs at least two sample points")
    if xs.size > args.max_samples:
        raise ValueError(
            f"the point layer carries {xs.size} samples; the dense kriging solve is capped at "
            f"{args.max_samples}"
        )

    x_min, x_max, y_min, y_max = extent
    if not (x_max > x_min and y_max > y_min):
        raise ValueError("the sample points must span a non-degenerate extent")

    # Pairwise sample distances and the ordinary-kriging left-hand side.
    dx = xs[:, None] - xs[None, :]
    dy = ys[:, None] - ys[None, :]
    distances = np.hypot(dx, dy)
    if np.any((distances == 0.0) & ~np.eye(xs.size, dtype=bool)):
        raise ValueError("two samples share a location; deduplicate before kriging")

    n = xs.size
    lhs = np.ones((n + 1, n + 1))
    lhs[:n, :n] = semivariogram(args.model, distances, args.nugget, args.sill, args.rng)
    lhs[n, n] = 0.0

    delta_x = (x_max - x_min) / args.width
    delta_y = (y_max - y_min) / args.height
    grid_x = x_min + (np.arange(args.width) + 0.5) * delta_x
    grid_y = y_max - (np.arange(args.height) + 0.5) * delta_y
    mesh_x, mesh_y = np.meshgrid(grid_x, grid_y)
    targets = mesh_x.size

    target_distances = np.hypot(
        xs[:, None] - mesh_x.reshape(1, targets),
        ys[:, None] - mesh_y.reshape(1, targets),
    )
    rhs = np.ones((n + 1, targets))
    rhs[:n, :] = semivariogram(args.model, target_distances, args.nugget, args.sill, args.rng)

    solution = np.linalg.solve(lhs, rhs)
    weights = solution[:n, :]
    lagrange = solution[n, :]

    prediction = (weights * zs[:, None]).sum(axis=0).reshape(args.height, args.width)
    variance = (weights * rhs[:n, :]).sum(axis=0) + lagrange
    # Round-off can drive an exactly-zero variance a few ulps negative.
    standard_error = np.sqrt(np.clip(variance, 0.0, None)).reshape(args.height, args.width)

    nodata = float("nan") if args.nodata.lower() == "nan" else float(args.nodata)

    driver = gdal.GetDriverByName("GTiff")
    dataset = driver.Create(args.output, args.width, args.height, 2, gdal.GDT_Float64)
    dataset.SetGeoTransform((x_min, delta_x, 0.0, y_max, 0.0, -delta_y))
    if wkt:
        dataset.SetProjection(wkt)
    for index, (values, name) in enumerate(
        ((prediction, "prediction"), (standard_error, "kriging_standard_error")), start=1
    ):
        band = dataset.GetRasterBand(index)
        band.SetNoDataValue(nodata)
        band.SetDescription(name)
        band.WriteArray(values)
    dataset.FlushCache()
    dataset = None

    json.dump({"samples": int(n), "width": args.width, "height": args.height}, sys.stdout)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv[1:]))
    except Exception as error:  # noqa: BLE001 - surfaced as the tool's stderr
        print(str(error), file=sys.stderr)
        sys.exit(2)
