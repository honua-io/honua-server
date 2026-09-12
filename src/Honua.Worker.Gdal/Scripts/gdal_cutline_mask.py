#!/usr/bin/env python3
# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
"""Publish warp validity as an internal TIFF mask, preserving the data bands."""
import sys
from osgeo import gdal

gdal.UseExceptions()
gdal.SetConfigOption("GDAL_TIFF_INTERNAL_MASK", "YES")
source = gdal.Open(sys.argv[1])
alpha = source.RasterCount
if source.GetRasterBand(alpha).GetColorInterpretation() != gdal.GCI_AlphaBand:
    raise ValueError("Clipped raster has no alpha validity band")
# A Float32 alpha band is not exposed by GetMaskBand. Convert it to the
# standard byte mask inside the TIFF; an external .msk would be lost on publish.
output = gdal.Translate(sys.argv[2], source, format="GTiff",
                        bandList=list(range(1, alpha)), maskBand=alpha)
output.Close()
source.Close()
