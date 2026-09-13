#!/usr/bin/env python3
"""Drive registration -> native scan -> Zarr read-back -> ImageServer slice."""

import io
import json
from pathlib import Path
import tempfile
import os
import time
import urllib.parse
import urllib.error
import urllib.request

import boto3
import numpy as np
from PIL import Image
import zarr


BASE_URL = os.environ.get("HONUA_BASE_URL", "http://honua:5000")
API_KEY = os.environ.get("HONUA_ADMIN_API_KEY", "ClientCompatAdmin123!")
BUCKET = os.environ.get("HONUA_MULTIDIM_BUCKET", "honua-multidim-fixtures")
KEY = os.environ.get("HONUA_MULTIDIM_KEY", "imageserver/sea-surface-temperature.nc")
ENDPOINT = os.environ.get("HONUA_S3_ENDPOINT", "http://localstack:4566")


def request(path: str, *, method: str = "GET", payload: dict | None = None, timeout: float = 30) -> tuple[int, bytes, str]:
    data = None if payload is None else json.dumps(payload).encode()
    headers = {"X-API-Key": API_KEY}
    if data is not None:
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(BASE_URL + path, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as response:
            return response.status, response.read(), response.headers.get_content_type()
    except urllib.error.HTTPError as error:
        return error.code, error.read(), error.headers.get_content_type()


def expected_temperature() -> np.ndarray:
    # Independent oracle: the authored fixture has a +1 south-to-north scan
    # at t0 and a -1 scan at t1, offset by 25 degrees. Missing cells differ
    # by time so ignored time selection or a lost fill mask cannot pass.
    ordinal = np.arange(16, dtype=np.float32).reshape(4, 4)
    expected = np.stack((10 + ordinal, 35 - ordinal))
    expected[0, 0, 0] = -9999
    expected[1, 3, 3] = -9999
    return expected


def verify_zarr(s3, keys: set[str]) -> None:
    prefix = "imageserver/sea-surface-temperature.zarr/"
    with tempfile.TemporaryDirectory() as directory:
        for key in sorted(keys):
            if not key.startswith(prefix):
                raise AssertionError(f"Zarr object outside derived prefix: {key}")
            # GDAL's /vsis3/ writer emits empty directory markers alongside
            # real Zarr files. A marker is not a downloadable array chunk.
            if key.endswith("/"):
                assert s3.head_object(Bucket=BUCKET, Key=key)["ContentLength"] == 0, key
                continue
            relative = Path(key.removeprefix(prefix))
            if relative.is_absolute() or ".." in relative.parts:
                raise AssertionError(f"invalid Zarr object key: {key}")
            target = Path(directory) / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            s3.download_file(BUCKET, key, str(target))
        group = zarr.open_group(directory, mode="r")
        values = group["sea_surface_temperature"]
        np.testing.assert_array_equal(values[:], expected_temperature())
        assert values.shape == (2, 4, 4), values.shape
        assert values.dtype == np.dtype("float32"), values.dtype
        assert values.fill_value == -9999, values.fill_value
        assert values.attrs["units"] == "degC", dict(values.attrs)
        assert values.attrs["_ARRAY_DIMENSIONS"] == ["time", "latitude", "longitude"]
        np.testing.assert_array_equal(group["time"][:], [0, 24])
        np.testing.assert_allclose(group["latitude"][:], [37.70, 37.75, 37.80, 37.85], rtol=0, atol=1e-10)
        np.testing.assert_allclose(group["longitude"][:], [-122.50, -122.45, -122.40, -122.35], rtol=0, atol=1e-10)
        assert group["time"].attrs["units"] == "hours since 2024-01-01 00:00:00"
    print("Zarr oracle: all 32 values, both fill masks, dimensions, dtype, units and coordinates passed")


def verify_pixels(image: bytes, time_index: int) -> None:
    decoded = Image.open(io.BytesIO(image)).convert("RGBA")
    assert decoded.size == (4, 4), decoded.size
    # Requested 0..40 grayscale stretch, north-up image, nearest-neighbor
    # sampling at the authored cell centers. This calculation does not read
    # server output to derive its expected values or color range.
    north_up = expected_temperature()[time_index, ::-1, :]
    expected = np.zeros((4, 4, 4), dtype=np.uint8)
    valid = north_up != -9999
    gray = np.rint(np.clip(north_up, 0, 40) * (255 / 40)).astype(np.uint8)
    for channel in range(3):
        expected[:, :, channel][valid] = gray[valid]
    expected[:, :, 3][valid] = 255
    np.testing.assert_array_equal(np.asarray(decoded), expected)
    print(f"ImageServer slice {time_index}: all RGBA values, north-up orientation and nodata alpha passed")


def main() -> None:
    status, body, _ = request(
        "/api/v1/admin/multidim-coverages",
        method="POST",
        payload={
            "layerId": 2000,
            "name": "Real multidimensional ImageServer fixture",
            "description": "NetCDF bytes scanned by the production native worker",
            "format": "NetCdf4",
            "provider": "AwsS3",
            "bucket": BUCKET,
            "objectKey": KEY,
            "variables": ["sea_surface_temperature"],
        },
    )
    if status == 409:
        status, body, _ = request("/api/v1/admin/multidim-coverages?layerId=2000")
        registrations = json.loads(body)
        registration = next(
            item for item in registrations
            if item["provider"] == "AwsS3" and item["bucket"] == BUCKET and item["objectKey"] == KEY
        )
    elif status == 201:
        registration = json.loads(body)
    else:
        raise RuntimeError(f"registration failed: HTTP {status}: {body.decode()}")

    status, body, _ = request(
        f"/api/v1/admin/multidim-coverages/{registration['id']}/refresh", method="POST"
    )
    if status != 202:
        raise RuntimeError(f"refresh failed: HTTP {status}: {body.decode()}")
    job = json.loads(body)

    deadline = time.monotonic() + 180
    while time.monotonic() < deadline:
        # First success materializes all derived Zarr metadata via bounded
        # S3 reads. LocalStack took >30s in run 34730824678 after the native
        # worker completed; retain the overall 180s job deadline while giving
        # that one materialization request the remaining budget.
        status, body, _ = request(job["statusUrl"], timeout=min(120, deadline - time.monotonic()))
        if status != 200:
            raise RuntimeError(f"scan status failed: HTTP {status}: {body.decode()}")
        result = json.loads(body)
        if result["status"] == "succeeded":
            break
        if result["status"] in {"failed", "cancelled"}:
            raise RuntimeError(f"native scan {result['status']}: {result.get('error')}")
        time.sleep(2)
    else:
        raise RuntimeError("timed out waiting for native multidimensional scan")

    coverage = result.get("coverage") or {}
    if coverage.get("variableCount", 0) < 1 or coverage.get("metadataScannedAt") is None:
        raise RuntimeError(f"scan did not materialize real metadata: {result}")

    s3 = boto3.client(
        "s3", endpoint_url=ENDPOINT, region_name="us-east-1",
        aws_access_key_id="test", aws_secret_access_key="test"
    )
    derived = s3.list_objects_v2(Bucket=BUCKET, Prefix="imageserver/sea-surface-temperature.zarr/")
    keys = {item["Key"] for item in derived.get("Contents", [])}
    if not any(key.endswith((".zarray", "zarr.json")) for key in keys):
        raise RuntimeError(f"derived Zarr catalog is absent; found keys: {sorted(keys)}")

    verify_zarr(s3, keys)

    _, body, _ = request("/rest/services/browser_compat/ImageServer?f=json")
    metadata = json.loads(body)
    if metadata.get("hasMultidimensions") is not True:
        raise RuntimeError(f"ImageServer metadata is not multidimensional: {metadata}")
    variables = (metadata.get("multidimensionalInfo") or {}).get("variables", [])
    if not any(variable.get("name") == "sea_surface_temperature" for variable in variables):
        raise RuntimeError(f"scanned variable missing from ImageServer metadata: {variables}")

    images = []
    for time_index, timestamp in enumerate((1704067200000, 1704153600000)):
        definition = json.dumps([{
            "variableName": "sea_surface_temperature", "dimensionName": "time", "values": [timestamp]
        }], separators=(",", ":"))
        query = urllib.parse.urlencode({
            "f": "image", "format": "png", "bbox": "-122.525,37.675,-122.325,37.875",
            "bboxSR": "4326", "imageSR": "4326", "size": "4,4",
            "multidimensionalDefinition": definition,
            "interpolation": "RSP_NearestNeighbor",
            "renderingRule": json.dumps({"rasterFunction": "Stretch", "rasterFunctionArguments": {
                "StretchType": 5, "Statistics": [[0, 40, 20, 10]], "Gamma": [1]
            }}),
        })
        _, image, content_type = request(f"/rest/services/browser_compat/ImageServer/exportImage?{query}")
        if content_type != "image/png" or not image.startswith(b"\x89PNG\r\n\x1a\n"):
            detail = image.decode(errors="replace") if content_type == "application/json" else f"{len(image)} bytes"
            raise RuntimeError(f"slice {timestamp} was not rendered as PNG ({content_type}): {detail}")
        verify_pixels(image, time_index)
        images.append(image)

    if images[0] == images[1]:
        raise RuntimeError("distinct time slices rendered identical PNG content; time selection was ignored")

    print("real multidimensional fixture verified: NetCDF -> native scan -> Zarr -> ImageServer slice")


if __name__ == "__main__":
    main()
