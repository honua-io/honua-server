# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
"""Run the real GDAL proximity CLI and declare its output nodata sentinel.

gdal_proximity.py's -nodata option controls ComputeProximity's fill value but
does not set the output band's nodata metadata. Keep the calculation in GDAL
and persist the sentinel before the executor publishes the GeoTIFF.
"""
import subprocess
import sys

from osgeo import gdal


def main(args: list[str]) -> int:
    result = subprocess.run(["gdal_proximity.py", *args], check=False)
    if result.returncode != 0:
        return result.returncode

    gdal.UseExceptions()
    dataset = gdal.Open(args[-1], gdal.GA_Update)
    dataset.GetRasterBand(1).SetNoDataValue(float(args[args.index("-nodata") + 1]))
    dataset.FlushCache()
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
