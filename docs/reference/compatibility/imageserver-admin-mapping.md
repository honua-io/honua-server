# ImageServer admin operations → Honua admin API

ArcGIS ImageServer exposes a set of **admin/mutation** operations
(`addRasters`, `deleteRasters`, `updateRaster`, `uploads`, `downloadRasters`,
`validate`, `calculateVolume`, `computeMultidimensionalInfo`,
`computeTiePoints`). Honua intentionally does **not** re-implement these as
GeoServices-REST mutations on the (anonymous, read-only) ImageServer surface.
Instead, raster ingestion and mutation are owned by the canonical, admin-authorized
Honua admin API, which enforces layer homogeneity, advisory locks, EXTERNAL-TOAST
storage, statistics, and tile pre-generation atomically through a single hardened
pipeline (#1875 decision memo).

This page maps each Esri ImageServer admin op to its Honua admin equivalent for
ArcGIS-client users migrating to Honua.

| Esri ImageServer admin op | Honua admin equivalent | Status |
|---|---|---|
| `addRasters` | `POST /api/v1/admin/import/raster` (multipart GeoTIFF/PNG/JPEG upload) and `POST /api/v1/admin/cloud-rasters` (register a cloud-hosted COG) | Supported (single-file, synchronous) |
| `deleteRasters` | `DELETE /api/v1/admin/import/raster/{rasterId}` (cascades to statistics, tiles, and sensor metadata) and `DELETE /api/v1/admin/cloud-rasters/{id}` for registered COGs | Supported |
| `updateRaster` | `PATCH /api/v1/admin/import/raster/{rasterId}` (update `name`, `description`, `acquisitionDate`) | Supported |
| `uploads` | Synchronous import only via `POST /api/v1/admin/import/raster`; async raster jobs are deferred | Partial |
| `downloadRasters` | None (clients hold the source data) | Not supported by design |
| `validate` | File validation occurs at import time | Not supported by design |
| `calculateVolume` | None — analytic, not catalog mutation | Deferred (raster-analysis scope) |
| `computeMultidimensionalInfo` | Single `acquisitionDate` per raster | Partial (no true multidimensional cube) |
| `computeTiePoints` | None — photogrammetry; ties to the sensor-model work (#1879/#1880/#1881) | Deferred |

## Why admin-API canonical instead of GeoServices-REST parity

1. **One ingestion path = one correctness/security surface.** The admin path
   enforces layer homogeneity (SRID/band-count), advisory locks, EXTERNAL-TOAST
   storage (#1625), statistics, and tile pre-generation atomically. A second
   ingestion path over GeoServices REST would duplicate or bypass all of that.
2. **The Esri admin ops assume a mosaic-dataset model Honua does not have.**
   Honua's catalog is an implicit projection of `raster_data`; each row *is* a
   catalog item. Faithful `addRasters`/`uploads`/`validate` parity would require
   inventing a mosaic-dataset/job subsystem.
3. **Mutation over an anonymous, read-only surface is a security smell.**
   ImageServer is intentionally anonymous/read-only; the admin API already has
   `RequireAdminAuthorization`.

## Examples

Delete an imported raster:

In the authorized [API explorer](../openapi-and-explorer.md), run `DELETE /api/v1/admin/import/raster/42`. Success returns `204`; an unknown raster id returns `404`.

Update an imported raster's descriptive metadata:

Run `PATCH /api/v1/admin/import/raster/42` with `{"name":"Maui 2024 mosaic","acquisitionDate":"2024-06-01T00:00:00Z"}`. Success returns the updated metadata; an empty recognized patch returns `400`, and an unknown id returns `404`.

PATCH semantics: a field omitted from the JSON body is left unchanged. To clear
`description` or `acquisitionDate`, send an empty string for that field.

A thin GeoServices `addRasters`/`deleteRasters` shim that *delegates* to the admin
service (no new pipeline) remains an optional, demand-driven follow-up — it is not
built speculatively.
