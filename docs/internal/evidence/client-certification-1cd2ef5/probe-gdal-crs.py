"""Independent EPSG:4326 bbox probe; run in the pinned GDAL 3.8.4 image."""
import json
import sys

from osgeo import gdal, osr

gdal.UseExceptions()
# These two points are declared in tests/seed/client-compat-v1.sql.
expected = {3: [-122.46, 37.73], 4: [-122.445, 37.74]}
result = {"client_version": gdal.__version__, "expected": expected, "actual": {}, "status": "fail"}
try:
    dataset = gdal.OpenEx("OAPIF:" + sys.argv[1] + "/ogc/features/collections/0", gdal.OF_VECTOR)
    layer = dataset.GetLayer(0)
    reference = osr.SpatialReference()
    reference.ImportFromEPSG(4326)
    reference.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
    result["set_active_srs"] = layer.SetActiveSRS(0, reference)
    result["active_authority"] = layer.GetSpatialRef().GetAuthorityCode(None)
    layer.SetSpatialFilterRect(-122.47, 37.715, -122.44, 37.745)
    result["actual"] = {feature.GetFID(): [feature.GetGeometryRef().GetX(), feature.GetGeometryRef().GetY()]
                        for feature in layer}
    if result["set_active_srs"] == 0 and result["active_authority"] == "4326" and result["actual"] == expected:
        result["status"] = "pass"
except RuntimeError as error:
    result["error"] = str(error)
print(json.dumps(result, indent=2))
sys.exit(0 if result["status"] == "pass" else 1)
