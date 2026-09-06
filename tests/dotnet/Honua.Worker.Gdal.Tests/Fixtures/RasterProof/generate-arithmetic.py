"""Generate analytical inputs only; expected results are derived in the C# tests."""
from pathlib import Path
import numpy as np
from osgeo import gdal, osr

gdal.UseExceptions()
root = Path(__file__).parent


def raster(name, bands, nodata=-9999):
    data = np.array(bands, dtype=np.float32)
    ds = gdal.GetDriverByName("GTiff").Create(str(root / name), data.shape[2], data.shape[1], len(data), gdal.GDT_Float32, options=["COMPRESS=DEFLATE"])
    srs = osr.SpatialReference()
    srs.ImportFromEPSG(4326)
    ds.SetProjection(srs.ExportToWkt())
    ds.SetGeoTransform((10, 0.25, 0, 20, 0, -0.5))
    for index, values in enumerate(data, 1):
        band = ds.GetRasterBand(index)
        band.WriteArray(values)
        if nodata is not None:
            band.SetNoDataValue(nodata)
    ds.Close()


raster("reclassify.tif", [[[-2, 0, 1, 2], [3, 4, 5, 6], [9, 10, -9999, 11]]])
raster("algebra-a.tif", [[[2, 4, 0, -9999], [6, -2, 8, 0]]])
raster("algebra-b.tif", [[[1, 0, 0, 2], [-9999, -4, 2, 5]]])
raster("statistics.tif", [[[0, 1, 2], [3, 4, -9999]], [[-5, -9999, 5], [-9999, 15, 25]]])
raster("algebra-a-unmasked.tif", [[[2, 4, 0, 10], [6, -2, 8, 0]]], nodata=None)
raster("algebra-b-unmasked.tif", [[[1, 0, 0, 2], [3, -4, 2, 5]]], nodata=None)
wide = np.ones((1, 513, 513), dtype=np.float32)
wide[0, 256, 256] = -9999
raster("statistics-wide.tif", wide)
