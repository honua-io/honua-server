#!/usr/bin/env python3
# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
"""Compute exact counts and moments in one bounded GDAL raster scan."""
import json
import sys

from osgeo import gdal
from gdal_zonal_statistics import band_statistics

gdal.UseExceptions()
# Honor the same conditional VSI read configuration as metadata-only gdalinfo.
args = gdal.GeneralCmdLineProcessor(sys.argv)
dataset = gdal.Open(args[1])
bands = [band_statistics(dataset, index) for index in range(1, dataset.RasterCount + 1)]
print(json.dumps({"bands": bands}, allow_nan=False))
