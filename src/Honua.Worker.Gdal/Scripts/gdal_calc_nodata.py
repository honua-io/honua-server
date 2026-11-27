#!/usr/bin/env python3
# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.
"""Resolve the calculator's native default for its requested/inferred output type."""
import json
import sys
from osgeo import gdal
from osgeo_utils.gdal_calc import DefaultNDVLookup

DEFAULT_ALLOWED_DRIVERS = ["GTiff", "PNG", "JPEG"]
ALLOWED_DRIVERS_PREFIX = "--allowed-drivers="


def parse_args(argv):
    allowed_drivers = list(DEFAULT_ALLOWED_DRIVERS)
    positional = []
    for token in argv:
        if token.startswith(ALLOWED_DRIVERS_PREFIX):
            parsed = [d.strip() for d in token[len(ALLOWED_DRIVERS_PREFIX):].split(",") if d.strip()]
            if not parsed:
                raise ValueError("--allowed-drivers must specify at least one GDAL driver")
            allowed_drivers = parsed
        else:
            positional.append(token)
    return allowed_drivers, positional


gdal.UseExceptions()
allowed_drivers, positional = parse_args(sys.argv[1:])
output_type = gdal.GetDataTypeByName(positional[0])
if output_type == gdal.GDT_Unknown:
    for path in positional[1:]:
        dataset = gdal.OpenEx(path, gdal.OF_RASTER | gdal.OF_READONLY, allowed_drivers=allowed_drivers)
        if dataset is None:
            raise RuntimeError(f"could not open raster '{path}' with allowed drivers {allowed_drivers}")
        output_type = gdal.DataTypeUnion(output_type, dataset.GetRasterBand(1).DataType)
print(json.dumps(DefaultNDVLookup[output_type], allow_nan=False))
