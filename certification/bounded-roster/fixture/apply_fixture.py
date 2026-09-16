#!/usr/bin/env python3
"""Author and register the bounded-roster cloud raster fixtures.

Runs once per roster run, after ``roster-raster-fixture.sql``, inside the GDAL
3.13.3 lane image on the fixture network (never through the recording proxy, so
fixture traffic is not client evidence). It:

1. creates the ``roster-fixtures`` bucket on the S3-compatible fixture store;
2. writes a deterministic EPSG:3857 GoogleMapsCompatible COG and a CF Zarr v2
   datacube there with GDAL;
3. registers them through the candidate's admin API
   (``/api/v1/admin/cloud-rasters`` for layers 5000/5001,
   ``/api/v1/admin/zarr-stores`` for layers 5200/5201);
4. writes ``fixture-artifacts.json`` with the sha256 of every authored object and
   the registration responses.

Pixel oracles the cells rely on:

* COG: value(col) = 40 + 8 * floor(col / 16) over a 256x256 source grid covering
  -122.46..-122.38 lon, 37.72..37.80 lat (nodata 0), warped to WebMercatorQuad.
* Zarr ``sea_surface_temperature(time, latitude, longitude)``: t0 = 10 + ordinal,
  t1 = 35 - ordinal over a 4x4 grid, fill -9999 at (t0, 0, 0) and (t1, 3, 3).
"""
from __future__ import annotations

import hashlib
import json
import os
import struct
import sys
import time
import urllib.error
import urllib.request

from osgeo import gdal, osr

gdal.UseExceptions()

BASE_URL = os.environ.get("ROSTER_FIXTURE_BASE_URL", "http://honua:5000")
S3_ENDPOINT = os.environ.get("ROSTER_S3_ENDPOINT", "localstack:4566")
BUCKET = "roster-fixtures"
COG_KEY = "cog/roster-3857.tif"
ZARR_ROOT = "zarr/roster-sst.zarr"
ADMIN_HEADERS = {"X-API-Key": os.environ.get("HONUA_CERT_API_KEY", ""), "Content-Type": "application/json"}
OUTPUT = os.environ.get("ROSTER_FIXTURE_OUTPUT", "/run/fixture-artifacts.json")

for name, value in {
    "AWS_S3_ENDPOINT": S3_ENDPOINT, "AWS_HTTPS": "NO", "AWS_VIRTUAL_HOSTING": "FALSE",
    "AWS_ACCESS_KEY_ID": "test", "AWS_SECRET_ACCESS_KEY": "test", "AWS_REGION": "us-east-1",
    "CPL_VSIL_USE_TEMP_FILE_FOR_RANDOM_WRITE": "YES",
}.items():
    gdal.SetConfigOption(name, value)


def http(method: str, url: str, body: dict | None = None, headers: dict | None = None) -> tuple[int, str]:
    data = None if body is None else json.dumps(body).encode()
    request = urllib.request.Request(url, data=data, headers=headers or {}, method=method)
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            return response.status, response.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode("utf-8", "replace")


def sha256_of(path: str) -> str:
    digest = hashlib.sha256()
    handle = gdal.VSIFOpenL(path, "rb")
    try:
        while chunk := gdal.VSIFReadL(1, 1 << 20, handle):
            digest.update(chunk)
    finally:
        gdal.VSIFCloseL(handle)
    return digest.hexdigest()


