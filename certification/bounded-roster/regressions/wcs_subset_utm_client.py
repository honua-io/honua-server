#!/usr/bin/env python3
"""Independent PROJ geometry oracle and real WCS UTM pixel regression (#4997)."""
import json
from urllib.parse import urlencode
from xml.etree import ElementTree

from owslib.util import ServiceException, http_get
from owslib.wcs import WebCoverageService
from rasterio.io import MemoryFile
from rasterio.warp import transform
from requests import HTTPError

# PROJ, independent of the server's PostGIS transformer: the envelope overlaps
# the fixture, while the west polygon edge is east of every fixture coordinate.
xs, ys = transform("EPSG:4326", "EPSG:32610", [-122, -122, -121, -121], [37, 38, 38, 37])
assert min(xs) < 587850 < 587950 < max(xs)
assert min(ys) < 4095400 < 4095500 < max(ys)
assert ys[0] < 4095400 < 4095500 < ys[1]
west_edge = [xs[0] + (y - ys[0]) * (xs[1] - xs[0]) / (ys[1] - ys[0]) for y in (4095400, 4095500)]
assert min(west_edge) > 587950
endpoint = "http://honua:5000/ogc/wcs/svc-coverage-4997-utm-image"
client = WebCoverageService(endpoint, version="2.0.1")
subsets = [("Long", -122, -121), ("Lat", 37, 38)]
try:
    client.getCoverage(identifier="coverage_2497", format="image/tiff", subsets=subsets, subsettingcrs="EPSG:4326").read()
except (ServiceException, HTTPError) as error:
    if isinstance(error, HTTPError):
        response = error.response
    else:
        params = [("service", "WCS"), ("version", "2.0.1"), ("request", "GetCoverage"),
                  ("CoverageID", "coverage_2497"), ("format", "image/tiff"), ("subsettingcrs", "EPSG:4326")]
        params.extend(("subset", f"{axis}({low},{high})") for axis, low, high in subsets)
        response = http_get(endpoint + "?" + urlencode(params), timeout=60)
    document = ElementTree.fromstring(response.content)
    assert document.tag == "{http://www.opengis.net/ows/2.0}ExceptionReport"
    exception = document.find("{http://www.opengis.net/ows/2.0}Exception")
    negative = {"status": response.status_code, **exception.attrib}
else:
    negative = {"status": 200}

# This geographic rectangle fully contains the native 100m x 100m fixture.
response = client.getCoverage(identifier="coverage_2497", format="image/tiff",
                              subsets=[("Long", -122.02, -121.98), ("Lat", 36.99, 37.01)],
                              subsettingcrs="EPSG:4326")
assert response.info().get("Content-Type").split(";")[0] == "image/tiff"
expected = [[-9999 if (r, c) == (1, 2) else 10 * r + c for c in range(4)] for r in range(4)]
with MemoryFile(response.read()) as memory, memory.open() as raster:
    assert (raster.width, raster.height, raster.count) == (4, 4, 1)
    assert raster.dtypes == ("float32",)
    assert raster.read(1).tolist() == expected
    assert raster.nodata == -9999
    assert raster.crs.to_epsg() == 32610
    assert raster.transform.to_gdal() == (587850, 25, 0, 4095500, 0, -25)
    positive = {"values": expected, "nodata": raster.nodata, "srid": 32610,
                "geotransform": raster.transform.to_gdal(), "size": [4, 4], "dtype": "float32"}
passed = negative["status"] in (400, 404) and negative.get("exceptionCode") == "InvalidSubsetting" and negative.get("locator") == "SUBSET"
print(json.dumps({"result": "pass" if passed else "fail", "negative": negative, "positive": positive,
                  "oracle": {"projected_corners": list(zip(xs, ys)), "minimum_west_edge_gap_metres": min(west_edge) - 587950}}, indent=2))
raise SystemExit(0 if passed else 1)
