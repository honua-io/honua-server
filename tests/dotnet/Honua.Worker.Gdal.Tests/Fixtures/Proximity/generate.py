# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
"""Regenerate only the committed input; never derive expected proximity outputs."""
from pathlib import Path

import numpy as np
from osgeo import gdal, osr

gdal.UseExceptions()
grid = np.zeros((5, 7), dtype=np.int16)
grid[1, 1] = 7
grid[3, 5] = 23
grid[4, 0] = -9999
ds = gdal.GetDriverByName("GTiff").Create(
    str(Path(__file__).with_name("proximity-sources.tif")), 7, 5, 1, gdal.GDT_Int16
)
ds.SetGeoTransform((500000, 10, 0, 2200000, 0, -10))
srs = osr.SpatialReference()
srs.ImportFromEPSG(32604)
ds.SetProjection(srs.ExportToWkt())
ds.GetRasterBand(1).SetNoDataValue(-9999)
ds.GetRasterBand(1).WriteArray(grid)
ds.FlushCache()
ds = None
