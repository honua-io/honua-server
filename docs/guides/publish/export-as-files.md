---
type: guide
title: "Share parcels as files"
description: "Export an uploaded parcels dataset as cloud-native PMTiles and GeoParquet files, from file upload to signed-in and public CDN URLs."
tags: [gis, publish, exports]
---

# Share parcels as files

Export a parcels Dataset as immutable cloud-native files — a PMTiles archive for map display and a Hilbert-ordered GeoParquet file for browser and analyst queries — described by one STAC 1.1 collection document (`collection.json`) following the open Portolan specification.

**Prerequisites:**

- A running Honua Server ([quickstart](../../get-started/quickstart.md)) with file storage configured.
- An administrator credential ([authentication](../secure/authentication.md)), or a human-provisioned agent credential paired with a human approver in Console Approvals ([approve agent changes](../secure/approve-agent-changes.md)).
- For public CDN URLs, `FileStorage:PublicData` enabled on the server (`FileStorage__PublicData__Enabled=true`, `BucketName`, `Region`, and HTTPS `BaseUrl`; see [environment variables](../../reference/configuration/environment-variables.md)).

## 1. Upload and publish the parcels Layer

An Export captures features from a Layer stored in Honua's managed feature table so the capture check can verify that neither rows nor metadata changed mid-export.

