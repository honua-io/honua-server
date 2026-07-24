# Publish tiles

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

`operation` is one of `seed`, `warm`, `invalidate`, `purge`, `archive`, `publish`; scope with `serviceId`, `layerId`, `minZoom`/`maxZoom`, `bbox`, `tileMatrixSetId` (currently `WebMercatorQuad`), and the `maxTiles` safety cap. `warm` re-primes existing cache entries on the same request shape.

### 3. Poll the job

Run `GET /api/v1/admin/tile-operations/jobs/{jobId}`, substituting the id from step 2.

List jobs with `GET .../jobs?activeOnly=true`, cancel with `POST .../jobs/{jobId}/cancel`, retry failed/cancelled jobs with `POST .../jobs/{jobId}/retry` (retry creates a new job id with the original parameters).

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

The completed job status carries `publishedArtifact` with `accessUrl`, `bounds`, `minZoom`/`maxZoom`, and storage details — everything a client needs for a `pmtiles://{accessUrl}` MapLibre source. Re-publishing the same `(serviceId, layerId, tileMatrixSetId)` overwrites the artifact.

How clients reach the artifact is set by `FileStorage:PMTilesPublish:UrlStrategy`:

- `SignedUrl` (default) — presigned/SAS URL, lifetime from `SignedUrlLifetime` (S3 IAM presign caps at 7 days); re-run publish to rotate the URL.
- `PublicUrl` — `{PublicBucketBaseUrl}/{objectKey}` for publicly readable buckets; never expires.
- `RangeProxy` — server-relative `/api/v1/tiles/pmtiles/{artifactId}`; range reads proxy through Honua, no storage credentials reach the browser.

For `SignedUrl`/`PublicUrl`, the bucket CORS policy must allow `GET`/`HEAD` with the `Range` request header and expose `Accept-Ranges`, `Content-Range`, `Content-Length`, `ETag`, and `Last-Modified`.

## Troubleshoot

- **Tile request returns `404`** — the layer id is unknown or disabled; check `GET /api/v1/admin/connections/{id}/layers`.
- **Seed job completes with warnings** — individual tile failures are listed per `z/x/y` in the job progress; commonly bad geometries at high zooms.
- **`archive`/`publish` fails with a cloud storage error** — both operations require configured cloud file storage; `archiveFileId`/`downloadUrl` are null without it.
- **`Publish aborted before upload: N tiles failed during generation.`** — fix the failing tiles (or narrow `bbox`/zoom range) and re-run; the previous artifact is untouched.
- **Browser cannot read the PMTiles artifact** — usually missing bucket CORS headers for `Range` requests, or an expired `SignedUrl`; re-publish or switch strategy.

More help: [troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Style maps](../style/style-maps.md) — styles consume the same MVT endpoints.
- [Publish layers](publish-layers.md) — publish more layers to tile.
- [Operations](../deploy/backup-and-restore.md) — job orchestration and monitoring.
