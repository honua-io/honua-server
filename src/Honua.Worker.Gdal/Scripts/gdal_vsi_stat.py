#!/usr/bin/env python3
# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.
"""Report the byte size of a GDAL VSI object without downloading its payload."""
import sys

from osgeo import gdal

MIN_ARGS = 2
gdal.UseExceptions()
args = gdal.GeneralCmdLineProcessor(sys.argv)
if len(args) < MIN_ARGS:
    sys.exit(MIN_ARGS)
stat = gdal.VSIStatL(args[1], gdal.VSI_STAT_EXISTS_FLAG | gdal.VSI_STAT_SIZE_FLAG)
if stat is None or stat.size < 0:
    sys.exit(1)
print(stat.size)
