#!/usr/bin/env python3
"""Real GDAL 3.8.4 / OWSLib 0.36.0 proof for coverage-fixture.sql.

Run the gdal mode in the pinned GDAL image and the owslib mode in an image
with OWSLib 0.36.0 and GDAL bindings. Values come from the authored grid,
never from a snapshot of the server's current output.
"""
import argparse
import io
import json
import struct
from urllib.parse import urlencode
from urllib.request import urlopen

from osgeo import gdal

gdal.UseExceptions()


def decode(data):
    path = "/vsimem/coverage-4996.tif"
    gdal.FileFromMemBuffer(path, data)
    dataset = gdal.Open(path)
    assert (dataset.RasterXSize, dataset.RasterYSize, dataset.RasterCount) == (2, 2, 1)
    band = dataset.GetRasterBand(1)
    values = struct.unpack("=4f", band.ReadRaster(buf_type=gdal.GDT_Float32))
    assert values == (11, -9999, 21, 22), values
    assert band.GetNoDataValue() == -9999
    assert dataset.GetGeoTransform() == (-123, 1, 0, 39, 0, -1), dataset.GetGeoTransform()
    assert dataset.GetSpatialRef().GetAuthorityCode(None) == "4326"
    result = {"size": [2, 2], "values": values, "nodata": band.GetNoDataValue(),
              "geotransform": dataset.GetGeoTransform(), "srid": 4326}
    dataset = None
    gdal.Unlink(path)
    return result


def gdal_checks(base):
    assert gdal.VersionInfo("RELEASE_NAME") == "3.8.4"
    dataset = gdal.OpenEx("OGCAPI:" + base + "/ogc/coverages/collections/2496",
                          gdal.OF_RASTER, open_options=["API=COVERAGE", "CACHE=NO"])
    assert dataset is not None
    # Select the central quarter of the independently authored 4x4 grid.
    window = gdal.Translate("", dataset, format="MEM", projWin=[-123, 39, -121, 37],
                            width=2, height=2, resampleAlg="nearest")
    assert window is not None
    values = struct.unpack("=4f", window.ReadRaster(buf_type=gdal.GDT_Float32))
    assert values == (11, -9999, 21, 22), values
    assert window.GetSpatialRef().GetAuthorityCode(None) == "4326"
    for observed, expected in zip(window.GetGeoTransform(), (-123, 1, 0, 39, 0, -1)):
        assert abs(observed - expected) < 1e-7, window.GetGeoTransform()
    # Decode both wire representations too: GDAL's virtual coverage band does
    # not expose the GeoTIFF nodata tag, but the actual response must preserve it.
    trims = []
    for subset in ["Lon(-123:-121),Lat(37:39)", "Lat(37:39),Lon(-123:-121)"]:
        with urlopen(base + "/ogc/coverages/collections/2496/coverage?" + urlencode({"subset": subset})) as response:
            assert response.headers.get_content_type() == "image/tiff"
            trims.append(decode(response.read()))
    assert trims[0] == trims[1]
    return {"client": "GDAL 3.8.4", "window_values": values, "trims": trims}


def owslib_checks(base):
    import owslib
    from owslib.ogcapi.coverages import Coverages

    assert owslib.__version__ == "0.36.0"
    client = Coverages(base + "/ogc/coverages")
    results = []
    for subset in [{"Lon": (-123, -121), "Lat": (37, 39)}, {"Lat": (37, 39), "Lon": (-123, -121)}]:
        data = client.coverage("2496", subset=subset)
        if hasattr(data, "read"):
            data = data.read()
        if isinstance(data, io.BytesIO):
            data = data.getvalue()
        results.append(decode(data))
    assert results[0] == results[1]
    return {"client": "OWSLib 0.36.0", "trims": results}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("client", choices=["gdal", "owslib"])
    parser.add_argument("--base-url", default="http://honua:5000")
    args = parser.parse_args()
    result = (gdal_checks if args.client == "gdal" else owslib_checks)(args.base_url.rstrip("/"))
    print(json.dumps({"result": "pass", "fixture": "coverage-fixture.sql", **result}, indent=2))
