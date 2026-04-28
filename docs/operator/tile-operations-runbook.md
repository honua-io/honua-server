# Tile Operations Runbook

This runbook covers asynchronous tile lifecycle jobs for cache management, priming, and archive generation.

## Supported Operations

Start jobs through:

`POST /api/v1/admin/tile-operations/jobs`

`operation` must be one of:

- `seed`
- `warm`
- `invalidate`
- `purge`
- `archive`
- `publish`

Request scope options:

- `serviceId`
- `layerId`
- `minZoom` / `maxZoom`
- `bbox` (`[minLon,minLat,maxLon,maxLat]`)
- `tileMatrixSetId` (currently `WebMercatorQuad`)
- `maxTiles` safety cap for seed/warm

## Job Control Endpoints

- `GET /api/v1/admin/tile-operations/jobs/{jobId}` status/progress
- `GET /api/v1/admin/tile-operations/jobs?activeOnly=true|false` list jobs
- `POST /api/v1/admin/tile-operations/jobs/{jobId}/cancel` cancel queued/running jobs
- `POST /api/v1/admin/tile-operations/jobs/{jobId}/retry` retry failed/cancelled jobs

## Operational Notes

- Jobs are tracked via the unified operations progress store as `OperationType.TileCache` (`OperationType.PMTilesArchive` for `archive` jobs, `OperationType.PMTilesPublish` for `publish` jobs).
- `seed`/`warm` currently target MVT generation through the standard tile provider.
- `invalidate`/`purge` use output cache invalidation scopes (layer/service/global metadata).
- `archive` generates a PMTiles v3 archive from tile outputs and uploads it to cloud storage as a temporary admin download (24h TTL). Partial generation failures still produce a downloadable archive (random per-job key, 24h TTL).
- `publish` generates a durable PMTiles artifact at a deterministic key with no TTL and returns a provider-agnostic descriptor for browser MapLibre/PMTiles consumption. Unlike `archive`, `publish` aborts before upload if any tiles fail to generate (`Publish aborted before upload: N tiles failed during generation.`), so a previously good artifact at the deterministic key is never overwritten with bytes that miss the failed tiles. See [PMTiles Publishing](pmtiles-publishing.md).
- Retry creates a new job ID while preserving the original request parameters.

## Metrics

Prometheus/OpenTelemetry surfaces include:

- `honua.tile.jobs.queue_depth`
- `honua.tile.jobs.total` (tagged by `operation`, `status`)
- `honua.tile.jobs.duration_ms`
- `honua.tile.jobs.tiles_processed`
- `honua.tile.archives.total` (count of generated PMTiles archives — incremented by both `archive` and `publish` jobs)
- `honua.tile.archives.size_bytes` (size histogram for generated PMTiles archives — recorded by both `archive` and `publish` jobs)

## Example: Invalidate a Layer

```bash
curl -X POST https://<host>/api/v1/admin/tile-operations/jobs \
  -H "Content-Type: application/json" \
  -d '{"operation":"invalidate","serviceId":"MyService","layerId":3}'
```

## Example: Seed Tiles

```bash
curl -X POST https://<host>/api/v1/admin/tile-operations/jobs \
  -H "Content-Type: application/json" \
  -d '{
    "operation":"seed",
    "layerId":3,
    "minZoom":8,
    "maxZoom":10,
    "bbox":[-123.0,37.0,-121.0,38.0],
    "maxTiles":2000
  }'
```

## PMTiles Archive Generation

Generate a PMTiles v3 archive from existing tile outputs. The archive is built by fetching tiles through the standard tile provider, assembling them into a sorted Hilbert-curve-indexed PMTiles v3 file, and uploading the result to cloud storage.

### Request

```bash
curl -X POST https://<host>/api/v1/admin/tile-operations/jobs \
  -H "Content-Type: application/json" \
  -d '{
    "operation": "archive",
    "layerId": 3,
    "minZoom": 0,
    "maxZoom": 10,
    "bbox": [-123.0, 37.0, -121.0, 38.0],
    "maxTiles": 5000
  }'
```

The `archive` operation requires `layerId` (single-layer archives only for this release).

### Archive-Specific Response Fields

When querying job status (`GET /api/v1/admin/tile-operations/jobs/{jobId}`), archive jobs include:

| Field | Description |
|-------|-------------|
| `archiveFileId` | Cloud storage file ID for the generated archive |
| `downloadUrl` | Time-limited presigned URL to download the `.pmtiles` file (24h expiry) |
| `archiveSizeBytes` | Final archive size in bytes |

### Validation

Download the archive and inspect with `pmtiles show`:

```bash
curl -o output.pmtiles "<downloadUrl>"
pmtiles show output.pmtiles
```

### Notes

- Archives are stored with a 24-hour TTL in cloud storage.
- If cloud storage is not configured, both `archiveFileId` and `downloadUrl` will be null.
- This operation generates tiles on-demand (does not read from cache). For large tile sets, use `maxTiles` to limit scope.
- The archive uses PMTiles v3 format with MVT tile type and tiles sorted by Hilbert curve index.

## Durable PMTiles Publish

`operation: "publish"` writes a durable PMTiles artifact for browser-based MapLibre/PMTiles
consumption. See [PMTiles Publishing](pmtiles-publishing.md) for storage configuration,
URL strategies (`SignedUrl` / `PublicUrl` / `RangeProxy`), and required object-store CORS
headers.

### Request

```bash
curl -X POST https://<host>/api/v1/admin/tile-operations/jobs \
  -H "Content-Type: application/json" \
  -d '{
    "operation": "publish",
    "serviceId": "world",
    "layerId": 42,
    "minZoom": 0,
    "maxZoom": 12
  }'
```

Completed job status returns a `publishedArtifact` descriptor with provider, bucket,
object key, content type, size, URL strategy, browser-usable access URL, and MapLibre
source hints (bounds, minzoom, maxzoom).

