"""Decode published GeoTIFFs without the executor's output readers."""
import json
import math
import sys
from osgeo import gdal, osr

gdal.UseExceptions()
ds = gdal.Open(sys.argv[1])
srs = osr.SpatialReference(wkt=ds.GetProjection())
srs.AutoIdentifyEPSG()
bands = []
for i in range(1, ds.RasterCount + 1):
    band = ds.GetRasterBand(i)
    values = band.ReadAsArray().flatten().tolist()
    nodata = band.GetNoDataValue()
    bands.append({"type": gdal.GetDataTypeName(band.DataType), "nodata": str(nodata) if nodata is not None and not math.isfinite(nodata) else nodata,
                  "values": [v if math.isfinite(v) else str(v) for v in values]})
print(json.dumps({"width": ds.RasterXSize, "height": ds.RasterYSize,
                  "srid": int(srs.GetAuthorityCode(None)), "transform": ds.GetGeoTransform(), "bands": bands}))
