#!/usr/bin/env python3
# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
"""Resolve the calculator's native default for its requested/inferred output type."""
import json
import sys
from osgeo import gdal
from osgeo_utils.gdal_calc import DefaultNDVLookup

gdal.UseExceptions()
output_type = gdal.GetDataTypeByName(sys.argv[1])
if output_type == gdal.GDT_Unknown:
    for path in sys.argv[2:]:
        dataset = gdal.Open(path)
        output_type = gdal.DataTypeUnion(output_type, dataset.GetRasterBand(1).DataType)
print(json.dumps(DefaultNDVLookup[output_type], allow_nan=False))
