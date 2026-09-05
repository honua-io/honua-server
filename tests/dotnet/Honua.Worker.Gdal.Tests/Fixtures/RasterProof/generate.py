"""Rebuild small analytical fixtures, never expected outputs (GDAL 3.13.1)."""
from pathlib import Path
import json
import numpy as np
from osgeo import gdal, osr

gdal.UseExceptions()
root = Path(__file__).parent


def raster(name, bands, transform=(0, 1, 0, 4, 0, -1), dtype=gdal.GDT_Float32, nodata=-9999):
    data = np.array(bands)
    ds = gdal.GetDriverByName("GTiff").Create(str(root / name), data.shape[2], data.shape[1], len(data), dtype)
    srs = osr.SpatialReference()
    srs.ImportFromEPSG(4326)
    ds.SetProjection(srs.ExportToWkt())
    ds.SetGeoTransform(transform)
    for i, values in enumerate(data, 1):
        ds.GetRasterBand(i).WriteArray(values)
        ds.GetRasterBand(i).SetNoDataValue(nodata)
    ds.Close()


raster("grid.tif", [[[1, 2, 3, 4], [5, -9999, 7, 8], [9, 10, 11, 12], [13, 14, 15, 16]],
                    [[10, 20, 30, 40], [50, -9999, 70, 80], [90, 100, 110, 120], [130, 140, 150, 160]]])
raster("reflectance.tif", [[[0.2, 0.1, 0, -9999], [0.3, 0.2, 0.4, 0.1]],
                            [[0.6, 0.5, 0, 0.8], [0.3, 0.8, 0.2, -9999]],
                            [[0.1, 0.05, 2 / 15, 0.1], [0.2, 0.1, 0.1, 0.1]]])
raster("mosaic-a.tif", [[[1, 2, 3], [4, 5, -9999]], [[11, 12, 13], [14, 15, -9999]]], (0, 1, 0, 2, 0, -1))
raster("mosaic-b.tif", [[[30, 40, 50], [60, -9999, 80]], [[130, 140, 150], [160, -9999, 180]]], (2, 1, 0, 2, 0, -1))
raster("histogram.tif", [[[0, 0, 0, 1], [1, 2, 2, 2], [2, 2, 3, 255]]], dtype=gdal.GDT_Byte, nodata=255)


def polygon(ring, name):
    return {"type": "Feature", "properties": {"id": name}, "geometry": {"type": "Polygon", "coordinates": [ring]}}


cutline = polygon([[1, 1], [4, 1], [4, 2], [2, 2], [2, 4], [1, 4], [1, 1]], "L")
(root / "cutline.geojson").write_text(json.dumps({"type": "FeatureCollection", "features": [cutline]}) + "\n")
zones = []
for name, x0, y0, x1, y1 in [("left", 0, 2, 2, 4), ("right", 2, 2, 4, 4), ("overlap", 1, 2, 3, 4), ("nodata", 1, 2, 2, 3)]:
    zones.append(polygon([[x0, y0], [x1, y0], [x1, y1], [x0, y1], [x0, y0]], name))
(root / "zones.geojson").write_text(json.dumps({"type": "FeatureCollection", "features": zones}) + "\n")
points = [{"type": "Feature", "properties": {"value": v}, "geometry": {"type": "Point", "coordinates": [x, y]}}
          for x, y, v in [(0, 0, 10), (4, 0, 20), (0, 4, 30), (4, 4, 40), (2, 2, 100)]]
(root / "points.geojson").write_text(json.dumps({"type": "FeatureCollection", "features": points}) + "\n")