1. Upload your parcels file (for example a GeoPackage or Shapefile) using [import files](import-files.md), keeping the source CRS or reprojecting explicitly as needed ([choose import coordinate system](choose-import-coordinate-system.md)).
2. Confirm the imported table has a stable unique identifier (such as a parcel number text column like `tmk_txt`, or the layer's primary key) and publish it as a Layer in a Service ([publish layers](publish-layers.md)).
3. Decide the intended audience before exporting:
   - **Signed-in only**: keep authentication required on the Service/Layer. The Export will be served only through authenticated `/api/v1/exports/{exportId}/...` routes.
   - **Public CDN URLs**: allow anonymous read and export on the Service/Layer, keep multi-tenancy off, and ensure no permanent row filter, anonymous row-level security filter, hidden field, or anonymous field mask is active. Public promotion follows the Layer's access automatically and refuses promotion if any of those restrictions apply.

## 2. Create the Export

In the authorized [API explorer](../../reference/openapi-and-explorer.md), send `POST /api/v1/admin/exports` (operation `admin.exports.create`). Only `source` is required:

```json
{
  "source": {
    "kind": "layer",
    "serviceId": "maui-parcels",
    "layerId": 1
  }
}
```

When omitted, Honua selects safe defaults:

- `exportId`: derived from the Layer's name as a lowercase slug (`3-64` letters, digits, or hyphens). An `exportId` stays bound to the Layer it first exported.
- `identityField`: the Layer's declared unique text key, or its object ID formatted as text, linking each tile feature to its GeoParquet row.
- `minZoom`, `maxZoom`, `maxTiles`: defaults clamped to the server's tile ceiling so clients can overzoom without blowing up tile generation.

To set explicit values (for example `exportId: "maui-county-parcels"` and `identityField: "tmk_txt"`):

```json
{
  "source": {
    "kind": "layer",
    "serviceId": "maui-parcels",
    "layerId": 1
  },
  "exportId": "maui-county-parcels",
  "identityField": "tmk_txt",
  "minZoom": 8,
  "maxZoom": 14
}
```

A direct administrator call returns `202 Accepted` with the Export record (`status: "running"`, `jobId`) and a `Location` header pointing to `/api/v1/admin/exports/{exportId}`.

If an AI agent submits `admin.exports.create`, Honua returns `AwaitingApproval` with a Proposal ID instead of starting the job. A human reviews and approves that exact Proposal in Console Approvals before the export job runs.

## 3. Wait for the Version to finish

Poll `GET /api/v1/admin/exports/{exportId}` (operation `admin.exports.get`) until `status` transitions from `running` to `ready` (or `failed`).

When `status` is `ready`, inspect the returned fields:

```json
{
  "success": true,
  "data": {
    "id": "maui-county-parcels",
    "title": "Maui County Parcels",
    "status": "ready",
    "source": {
      "kind": "layer",
      "serviceId": "maui-parcels",
      "layerId": 1,
      "name": "Maui County Parcels"
    },
    "currentVersion": "v20261121-01",
    "collectionUrl": "https://maps.example.org/api/v1/exports/maui-county-parcels/current.json",
    "publicUrl": "https://cdn.example.org/data/publications/maui-county-parcels/v20261121-01/collection.json",
    "versions": [
      {
        "version": "v20261121-01",
        "createdAt": "2026-11-21T18:00:00Z",
        "featureCount": 42150,
        "artifacts": [
          { "kind": "pmtiles", "href": "maui-county-parcels.pmtiles", "bytes": 18432910 },
          { "kind": "geoparquet", "href": "maui-county-parcels.parquet", "bytes": 11204882 }
        ]
      }
    ]
  }
}
```

- `currentVersion`: the immutable Version identifier that the `current` pointer now targets.
- `collectionUrl`: the signed-in pointer URL (`/api/v1/exports/{exportId}/current.json`).
- `publicUrl`: the public CDN `collection.json` URL when `FileStorage:PublicData` is enabled and the Layer passed the anonymous-safety gate; otherwise omitted, with `notPublicReason` explaining why.
- `versions`: immutable Versions with `createdAt`, `featureCount`, and `artifacts` (kind, relative `href`, and byte size).

Each completed Version contains:

- `{exportId}.pmtiles` (`pmtiles` asset, role `visual`, gzip-compressed vector tiles keeping every public attribute) and `{exportId}.parquet` (`geoparquet` asset, role `data`, GeoParquet 1.1 Hilbert-ordered on bbox centres and ZSTD-compressed for fast browser range lookups).
- `collection.json` (STAC 1.1 collection following the open Portolan specification with the `honua:` STAC extension carrying the identity field and capture identifier).
- Generated companions derived from the Layer's metadata:
  - `README.md` — title, description, SPDX licence, producer/host providers, and capture timestamp.
  - `AGENTS.md` — public field table, coordinate system, and ready-to-run DuckDB and Python queries.
  - `style.json` — MapLibre GL style built from the Layer's saved style (without symbol layers).
  - `thumbnail.png` — rendered preview image when available.

## 4. Verify signed-in and public URLs

### Signed-in URL

Send an authenticated `GET` or `HEAD` to `/api/v1/exports/{exportId}/current.json`:

- Expected: `307 Temporary Redirect` to the current immutable Version's `collection.json`, with `Cache-Control: private, no-store`.
- Resolve relative asset links (`geoparquet`, `pmtiles`, `style`, `thumbnail`, `README.md`, `AGENTS.md`) against the redirected immutable URL so every file comes from the same capture.
- Versioned files support byte ranges (`206 Partial Content`), `HEAD`, and conditional `ETag` checks.

### Public CDN URLs

When `publicUrl` is populated:

1. Open `{BaseUrl}/data/catalog.json` (short-lived cache: `public, max-age=60, s-maxage=300`) and confirm the Export appears in the root catalog links.
2. Open `publicUrl` (`collection.json`) and confirm the `geoparquet` asset (role `data`) and `pmtiles` asset (role `visual`) carry `file:size` and `file:checksum` and resolve with `Cache-Control: public, max-age=31536000, immutable`.
3. Open `publicUrl` in the Honua JavaScript SDK (`openExport`), in any Portolan-aware viewer, or query the GeoParquet URL directly from DuckDB without Honua credentials:

```sql
INSTALL spatial;
LOAD spatial;
SELECT tmk_txt, ST_Area(geometry) AS area
FROM read_parquet('https://cdn.example.org/data/publications/maui-county-parcels/v20261121-01/maui-county-parcels.parquet')
LIMIT 10;
```

Or in Python with GeoPandas:

```python
import geopandas as gpd

gdf = gpd.read_parquet(
    "https://cdn.example.org/data/publications/maui-county-parcels/v20261121-01/maui-county-parcels.parquet"
)
print(gdf.shape)
```

## 5. Refresh or unpublish

- **Publish a new Version**: call `POST /api/v1/admin/exports` again for the same Layer and `exportId`. Honua writes a new immutable Version and moves the `current` pointer only after every checksum and validation check passes. Normal refreshes require zero CDN invalidations because versioned keys are immutable.
- **Unpublish**: call `DELETE /api/v1/admin/exports/{exportId}` (operation `admin.exports.delete`; requires human approval when submitted by an agent). Honua marks the Export `unpublished`, removes public objects, rewrites `data/catalog.json`, and tracks a CDN invalidation to completion.
- **Automatic revocation on access change**: if you restrict the source Layer (for example disable anonymous access, add a row-level security filter, or hide a field), Honua automatically revokes the public files and invalidates the CDN path.

## Troubleshoot

- **`publicUrl` is empty and `notPublicReason` is set** — either `FileStorage:PublicData` is disabled, or the Layer failed the anonymous-safety gate (multi-tenancy enabled, anonymous access off, row filter active, hidden field, or field mask). Fix the underlying condition and re-export if public delivery is intended; do not bypass access policy.
- **`409 Conflict` when starting an Export** — an export job is already running for that `exportId`. Poll `GET /api/v1/admin/exports/{exportId}` until it finishes instead of submitting a duplicate job.
- **`403 Forbidden` on signed-in `/api/v1/exports/{exportId}/...` reads** — the source Layer now has a row filter or field mask that an immutable archive cannot enforce, or the caller lacks export permission.
- **Export refused for an external database table** — Exports capture from Honua's managed feature table so edit-log consistency can be verified; import the dataset into managed storage first.
- **Tile-operations `paired-publish` rejected** — `paired-publish` has been retired from `/api/v1/admin/tile-operations`; use `POST /api/v1/admin/exports` instead.

## Next steps

- [Export concept](../../concepts/domain/export.md) — Versions, Artifacts, the current pointer, and access rules.
- [Publish tiles](publish-tiles.md) — tile caching and PMTiles delivery options.
- [Approve agent changes](../secure/approve-agent-changes.md) — how Console Approvals governs agent-proposed Exports.
- [Honua operating skills](../connect/operating-skills.md) — use the `honua-exports` skill to run this workflow with an AI agent.
