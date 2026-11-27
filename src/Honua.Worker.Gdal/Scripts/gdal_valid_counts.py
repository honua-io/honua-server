#!/usr/bin/env python3
# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.
"""Compute exact counts and moments in one bounded GDAL raster scan."""
import json
import sys

from osgeo import gdal
from gdal_zonal_statistics import band_statistics

DEFAULT_ALLOWED_DRIVERS = ["GTiff", "PNG", "JPEG"]
ALLOWED_DRIVERS_PREFIX = "--allowed-drivers="


def extract_allowed_drivers(argv):
    allowed_drivers = list(DEFAULT_ALLOWED_DRIVERS)
    remaining = []
    for token in argv:
        if token.startswith(ALLOWED_DRIVERS_PREFIX):
            parsed = [d.strip() for d in token[len(ALLOWED_DRIVERS_PREFIX):].split(",") if d.strip()]
            if not parsed:
                raise ValueError("--allowed-drivers must specify at least one GDAL driver")
            allowed_drivers = parsed
        else:
            remaining.append(token)
    return allowed_drivers, remaining


gdal.UseExceptions()
# Honor the same conditional VSI read configuration as metadata-only gdalinfo.
allowed_drivers, raw_args = extract_allowed_drivers(sys.argv)
args = gdal.GeneralCmdLineProcessor(raw_args)
dataset = gdal.OpenEx(args[1], gdal.OF_RASTER | gdal.OF_READONLY, allowed_drivers=allowed_drivers)
if dataset is None:
    raise RuntimeError(f"could not open raster '{args[1]}' with allowed drivers {allowed_drivers}")
bands = [band_statistics(dataset, index) for index in range(1, dataset.RasterCount + 1)]
print(json.dumps({"bands": bands}, allow_nan=False))
