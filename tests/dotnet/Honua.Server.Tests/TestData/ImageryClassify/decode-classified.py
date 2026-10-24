# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.
"""Decode a raster to JSON so the proof asserts pixels, not bytes."""
import json
import sys

from osgeo import gdal


def main(argv: list[str]) -> int:
    gdal.UseExceptions()
    dataset = gdal.Open(argv[0])
    payload = {
        "width": dataset.RasterXSize,
        "height": dataset.RasterYSize,
        "bands": dataset.RasterCount,
        "transform": list(dataset.GetGeoTransform()),
        "epsg": None,
        "values": [],
    }
    reference = dataset.GetSpatialRef()
    if reference is not None:
        code = reference.GetAuthorityCode(None)
        payload["epsg"] = int(code) if code else None
    for index in range(dataset.RasterCount):
        band = dataset.GetRasterBand(index + 1)
        payload["values"].append([int(value) for value in band.ReadAsArray().ravel()])
    json.dump(payload, sys.stdout)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
