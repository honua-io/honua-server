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

The examples use `layerId: 1`; replace it with your published layer's numeric id everywhere, including URL paths. Replace `{serviceId}` with the id of the service that contains that layer. The example bounding box must also overlap your data.

## Steps

### 1. Fetch tile metadata and a tile

In the authorized [API explorer](../../reference/openapi-and-explorer.md), request `GET /tiles/{layerId}/tile.json`, substituting the layer id. Tile reads require authentication by default: opening this URL in an unauthenticated browser returns `401`. For browser use, supply credentials with read access on both metadata and tile requests, or explicitly enable anonymous reads for the service; see [authentication](../secure/authentication.md).

The TileJSON `tiles` template is `/tiles/{layerId}/{z}/{x}/{y}.mvt`. Point a [MapLibre vector source](../connect/maplibre-web-maps.md) at the `tile.json` URL with the same read credentials; the source-layer name is `layer`.

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

`maxTiles` defaults to **500** and is clamped to the server's configured limit. Set it explicitly when increasing coverage. If the requested pyramid exceeds the cap, only the selected tiles are processed; archive/publish jobs report a truncation warning and the archive header describes the selected coverage. Inspect job warnings before treating an archive as complete.

### 3. Poll the job

Run `GET /api/v1/admin/tile-operations/jobs/{jobId}`, substituting the id from step 2.

List jobs with `GET .../jobs?activeOnly=true`, cancel with `POST .../jobs/{jobId}/cancel`, retry failed/cancelled jobs with `POST .../jobs/{jobId}/retry` (retry creates a new job id with the original parameters).

A tile job is a job in the server's PostgreSQL database. It runs once, on whichever server claims it, and still runs if the server that accepted it stops. Tile jobs queued or running before an upgrade to this release are not carried over: they cannot be cancelled or retried, and they disappear from the job list within 24 hours. Submit those operations again.

### 4. Invalidate after data changes

Run `POST /api/v1/admin/tile-operations/jobs` with `{"operation":"invalidate","layerId":1}`.

`invalidate` and `purge` evict output-cache entries at layer, service, or global scope; re-seed afterward for hot areas.

## Verify

Load a tile overlapping your layer through MapLibre or another MVT client, using the read credentials from step 1. For example, `/tiles/{layerId}/12/655/1583.mvt` only contains data if your layer has features at those coordinates. A tile containing features returns:

```text
200 application/vnd.mapbox-vector-tile
```

An empty tile returns `204 No Content` with no tile content type. This is expected outside the data's coverage; navigate to your features to verify rendering.

## PMTiles

Two job operations produce PMTiles v3 archives from a layer's tiles:

| Concern | `archive` | `publish` |
| --- | --- | --- |
| Lifetime | 24-hour TTL, random key | Durable, deterministic key (`{prefix}/pmtiles/{serviceId}/{layerId}/{tms}.pmtiles`) |
| Result | `archiveFileId`, `downloadUrl`, `archiveSizeBytes` | `publishedArtifact` descriptor |
| Audience | Operator download | Browser MapLibre/PMTiles clients |
| Partial tile failures | Tolerated | Job fails before upload (never overwrites a good artifact) |

Run `POST /api/v1/admin/tile-operations/jobs` with this body, substituting your service and layer ids. This small initial pyramid has at most 341 tiles, below the explicit 500-tile cap:

```json
{
  "operation": "publish",
  "serviceId": "{serviceId}",
  "layerId": 1,
  "minZoom": 0,
  "maxZoom": 4,
  "maxTiles": 500
}
```

Archives never carry fields hidden on the layer (`hidden: true` in its field configuration): tiles and the `vector_layers` metadata omit them.

Both operations accept an optional `tileCompression`: `none` (default) or `gzip`. Gzip tiles are typically about half the size of uncompressed MVT tiles and suit archives served by HTTP range requests, where transport compression does not apply. PMTiles clients read the compression from the archive header and decode gzip tiles natively. The job status reports the value written as `TileCompression`.

The completed job status carries `publishedArtifact` with `accessUrl`, `bounds`, `minZoom`/`maxZoom`, and storage details. With a browser-accessible URL, use `pmtiles://{accessUrl}` for a MapLibre source. Re-publishing the same `(serviceId, layerId, tileMatrixSetId)` overwrites the artifact.

The quickstart's local file storage can successfully archive and publish. With the default `SignedUrl` strategy, however, `downloadUrl` and `publishedArtifact.accessUrl` are `file:///...` paths on the server (inside the container when containerized). They are not browser download URLs. A reported URL expiry does not make a local file URL signed or remotely accessible. Use a delivery strategy that exposes the artifact over HTTP before connecting a browser client.

How clients reach the artifact is set by `FileStorage:PMTilesPublish:UrlStrategy`:

