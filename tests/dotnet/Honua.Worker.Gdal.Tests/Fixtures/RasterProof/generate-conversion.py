"""Input fixtures only: integer cell matrices are the independently specified oracle."""
from pathlib import Path
import numpy as np
from osgeo import gdal, osr

gdal.UseExceptions()
root = Path(__file__).parent
srs = osr.SpatialReference()
srs.ImportFromEPSG(4326)


def write(name, data, nodata, transform):
    a = np.array(data, dtype=np.uint8)
    ds = gdal.GetDriverByName("GTiff").Create(str(root / name), a.shape[2], a.shape[1], len(a), gdal.GDT_Byte)
    ds.SetProjection(srs.ExportToWkt())
    ds.SetGeoTransform(transform)
    for i, values in enumerate(a, 1):
        band = ds.GetRasterBand(i)
        band.WriteArray(values)
        band.SetNoDataValue(nodata)
        if len(a) == 3:
            band.SetColorInterpretation([gdal.GCI_RedBand, gdal.GCI_GreenBand, gdal.GCI_BlueBand][i - 1])
    ds.Close()


write("conversion-rgb.tif", [[[10, 20, 30], [40, 50, 255]],
                              [[11, 21, 31], [41, 51, 255]],
                              [[12, 22, 32], [42, 52, 255]]], 255, (10, 0.5, 0, 20, 0, -0.5))
write("conversion-classes.tif", [[[1, 1, 0], [1, 0, 2], [0, 2, 0]]], 0, (10, 1, 0, 20, 0, -1))
