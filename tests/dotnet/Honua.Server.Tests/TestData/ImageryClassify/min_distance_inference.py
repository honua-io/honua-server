# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
"""Minimum-distance-to-mean inference program run by the proof's model server.

This is the MODEL, not a stub: it reads the posted GeoTIFF with GDAL, computes a
per-pixel nearest-centroid classification with NumPy against a committed model
file, and writes a single-band classification GeoTIFF that carries the source
grid and CRS forward. Honua never sees this code path at runtime - the
delegated-inference contract puts the model behind an HTTP endpoint - so the
proof's backend runs it in the pinned production GDAL image exactly the way a
deployment's own model server would run its model.

    python3 min_distance_inference.py <model.json> <scene.tif> <classified.tif>
"""
import json
import sys

import numpy as np
from osgeo import gdal


def main(argv: list[str]) -> int:
    gdal.UseExceptions()
    model_path, source_path, output_path = argv

    with open(model_path, encoding="utf-8") as handle:
        model = json.load(handle)
    centroids = np.array([entry["centroid"] for entry in model["classes"]], dtype=np.float64)
    class_ids = np.array([entry["id"] for entry in model["classes"]], dtype=np.uint8)

    source = gdal.Open(source_path)
    if source.RasterCount < centroids.shape[1]:
        raise ValueError(
            f"the scene has {source.RasterCount} bands; the model needs {centroids.shape[1]}")

    stack = np.stack(
        [source.GetRasterBand(index + 1).ReadAsArray().astype(np.float64)
         for index in range(centroids.shape[1])],
        axis=-1)

    # Squared Euclidean distance to every class mean, then the arg-min class id.
    distances = ((stack[..., None, :] - centroids[None, None, :, :]) ** 2).sum(axis=-1)
    classified = class_ids[distances.argmin(axis=-1)]

    output = gdal.GetDriverByName("GTiff").Create(
        output_path, source.RasterXSize, source.RasterYSize, 1, gdal.GDT_Byte)
    output.SetGeoTransform(source.GetGeoTransform())
    output.SetProjection(source.GetProjection())
    output.GetRasterBand(1).WriteArray(classified)
    output.FlushCache()
    del output

    json.dump({"model": model["modelId"], "pixels": int(classified.size)}, sys.stdout)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
