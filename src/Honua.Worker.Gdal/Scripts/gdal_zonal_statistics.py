#!/usr/bin/env python3
# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
"""Exact statistics from a clipped zone, with bounded memory and no sampling."""
import json
import math
import sys

import numpy as np
from osgeo import gdal


def statistics(path):
    gdal.UseExceptions()
    dataset = gdal.Open(path)
    band = dataset.GetRasterBand(1)
    mask = band.GetMaskBand()
    count = 0
    total = 0.0
    mean = 0.0
    m2 = 0.0
    minimum = math.inf
    maximum = -math.inf
    # Fixed-size windows bound managed arrays even for full-width striped TIFFs.
    for y in range(0, dataset.RasterYSize, 256):
        for x in range(0, dataset.RasterXSize, 256):
            width = min(256, dataset.RasterXSize - x)
            height = min(256, dataset.RasterYSize - y)
            data = band.ReadAsArray(x, y, width, height).astype(np.float64)
            valid = (mask.ReadAsArray(x, y, width, height) != 0) & np.isfinite(data)
            values = data[valid]
            n = values.size
            if n == 0:
                continue
            block_mean = float(values.mean())
            delta = block_mean - mean
            next_count = count + n
            m2 += float(np.square(values - block_mean).sum()) + delta * delta * count * n / next_count
            mean += delta * n / next_count
            count = next_count
            total += float(values.sum())
            minimum = min(minimum, float(values.min()))
            maximum = max(maximum, float(values.max()))
    return {"bands": [{"band": 1, "validCount": int(count), "sum": total,
                       "minimum": minimum if count else None,
                       "maximum": maximum if count else None,
                       "mean": mean if count else None,
                       "stdDev": math.sqrt(m2 / count) if count else None}]}


if __name__ == "__main__":
    print(json.dumps(statistics(sys.argv[1]), allow_nan=False))
