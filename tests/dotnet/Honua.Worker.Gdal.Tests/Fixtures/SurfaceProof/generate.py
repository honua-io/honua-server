"""Deterministic source DEMs only; expected results are derived separately in C#."""
from pathlib import Path
import numpy as np
from osgeo import gdal, osr

gdal.UseExceptions()
root = Path(__file__).parent

def raster(name, values, cell=2, nodata=-9999):
    data = np.array(values, dtype=np.float32)
    ds = gdal.GetDriverByName('GTiff').Create(str(root / name), data.shape[1], data.shape[0], 1, gdal.GDT_Float32)
    srs = osr.SpatialReference()
    srs.ImportFromEPSG(3857)
    ds.SetProjection(srs.ExportToWkt())
    ds.SetGeoTransform((1000, cell, 0, 2000, 0, -cell))
    band = ds.GetRasterBand(1)
    band.WriteArray(data)
    if nodata is not None:
        band.SetNoDataValue(nodata)
    ds.Close()

for name, dx, dr in [('plane', 2, 3), ('east', 2, 0), ('north', 0, -2), ('flat', 0, 0)]:
    data = [[100 + dx*c + dr*r for c in range(5)] for r in range(5)]
    raster(name + '.tif', data)
    if name == 'plane':
        data[0][0] = -9999
        raster('plane-hole.tif', data)

peak = [[2 for c in range(5)] for r in range(5)]
peak[2][2] = 12
peak[1][1] = -4
raster('peak-depression.tif', peak)
peak[0][0] = -9999
raster('peak-depression-hole.tif', peak)
raster('ridge-shade.tif', [[2 * min(c, 8-c) for c in range(9)] for r in range(5)])
raster('ridge-visibility.tif', [[10 if c == 3 else 0 for c in range(7)] for r in range(7)], cell=1, nodata=None)
raster('ramp.tif', [[10*c for c in range(5)] for r in range(5)])
