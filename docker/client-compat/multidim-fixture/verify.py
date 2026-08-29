#!/usr/bin/env python3
"""Drive registration -> native scan -> Zarr read-back -> ImageServer slice."""

import json
import os
import time
import urllib.parse
import urllib.error
import urllib.request

import boto3


BASE_URL = os.environ.get("HONUA_BASE_URL", "http://honua:5000")
API_KEY = os.environ.get("HONUA_ADMIN_API_KEY", "ClientCompatAdmin123!")
BUCKET = os.environ.get("HONUA_MULTIDIM_BUCKET", "honua-multidim-fixtures")
KEY = os.environ.get("HONUA_MULTIDIM_KEY", "imageserver/sea-surface-temperature.nc")
ENDPOINT = os.environ.get("HONUA_S3_ENDPOINT", "http://localstack:4566")


def request(path: str, *, method: str = "GET", payload: dict | None = None) -> tuple[int, bytes, str]:
    data = None if payload is None else json.dumps(payload).encode()
    headers = {"X-API-Key": API_KEY}
    if data is not None:
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(BASE_URL + path, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=30) as response:
            return response.status, response.read(), response.headers.get_content_type()
    except urllib.error.HTTPError as error:
        return error.code, error.read(), error.headers.get_content_type()


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
        _, body, _ = request(job["statusUrl"])
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

    _, body, _ = request("/rest/services/browser_compat/ImageServer?f=json")
    metadata = json.loads(body)
    if metadata.get("hasMultidimensions") is not True:
        raise RuntimeError(f"ImageServer metadata is not multidimensional: {metadata}")
    variables = (metadata.get("multidimensionalInfo") or {}).get("variables", [])
    if not any(variable.get("name") == "sea_surface_temperature" for variable in variables):
        raise RuntimeError(f"scanned variable missing from ImageServer metadata: {variables}")

    definition = json.dumps([{
        "variableName": "sea_surface_temperature", "dimensionName": "time", "values": [1704067200000]
    }], separators=(",", ":"))
    query = urllib.parse.urlencode({
        "f": "image", "format": "png", "bbox": "-122.5,37.7,-122.35,37.85",
        "bboxSR": "4326", "imageSR": "4326", "size": "4,4",
        "multidimensionalDefinition": definition,
    })
    _, image, content_type = request(f"/rest/services/browser_compat/ImageServer/exportImage?{query}")
    if content_type != "image/png" or not image.startswith(b"\x89PNG\r\n\x1a\n"):
        detail = image.decode(errors="replace") if content_type == "application/json" else f"{len(image)} bytes"
        raise RuntimeError(f"selected slice was not rendered as PNG ({content_type}): {detail}")

    print("real multidimensional fixture verified: NetCDF -> native scan -> Zarr -> ImageServer slice")


if __name__ == "__main__":
    main()
