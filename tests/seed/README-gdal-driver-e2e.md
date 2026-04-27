# GDAL Driver E2E — Seed Fixture Notes

The `gdal-driver-e2e.yml` workflow (see ADR-0034) reuses the canonical client
compatibility seed `client-compat-v1.sql`. No bespoke fixture is committed for
the GDAL lane — keeping the seed surface aligned with the PyQGIS and Windows
client compatibility lanes is intentional, so all client-facing certifications
exercise the same shape of data.

## What the seed gives the GDAL workflow

`client-compat-v1.sql` provisions a single OGC API Features collection with the
shape GDAL needs to round-trip via the built-in `OAPIF:` stand-in driver:

| Aspect | Value | Why GDAL needs it |
|---|---|---|
| Service id | `test_service` | OGC API Features service URL prefix used by the workflow |
| Collection id | `0` | Stable collection id the workflow asserts is present in `ogrinfo` output |
| CRS | `EPSG:4326` | Round-trip CRS check: must survive the GeoPackage write |
| Geometry type | `Point` | Simplest geometry type that exercises GeoJSON streaming and GeoPackage encoding |
| Feature count | `10` | Non-zero, fits in a single page (default `limit=1000`) so paging is exercised but not stressed |
| Attribute types | string, integer, double, boolean, datetime, date, time, uuid, JSON arrays | Exercises GDAL's OGR field type inference for the most common honua field types |
| Nullable attribute | `description` (some rows NULL) | Verifies NULL propagation through the GeoJSON → OGR field pipeline |
| Null-geometry row | `lambda` row has NULL `geometry` | Verifies the OAPIF→OGR adapter does not drop attribute-only rows |

## What the workflow asserts

1. `ogrinfo -ro -so OAPIF:http://localhost:5000/ogc/features` lists collection
   `0`. This satisfies the "list layers" acceptance criterion.
2. `ogr2ogr -f GPKG out.gpkg OAPIF:http://localhost:5000/ogc/features 0`
   produces a non-empty GeoPackage. This satisfies the "round-trip to
   GeoPackage" acceptance criterion.
3. `ogrinfo` against the produced GeoPackage reports a feature count equal to
   the source `numberReturned`. This guards the "without data loss" half of
   the acceptance criterion.
4. `ogrinfo` against the produced GeoPackage reports `EPSG:4326` in the layer
   SRS metadata. This guards the CRS-fidelity half of the acceptance criterion.

## Why this seed and not a new one

- It is already maintained for the Windows and PyQGIS client compatibility
  lanes — the same shape of data has to satisfy multiple desktop GIS clients.
- Inventing a new seed would create a fresh maintenance surface (and a fresh
  drift risk between the GDAL lane and the PyQGIS lane) without adding
  coverage. ADR-0034 intentionally keeps the GDAL CI artifact thin while the
  bulk of driver-specific testing lives in the `honua-gdal` repo.

## When the `honua-gdal` plugin lands

The workflow stays the same shape. Only two lines change:

1. Install the `honua-gdal` plugin into the GDAL container (or swap to the
   `honua/gdal-driver` image).
2. Replace `OAPIF:${GDAL_E2E_OAPIF_LANDING}` with `HONUA:${GDAL_E2E_BASE_URL}`
   in the two `ogrinfo` / `ogr2ogr` invocations.

The seed expectations above do not change. New honua-specific assertions
(`HONUA_TOKEN`, FeatureServer fallback) belong in the driver's own CI inside
the `honua-gdal` repo, not here.
