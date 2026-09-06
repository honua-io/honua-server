"""Encode the bounded managed kriging solver's Float64 predictions as GeoTIFF.

No VRT driver is used: production GDAL driver hardening remains intact.
The caller supplies a fixed local raw file and validated grid arguments.
"""
import sys
import numpy as np
from osgeo import gdal, osr

gdal.UseExceptions()
raw, destination = sys.argv[1:3]
width, height, srid = map(int, sys.argv[3:6])
xmin, ymax, dx, dy = map(float, sys.argv[6:10])
values = np.fromfile(raw, dtype="<f8").reshape(height, width)
if not np.isfinite(values).all():
    raise ValueError("kriging predictions must be finite")
srs = osr.SpatialReference()
srs.ImportFromEPSG(srid)
with gdal.GetDriverByName("GTiff").Create(destination, width, height, 1, gdal.GDT_Float64) as raster:
    raster.SetGeoTransform([xmin, dx, 0, ymax, 0, -dy])
    raster.SetProjection(srs.ExportToWkt())
    raster.SetMetadataItem("HONUA_KRIGING_MODEL", "ordinary-linear-zero-nugget-v1")
    raster.GetRasterBand(1).SetNoDataValue(float("nan"))
    raster.GetRasterBand(1).WriteArray(values)
