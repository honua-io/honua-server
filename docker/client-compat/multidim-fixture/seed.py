#!/usr/bin/env python3
"""Create and upload the real two-time-slice NetCDF fixture."""

import os
import tempfile

import boto3
import netCDF4
import numpy as np


BUCKET = os.environ.get("HONUA_MULTIDIM_BUCKET", "honua-multidim-fixtures")
KEY = os.environ.get("HONUA_MULTIDIM_KEY", "imageserver/sea-surface-temperature.nc")
ENDPOINT = os.environ.get("HONUA_S3_ENDPOINT", "http://localstack:4566")


def main() -> None:
    s3 = boto3.client(
        "s3",
        endpoint_url=ENDPOINT,
        region_name="us-east-1",
        aws_access_key_id="test",
        aws_secret_access_key="test",
    )
    s3.create_bucket(Bucket=BUCKET)

    with tempfile.NamedTemporaryFile(suffix=".nc") as artifact:
        with netCDF4.Dataset(artifact.name, "w", format="NETCDF4") as dataset:
            dataset.Conventions = "CF-1.8"
            dataset.setncattr_string("variables", ["sea_surface_temperature"])
            dataset.crs_wkid = np.int32(4326)
            dataset.extent = np.asarray([-122.50, 37.70, -122.35, 37.85], dtype=np.float64)
            dataset.primary_variable = "sea_surface_temperature"
            dataset.x_dimension = "longitude"
            dataset.y_dimension = "latitude"
            dataset.t_dimension = "time"
            dataset.t_start = "2024-01-01T00:00:00Z"
            dataset.t_end = "2024-01-02T00:00:00Z"
            dataset.t_step_seconds = np.float64(86400)
            dataset.createDimension("time", 2)
            dataset.createDimension("latitude", 4)
            dataset.createDimension("longitude", 4)

            time = dataset.createVariable("time", "f8", ("time",))
            time.standard_name = "time"
            time.units = "hours since 2024-01-01 00:00:00"
            time.calendar = "gregorian"
            time[:] = [0, 24]

            latitude = dataset.createVariable("latitude", "f8", ("latitude",))
            latitude.standard_name = "latitude"
            latitude.units = "degrees_north"
            latitude.axis = "Y"
            latitude[:] = [37.70, 37.75, 37.80, 37.85]

            longitude = dataset.createVariable("longitude", "f8", ("longitude",))
            longitude.standard_name = "longitude"
            longitude.units = "degrees_east"
            longitude.axis = "X"
            longitude[:] = [-122.50, -122.45, -122.40, -122.35]

            temperature = dataset.createVariable(
                "sea_surface_temperature",
                "f4",
                ("time", "latitude", "longitude"),
                zlib=True,
                complevel=1,
                fill_value=np.float32(-9999.0),
            )
            temperature.standard_name = "sea_surface_temperature"
            temperature.long_name = "Sea Surface Temperature"
            temperature.units = "degC"
            temperature.coordinates = "time latitude longitude"
            temperature[:] = np.asarray(
                [
                    [[10, 11, 12, 13], [14, 15, 16, 17], [18, 19, 20, 21], [22, 23, 24, 25]],
                    [[20, 21, 22, 23], [24, 25, 26, 27], [28, 29, 30, 31], [32, 33, 34, 35]],
                ],
                dtype=np.float32,
            )

        s3.upload_file(artifact.name, BUCKET, KEY)

    size = s3.head_object(Bucket=BUCKET, Key=KEY)["ContentLength"]
    if size <= 0:
        raise RuntimeError("uploaded NetCDF fixture is empty")
    print(f"uploaded s3://{BUCKET}/{KEY} ({size} bytes)")


if __name__ == "__main__":
    main()