- `SignedUrl` (default) — presigned/SAS URL for object storage, lifetime from `SignedUrlLifetime` (S3 IAM presign caps at 7 days); re-run publish to rotate the URL. Local storage returns a server-local file URL instead.
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

Each run writes a new immutable version under `{KeyPrefix}/publications/{publicationId}/{version}/`: the two artifacts, three companion files generated from the layer's metadata, and one STAC 1.1 collection document (`collection.json`). The companions are `README.md` (title, description, licence, source and capture time), `AGENTS.md` (the public fields, the coordinate system and ready-to-run DuckDB and Python queries for the GeoParquet file) and `style.json` (a MapLibre style for the PMTiles file, made from the layer's stored style without symbol layers), plus a `thumbnail.png` when the server can render one. The collection document follows the open [Portolan specification](https://github.com/portolan-sdi/portolan-spec) v0.3.0: GeoParquet is the `data` asset and PMTiles the `visual` asset, each with its media type, `file:size` and a multihash `file:checksum`, plus a `pmtiles` link; it links `README.md` (`describedby`) and `AGENTS.md` (`agents`), lists the style and thumbnail as assets, and carries the licence as an SPDX identifier (`other` otherwise), the producer and host providers and `table:columns` for the public fields. Facts only Honua needs, such as the field that links a tile feature to its GeoParquet row, ride in the [`honua:` STAC extension](../../gis/schemas/stac-honua.v1.0.0.schema.json). Asset links are relative to the document, so the same file works wherever it is served. Both artifacts embed the same capture ID in their metadata (`honua:captureId`), and hidden fields appear in no file. The run then moves `{KeyPrefix}/publications/{publicationId}/current.json` to the new version with a conditional write.

The pointer moves only when every check passes: both stored copies match the generated bytes, the layer and its metadata did not change during the capture, and the collection document satisfies the extension's cross-field rules. Otherwise the job fails, the new version is deleted, and the previous version stays current. A run that finds the pointer already moved by another publish also fails without activating its version. Earlier versions are kept.

The identity field must hold a unique, non-empty value for every feature, so each rendered feature resolves to exactly one GeoParquet row. Tiles in a paired archive keep every feature and attribute: the per-tile size budget that can drop attributes from served tiles does not apply, so choose `minZoom` and `maxTiles` with the layer's density in mind. A publication id stays bound to the layer it first published; a run for another layer with the same id fails.

Paired publish needs a layer stored in Honua's managed feature table (imported or created in Honua), because the capture check reads that table's edit log; layers that read an external source table are refused. The completed job status carries `publishedPublication` with the version, capture ID, collection document key (`descriptorKey`) and pointer key.

Authenticated `GET` or `HEAD` requests to `/api/v1/publications/{publicationId}/current.json` return a `307` redirect to the current version's `collection.json`. Resolve its relative asset links against that immutable URL to keep both files on the same capture when a later publish moves the pointer. Versioned files support byte ranges, `HEAD` and conditional requests with `ETag`.

Every read requires export access to the current source layer and checks its tenant, publication and visible fields. Retiring or rebinding the source, changing its tenant or hiding a published field can make retained versions unavailable. Immutable artifacts cannot apply current row filters or field masks, so a read returns `403` when the current policy requires either. This also applies to redirects, conditional requests and byte ranges. Responses use `private, no-store` so shared caches cannot bypass these checks.

## Troubleshoot

- **Tile request returns `404`** — the layer id is unknown or disabled; check `GET /api/v1/admin/connections/{id}/layers`.
- **Seed job completes with warnings** — individual tile failures are listed per `z/x/y` in the job progress; commonly bad geometries at high zooms.
- **Tile request returns `401`** — supply read credentials on TileJSON and tile requests, or explicitly configure anonymous reads for the service.
- **`archive`/`publish` fails with a storage configuration error** — both operations require a file storage provider. Local storage is supported; object storage is not required for job completion.
- **`Publish aborted before upload: N tiles failed during generation.`** — fix the failing tiles (or narrow `bbox`/zoom range) and re-run; the previous artifact is untouched.
- **Range proxy returns `404` for an archive that exists in the bucket** — the archive has no `layerId` metadata (it was uploaded directly, not by a publish job) and `AllowAnonymousUnboundArchives` is off, or its source layer was retired or rebound. The server logs which one at Information level.
- **Browser cannot read the PMTiles artifact** — check `accessUrl` first: a `file:///...` URL points to server-local storage and needs HTTP delivery. For object storage, check bucket CORS headers for `Range` requests and `SignedUrl` expiry; re-publish or switch strategy.

More help: [troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Style maps](../style/style-maps.md) — styles consume the same MVT endpoints.
- [Publish layers](publish-layers.md) — publish more layers to tile.
- [Monitor Honua Server](../deploy/monitoring.md) — job history and the Operate views.
