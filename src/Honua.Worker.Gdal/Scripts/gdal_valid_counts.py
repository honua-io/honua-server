#!/usr/bin/env python3
# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
"""Count non-nodata samples exactly; gdalinfo valid percentages are rounded."""
import json
import math
import sys

import numpy as np
from osgeo import gdal

gdal.UseExceptions()
# Honor the same conditional VSI read configuration as the preceding gdalinfo.
args = gdal.GeneralCmdLineProcessor(sys.argv)
dataset = gdal.Open(args[1])
bands = []
for index in range(1, dataset.RasterCount + 1):
    band = dataset.GetRasterBand(index)
    nodata = band.GetNoDataValue()
    count = 0
    for y in range(0, dataset.RasterYSize, 256):
        for x in range(0, dataset.RasterXSize, 256):
            data = band.ReadAsArray(x, y, min(256, dataset.RasterXSize - x), min(256, dataset.RasterYSize - y))
            # Match GDAL statistics: NaN and the band sentinel are excluded.
            # Explicit dataset masks are not used by GDAL ComputeStatistics.
            valid = ~np.isnan(data)
            if nodata is not None and not math.isnan(nodata):
                valid &= data != nodata
            count += int(np.count_nonzero(valid))
    bands.append({"band": index, "validCount": count})
print(json.dumps({"bands": bands}, allow_nan=False))
