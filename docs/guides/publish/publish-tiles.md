---
type: guide
title: "Publish tiles"
description: "You'll have a published layer serving vector tiles, a seeded tile cache, and (optionally) a durable PMTiles artifact in about 10 minutes."
resource: "honua://capability/serve.vector-tiles"
---
# Publish tiles

See the [Honua vocabulary](../../concepts/domain/README.md) for the meanings of Connection, Dataset, Layer, Service, Folder, and Environment.

You'll have a published layer serving vector tiles, a seeded tile cache, and (optionally) a durable PMTiles artifact in about 10 minutes.

**Prerequisites:** A running server ([quickstart](../../get-started/quickstart.md)), admin credentials ([authentication](../secure/authentication.md)), and a published layer ([Publish layers](publish-layers.md)).

Every published layer serves Mapbox Vector Tiles immediately; tile operations jobs manage the cache and produce PMTiles archives asynchronously.

## Steps

### 1. Fetch tile metadata and a tile

Open `http://localhost:8080/tiles/{layerId}/tile.json` in a browser, substituting the layer id.

The TileJSON `tiles` template is `/tiles/{layerId}/{z}/{x}/{y}.mvt`. Point any MapLibre vector source at the `tile.json` URL; the source-layer name is `layer`.

### 2. Seed the cache

In the authorized [API explorer](../../reference/openapi-and-explorer.md), run `POST /api/v1/admin/tile-operations/jobs` with this body:

```json
{
  "operation": "seed",
  "layerId": 1,
  "minZoom": 8,
  "maxZoom": 12,
  "bbox": [-123.0, 37.0, -121.0, 38.0],
  "maxTiles": 5000
}
```

`operation` is one of `seed`, `warm`, `invalidate`, `purge`, `archive`, `publish`, `paired-publish`; scope with `serviceId`, `layerId`, `minZoom`/`maxZoom`, `bbox`, `tileMatrixSetId` (currently `WebMercatorQuad`), and the `maxTiles` safety cap. `warm` re-primes existing cache entries on the same request shape.

### 3. Poll the job

Run `GET /api/v1/admin/tile-operations/jobs/{jobId}`, substituting the id from step 2.

List jobs with `GET .../jobs?activeOnly=true`, cancel with `POST .../jobs/{jobId}/cancel`, retry failed/cancelled jobs with `POST .../jobs/{jobId}/retry` (retry creates a new job id with the original parameters).

A tile job is a job in the server's PostgreSQL database. It runs once, on whichever server claims it, and still runs if the server that accepted it stops. Tile jobs queued or running before an upgrade to this release are not carried over: they cannot be cancelled or retried, and they disappear from the job list within 24 hours. Submit those operations again.

### 4. Invalidate after data changes

Run `POST /api/v1/admin/tile-operations/jobs` with `{"operation":"invalidate","layerId":1}`.

`invalidate` and `purge` evict output-cache entries at layer, service, or global scope; re-seed afterward for hot areas.

## Verify

Load `/tiles/{layerId}/12/655/1583.mvt` through MapLibre or another MVT client. Browser developer tools should report:

```text
200 application/vnd.mapbox-vector-tile
```

## PMTiles

Two job operations produce PMTiles v3 archives from a layer's tiles:

| Concern | `archive` | `publish` |
| --- | --- | --- |
| Lifetime | 24-hour TTL, random key | Durable, deterministic key (`{prefix}/pmtiles/{serviceId}/{layerId}/{tms}.pmtiles`) |
| Result | `archiveFileId`, `downloadUrl`, `archiveSizeBytes` | `publishedArtifact` descriptor |
| Audience | Operator download | Browser MapLibre/PMTiles clients |
| Partial tile failures | Tolerated | Job fails before upload (never overwrites a good artifact) |

Run `POST /api/v1/admin/tile-operations/jobs` with this body:

```json
{
  "operation": "publish",
  "serviceId": "default",
  "layerId": 1,
  "minZoom": 0,
  "maxZoom": 12
}
```

Archives never carry fields hidden on the layer (`hidden: true` in its field configuration): tiles and the `vector_layers` metadata omit them.

Both operations accept an optional `tileCompression`: `none` (default) or `gzip`. Gzip tiles are typically about half the size of uncompressed MVT tiles and suit archives served by HTTP range requests, where transport compression does not apply. PMTiles clients read the compression from the archive header and decode gzip tiles natively. The job status reports the value written as `TileCompression`.

The completed job status carries `publishedArtifact` with `accessUrl`, `bounds`, `minZoom`/`maxZoom`, and storage details — everything a client needs for a `pmtiles://{accessUrl}` MapLibre source. Re-publishing the same `(serviceId, layerId, tileMatrixSetId)` overwrites the artifact.

How clients reach the artifact is set by `FileStorage:PMTilesPublish:UrlStrategy`:

- `SignedUrl` (default) — presigned/SAS URL, lifetime from `SignedUrlLifetime` (S3 IAM presign caps at 7 days); re-run publish to rotate the URL.
- `PublicUrl` — `{PublicBucketBaseUrl}/{objectKey}` for publicly readable buckets; never expires.
- `RangeProxy` — server-relative `/api/v1/tiles/pmtiles/{artifactId}`; range reads proxy through Honua, no storage credentials reach the browser.

