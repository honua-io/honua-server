#!/usr/bin/env python3
"""OWSLib WCS boundary regression using the independent coverage-fixture.sql grid."""
import argparse
import json
from xml.etree import ElementTree
from urllib.parse import urlencode

import owslib
from owslib.util import ServiceException, http_get
from owslib.wcs import WebCoverageService
from rasterio.io import MemoryFile
from requests import HTTPError


def check(base):
    assert owslib.__version__ == "0.36.0"
    endpoint = base + "/ogc/wcs/svc-coverage-4996-image"
    client = WebCoverageService(endpoint, version="2.0.1")
    coverage = "coverage_2496"
    failures = []
    negatives = []
    # The fixture occupies [-124,-120] x [36,40]. These windows have no
    # positive-area intersection, including a window touching its west edge.
    for subsets, kwargs in [
        ([("Long", -100, -99.9), ("Lat", 10, 10.1)], {}),
        ([("Long", -126, -125)], {}),
        ([("Long", -119, -118)], {}),
        ([("Lat", 34, 35)], {}),
        ([("Lat", 41, 42)], {}),
        ([("Long", -125, -124)], {}),
        ([("x", 0, 1000), ("y", 0, 1000)], {"subsettingcrs": "EPSG:3857"}),
    ]:
        try:
            client.getCoverage(identifier=coverage, format="image/tiff", subsets=subsets, **kwargs).read()
        except (ServiceException, HTTPError) as error:
            # HTTPError retains the real response; ServiceException does not.
            # In that case also check status using OWSLib's public HTTP helper.
            if isinstance(error, HTTPError):
                response = error.response
            else:
                params = [("service", "WCS"), ("version", "2.0.1"),
                          ("request", "GetCoverage"), ("CoverageID", coverage),
                          ("format", "image/tiff"), *kwargs.items()]
                params.extend(("subset", f"{axis}({low},{high})") for axis, low, high in subsets)
                response = http_get(endpoint + "?" + urlencode(params), timeout=60)
            document = ElementTree.fromstring(response.content)
            assert document.tag == "{http://www.opengis.net/ows/2.0}ExceptionReport"
            exception = document.find("{http://www.opengis.net/ows/2.0}Exception")
            result = {"subsets": subsets, "status": response.status_code,
                      "code": exception.attrib["exceptionCode"],
                      "locator": exception.attrib.get("locator")}
            negatives.append(result)
            if response.status_code not in (400, 404) or result["code"] != "InvalidSubsetting" or result["locator"] != "SUBSET":
                failures.append(result)
        else:
            failures.append({"subsets": subsets, "error": "outside subset returned success"})

    positives = []
    for subsets, expected, transform in [
        ([("Long", -123, -121), ("Lat", 37, 39)], [[11, -9999], [21, 22]], (-123, 1, 0, 39, 0, -1)),
        ([("Lat", 37, 39), ("Long", -123, -121)], [[11, -9999], [21, 22]], (-123, 1, 0, 39, 0, -1)),
        # Straddling the north-west corner clips to the first two rows/columns.
        ([("Long", -125, -122), ("Lat", 38, 41)], [[0, 1], [10, 11]], (-124, 1, 0, 40, 0, -1)),
    ]:
        response = client.getCoverage(identifier=coverage, format="image/tiff", subsets=subsets)
        assert response.info().get("Content-Type").split(";")[0] == "image/tiff"
        with MemoryFile(response.read()) as memory, memory.open() as raster:
            assert (raster.width, raster.height, raster.count) == (2, 2, 1)
            assert raster.dtypes == ("float32",)
            assert raster.read(1).tolist() == expected
            assert raster.nodata == -9999
            assert raster.crs.to_epsg() == 4326
            assert raster.transform.to_gdal() == transform
            positives.append({"subsets": subsets, "values": expected,
                              "geotransform": transform, "nodata": raster.nodata,
                              "size": [2, 2], "srid": 4326, "dtype": "float32"})
    result = {"result": "fail" if failures else "pass", "client": "OWSLib 0.36.0",
              "fixture": "coverage-fixture.sql", "negative_checks": negatives,
              "positive_checks": positives, "failures": failures}
    print(json.dumps(result, indent=2))
    return bool(failures)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default="http://honua:5000")
    raise SystemExit(check(parser.parse_args().base_url.rstrip("/")))
