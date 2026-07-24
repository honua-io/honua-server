# Import data from files

You'll have a geospatial file loaded into a PostGIS table, ready to publish, in about 5 minutes.

**Prerequisites:** A running server ([quickstart](../../get-started/quickstart.md)) and admin credentials ([authentication](../secure/authentication.md)).

The admin import API accepts a single file per request, streams it into PostGIS, reprojects to your target SRID, and queues large uploads as background jobs automatically. Imports land as database tables — publish them as layers afterward (see [Publish layers](publish-layers.md)).

> Prefer an SDK? The same endpoints are wrapped by `honua-sdk-js` and `honua-sdk-dotnet`. Also available in Honua Console — UI guide coming soon.

## Steps

### 1. Check supported formats

In the authorized [API explorer](../../reference/openapi-and-explorer.md), run `GET /api/v1/admin/import/formats`.

Returns the live extension list for your build (`.geojson`, `.json`, `.zip`, `.gpkg`, `.gpx`, `.kml`, `.kmz`, `.wkt`, `.csv`, `.fgb`, `.gdb.zip`, `.parquet`, `.geoparquet`).

### 2. Preview before importing (optional)

Run `POST /api/v1/admin/import/preview` and attach `parcels.geojson` to the `file` form field.

Preview reports detected format, fields, and a `warnings` array without writing any data.

### 3. Upload and import

Run `POST /api/v1/admin/import/upload` with these form values:

| Field | Value |
| --- | --- |
| `file` | `parcels.geojson` |
| `tableName` | `parcels` |
| `targetSrid` | `4326` |
| `overwriteExisting` | `true` |

Optional form fields: `sourceSrid` (when CRS auto-detection fails), `targetSchema`, `forceBackground`. Files above the background-job threshold (see `GET /api/v1/admin/import/limits`) return `202 Accepted` with a `jobId` instead of a synchronous result.

### 4. Poll the job (background imports only)

Run `GET /api/v1/admin/import/jobs/{jobId}`, substituting the `jobId` returned by the upload.

Cancel with `POST /api/v1/admin/import/jobs/{jobId}/cancel`; list active jobs with `GET /api/v1/admin/import/jobs`.

### 5. Import from a public object URL (alternative to upload)

Run `POST /api/v1/admin/import/upload-url` with this JSON body:

```json
{
  "sourceUrl": "https://s3.amazonaws.com/example-bucket/parcels.gdb.zip",
  "tableName": "parcels",
  "targetSrid": 4326,
  "overwriteExisting": true
}
```

The server downloads the object directly; redirecting URLs are rejected. `POST /api/v1/admin/import/preview-url` previews the same way.

## Verify

A synchronous import returns the result inline; a completed background job returns it from the job-status endpoint:

```json
{"success": true, "tableName": "parcels", "featureCount": 1250, "format": "GeoJson", "detectedSrid": 4326, "warnings": [], …}
```

`detectedSrid` confirms CRS auto-detection. The table is now discoverable via `GET /api/v1/admin/connections/{id}/tables` and ready to publish.

## Per-format notes

| Format | Extension | Notes |
| --- | --- | --- |
| GeoJSON | `.geojson`, `.json` | Assumed WGS 84 per the GeoJSON spec. |
| Shapefile | `.zip` | Must be a zip containing `.shp`/`.dbf`/`.shx`/`.prj`; a raw `.shp` upload is rejected. CRS read from `.prj`. |
| GeoPackage | `.gpkg` | CRS read from the GeoPackage metadata. |
| GPX | `.gpx` | WGS 84 by definition. |
| KML / KMZ | `.kml`, `.kmz` | WGS 84 by definition. |
| WKT | `.wkt` | No embedded CRS; provide `sourceSrid` if not WGS 84. |
| CSV | `.csv` | Needs lon/lat columns or a WKT geometry column. |
| FlatGeobuf | `.fgb` | Upload directly, no archive. CRS read from the header; if absent, `sourceSrid` is required or the import is rejected. |
| FileGDB | `.gdb.zip` | Zip exactly one `.gdb` directory with its internal files intact (do not flatten). SRID detected from geodatabase metadata when present. One target table per request — per-layer selection is not exposed. Domains, relationship classes, subtypes, topology rules, and network datasets are detected but not imported; they surface in `warnings`. |
| GeoParquet | `.parquet`, `.geoparquet` | CRS read from the GeoParquet `geo` metadata. Requires WKB geometry encoding; nested columns are skipped with warnings; rows with null geometry are skipped and reported; row groups over 100,000 rows are rejected — re-export with smaller row groups. |

When the server cannot detect a source CRS and no `sourceSrid` was supplied, the import fails with the stable error code `import.source_srid_required`.

## Troubleshoot

- **`Shapefile uploads must be a .zip containing .shp and .dbf files.`** — zip the full shapefile sidecar set instead of uploading `.shp` alone.
- **`Unsupported file format`** — the extension is not in your build's `GET /formats` list; convert to a supported format.
- **Import fails with `import.source_srid_required`** — the file carries no CRS information; re-run with `sourceSrid` set to the EPSG code of the source data.
- **`Background import service not available` (503)** — the file exceeds the synchronous size limit and no job queue is configured; check `GET /api/v1/admin/import/limits` and your deployment's job/Redis configuration.
- **FileGDB import succeeds but data looks incomplete** — check the `warnings` array; advanced geodatabase constructs are not preserved.

More help: [troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Publish layers](publish-layers.md) — turn the imported table into a live layer.
- [Import from ArcGIS services](import-from-arcgis-services.md) — import from a live service instead of a file.
- [Publish rasters](publish-rasters.md) — GeoTIFF and other raster imports use a separate endpoint.
