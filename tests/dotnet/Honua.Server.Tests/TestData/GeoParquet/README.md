# Independent GeoParquet import controls

These two inputs were written by PyArrow 25.0.1 with Shapely 2.2.0 WKB, outside
Honua's Parquet writer and test factory. Regenerate them with
`generate-independent-fixtures.py` in a Python environment with those packages.
The dynamic CRS definition is read from Honua's authoritative EPSG:7912
PROJJSON catalog; the 1.0 input omits `crs` and uses the specification's
OGC:CRS84 default. Both include an unknown vendor metadata extension to verify
that optional extensions do not replace or invalidate required fields.

| File | SHA-256 | Expected content |
|---|---|---|
| `independent-v1.0.parquet` | `aa291444fa9ea1fecdeb35f1004d2b0dbacd4f1bcbf50b89bc195091fc07259e` | GeoParquet 1.0.0, POINT (-122.4194 37.7749), one row |
| `independent-epoch-v1.1.parquet` | `99e01ae71ad13f2d41cc569b7da3b268f20df77746d28748d18cb0cf3fe8cb40` | GeoParquet 1.1.0, EPSG:7912, epoch 2021.25, POINT Z (10 20 30), one row |

The Postgres unit controls read and preview both inputs. Server integration
controls upload the 1.0 input to PostGIS and upload, publish and export the
1.1 dynamic-epoch input.