The range proxy checks every read against the access policy of the layer the archive was published from, so an archive whose layer is restricted, retired or rebound is not served. An archive you place in the bucket yourself (for example a basemap extract) has no source layer. To serve it, upload it at the publish `KeyPrefix` with content type `application/vnd.pmtiles` and object metadata `operation=publish`, and set `FileStorage:PMTilesPublish:AllowAnonymousUnboundArchives` to `true`. The proxy then serves it to any caller. The default is `false`, and such archives return `404`.

For `SignedUrl`/`PublicUrl`, the bucket CORS policy must allow `GET`/`HEAD` with the `Range` request header and expose `Accept-Ranges`, `Content-Range`, `Content-Length`, `ETag`, and `Last-Modified`.

## Paired PMTiles and GeoParquet

`paired-publish` writes one version of a layer as a PMTiles archive for display and a GeoParquet file for query and export, both from a single read of the layer. It takes `serviceId`, `layerId`, a `publicationId` (3-64 lowercase letters, digits or hyphens), an `identityField` (a visible text field that names each feature in both files), and the usual `minZoom`, `maxZoom`, `maxTiles` and `tileCompression`. `bbox` is refused: tile coverage follows the captured features.

```json
{
  "operation": "paired-publish",
  "serviceId": "maui-parcels",
  "layerId": 1,
  "publicationId": "maui-county-parcels",
  "identityField": "tmk_txt",
  "minZoom": 8,
  "maxZoom": 14,
  "maxTiles": 200000
}
```

Each run writes a new immutable version under `{KeyPrefix}/publications/{publicationId}/{version}/`: the two artifacts, a [publication descriptor](../../gis/schemas/publication-descriptor.v1.schema.json) (`descriptor.json`) and a STAC item (`item.json`) with the artifacts as role-tagged assets. Both artifacts embed the same capture ID in their metadata (`honua:captureId`), and hidden fields appear in neither. The run then moves `{KeyPrefix}/publications/{publicationId}/current.json` to the new version with a conditional write.

The pointer moves only when every check passes: both stored copies match the generated bytes, the layer and its metadata did not change during the capture, and the descriptor satisfies the schema's cross-field rules. Otherwise the job fails, the new version is deleted, and the previous version stays current. A run that finds the pointer already moved by another publish also fails without activating its version. Earlier versions are kept.

The identity field must hold a unique, non-empty value for every feature, so each rendered feature resolves to exactly one GeoParquet row. Tiles in a paired archive keep every feature and attribute: the per-tile size budget that can drop attributes from served tiles does not apply, so choose `minZoom` and `maxTiles` with the layer's density in mind. A publication id stays bound to the layer it first published; a run for another layer with the same id fails.

Paired publish needs a layer stored in Honua's managed feature table (imported or created in Honua), because the capture check reads that table's edit log; layers that read an external source table are refused. The completed job status carries `publishedPublication` with the version, capture ID, descriptor key and pointer key.

Authenticated `GET` or `HEAD` requests to `/api/v1/publications/{publicationId}/current.json` return a `307` redirect to the current version's `descriptor.json`. Resolve the descriptor's relative artifact links against that immutable URL to keep both files on the same capture when a later publish moves the pointer. Versioned files support byte ranges, `HEAD` and conditional requests with `ETag`.

Every read requires export access to the current source layer and checks its tenant, publication and visible fields. Retiring or rebinding the source, changing its tenant or hiding a published field can make retained versions unavailable. Immutable artifacts cannot apply current row filters or field masks, so a read returns `403` when the current policy requires either. This also applies to redirects, conditional requests and byte ranges. Responses use `private, no-store` so shared caches cannot bypass these checks.

## Troubleshoot

- **Tile request returns `404`** — the layer id is unknown or disabled; check `GET /api/v1/admin/connections/{id}/layers`.
- **Seed job completes with warnings** — individual tile failures are listed per `z/x/y` in the job progress; commonly bad geometries at high zooms.
- **`archive`/`publish` fails with a cloud storage error** — both operations require configured cloud file storage; `archiveFileId`/`downloadUrl` are null without it.
- **`Publish aborted before upload: N tiles failed during generation.`** — fix the failing tiles (or narrow `bbox`/zoom range) and re-run; the previous artifact is untouched.
- **Range proxy returns `404` for an archive that exists in the bucket** — the archive has no `layerId` metadata (it was uploaded directly, not by a publish job) and `AllowAnonymousUnboundArchives` is off, or its source layer was retired or rebound. The server logs which one at Information level.
- **Browser cannot read the PMTiles artifact** — usually missing bucket CORS headers for `Range` requests, or an expired `SignedUrl`; re-publish or switch strategy.

More help: [troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Style maps](../style/style-maps.md) — styles consume the same MVT endpoints.
- [Publish layers](publish-layers.md) — publish more layers to tile.
- [Monitor Honua Server](../deploy/monitoring.md) — job history and the Operate views.
