# Tile Operations Runbook

This runbook covers asynchronous tile lifecycle jobs for cache management and priming.

## Supported Operations

Start jobs through:

`POST /api/v1/admin/tile-operations/jobs`

`operation` must be one of:

- `seed`
- `warm`
- `invalidate`
- `purge`

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

- Jobs are tracked via the unified operations progress store as `OperationType.TileCache`.
- `seed`/`warm` currently target MVT generation through the standard tile provider.
- `invalidate`/`purge` use output cache invalidation scopes (layer/service/global metadata).
- Retry creates a new job ID while preserving the original request parameters.

## Metrics

Prometheus/OpenTelemetry surfaces include:

- `honua.tile.jobs.queue_depth`
- `honua.tile.jobs.total` (tagged by `operation`, `status`)
- `honua.tile.jobs.duration_ms`
- `honua.tile.jobs.tiles_processed`

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