def author_cog() -> dict:
    source = gdal.GetDriverByName("MEM").Create("", 256, 256, 1, gdal.GDT_Byte)
    source.SetGeoTransform([-122.46, 0.0003125, 0, 37.80, 0, -0.0003125])
    reference = osr.SpatialReference()
    reference.ImportFromEPSG(4326)
    source.SetProjection(reference.ExportToWkt())
    band = source.GetRasterBand(1)
    band.SetNoDataValue(0)
    band.WriteRaster(0, 0, 256, 256, bytes((40 + (x // 16) * 8) & 0xFF for _ in range(256) for x in range(256)))
    target = f"/vsis3/{BUCKET}/{COG_KEY}"
    gdal.Translate(target, source, format="COG", creationOptions=["TILING_SCHEME=GoogleMapsCompatible", "COMPRESS=DEFLATE"])
    dataset = gdal.Open(target)
    described = {"key": COG_KEY, "size": [dataset.RasterXSize, dataset.RasterYSize],
                 "geotransform": dataset.GetGeoTransform(), "layout": dataset.GetMetadata("IMAGE_STRUCTURE"),
                 "sha256": sha256_of(target)}
    dataset = None
    return described


def author_zarr() -> dict:
    root_path = f"/vsis3/{BUCKET}/{ZARR_ROOT}"
    if gdal.VSIStatL(root_path) is not None:
        gdal.RmdirRecursive(root_path)
    dataset = gdal.GetDriverByName("Zarr").CreateMultiDimensional(root_path, options=["FORMAT=ZARR_V2"])
    group = dataset.GetRootGroup()
    time_dim = group.CreateDimension("time", "TEMPORAL", None, 2)
    lat_dim = group.CreateDimension("latitude", "HORIZONTAL_Y", "NORTH", 4)
    lon_dim = group.CreateDimension("longitude", "HORIZONTAL_X", "EAST", 4)
    float64 = gdal.ExtendedDataType.Create(gdal.GDT_Float64)
    float32 = gdal.ExtendedDataType.Create(gdal.GDT_Float32)

    def coordinate(name, dimension, values, attributes):
        array = group.CreateMDArray(name, [dimension], float64)
        array.Write(struct.pack(f"<{len(values)}d", *values))
        for key, value in attributes.items():
            attribute = array.CreateAttribute(key, [], gdal.ExtendedDataType.CreateString())
            attribute.Write(value)
        dimension.SetIndexingVariable(array)
        return array

    coordinate("time", time_dim, [0.0, 24.0], {"units": "hours since 2024-01-01 00:00:00", "standard_name": "time"})
    coordinate("latitude", lat_dim, [37.70, 37.75, 37.80, 37.85], {"units": "degrees_north", "standard_name": "latitude"})
    coordinate("longitude", lon_dim, [-122.50, -122.45, -122.40, -122.35], {"units": "degrees_east", "standard_name": "longitude"})
    values = [10.0 + ordinal for ordinal in range(16)] + [35.0 - ordinal for ordinal in range(16)]
    values[0] = -9999.0
    values[16 + 15] = -9999.0
    sst = group.CreateMDArray("sea_surface_temperature", [time_dim, lat_dim, lon_dim], float32)
    sst.SetNoDataValueDouble(-9999.0)
    sst.SetUnit("degC")
    reference = osr.SpatialReference()
    reference.ImportFromEPSG(4326)
    sst.SetSpatialRef(reference)
    sst.Write(struct.pack("<32f", *values))
    dataset = None
    listing = sorted(gdal.ReadDirRecursive(root_path) or [])
    return {"root": ZARR_ROOT, "objects": {entry: sha256_of(f"{root_path}/{entry}") for entry in listing
                                           if not entry.endswith("/")}}


def register(path: str, body: dict) -> dict:
    status, text = http("POST", BASE_URL + path, body, ADMIN_HEADERS)
    if status == 409:
        return {"layerId": body["layerId"], "status": 409, "detail": text[:200]}
    if status not in (200, 201):
        raise SystemExit(f"registration {path} {body.get('layerId')} failed: {status} {text[:300]}")
    return json.loads(text)


def main() -> int:
    if not ADMIN_HEADERS["X-API-Key"]:
        raise SystemExit("HONUA_CERT_API_KEY is required to register fixtures")
    for attempt in range(30):
        status, _ = http("PUT", f"http://{S3_ENDPOINT}/{BUCKET}")
        if status in (200, 409):
            break
        time.sleep(2)
    else:
        raise SystemExit("could not create the fixture bucket")
    artifacts = {"bucket": BUCKET, "gdal": gdal.VersionInfo("--version"), "cog": author_cog(), "zarr": author_zarr()}
    artifacts["registrations"] = {
        "cloud-rasters": [register("/api/v1/admin/cloud-rasters", {
            "layerId": layer, "name": f"roster cog {layer}", "provider": "AwsS3", "bucket": BUCKET, "objectKey": COG_KEY})
            for layer in (5000, 5001)],
        "zarr-stores": [register("/api/v1/admin/zarr-stores", {
            "layerId": layer, "name": f"roster zarr {layer}", "provider": "AwsS3", "bucket": BUCKET, "rootPath": ZARR_ROOT})
            for layer in (5200, 5201)],
    }
    with open(OUTPUT, "w", encoding="utf-8") as handle:
        json.dump(artifacts, handle, indent=2)
    print(json.dumps({"cog": artifacts["cog"]["sha256"], "zarr_objects": len(artifacts["zarr"]["objects"])}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
