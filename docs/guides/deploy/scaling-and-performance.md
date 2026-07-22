# Scale and tune performance

You'll size the database admission limits, add Redis-backed caching, scale Honua horizontally, and tune outbound HTTP resilience.

**Prerequisites:** A running deployment and access to its environment variables. Confirm effective values at runtime with `GET /api/v1/admin/config` (admin auth).

## Steps

1. Size database admission. Honua gates concurrent queries and pools connections per replica; keep the total under the database's `max_connections` with headroom (`max_connections >= pool_size × replicas + headroom`).

```bash
Limits__Connections__MaxConnectionPoolSize=100
Limits__Connections__MaxConcurrentQueries=200
Limits__Connections__RequestTimeout=00:02:00
ConnectionStrings__DefaultConnection="Host=db;Database=honua;Username=honua;Password=secret;Minimum Pool Size=10;Maximum Pool Size=100;Connection Idle Lifetime=300"
```

2. Bound query and tile work. These limits apply across all protocols and are the first lever when large requests saturate the database.

```bash
Limits__Query__MaxRecordCount=5000
Limits__Query__DefaultRecordCount=1000
Limits__Query__QueryTimeout=00:01:00
Limits__Tiles__MaxFeaturesPerTile=50000
Limits__Tiles__TileTimeout=00:00:30
TileOptions__CacheMaxAge=3600
```

3. Add Redis caching. With Redis configured, metadata and output caches are shared across replicas; without it each replica falls back to a bounded in-memory cache.

```bash
ConnectionStrings__Redis=redis.example.com:6379
Cache__Enabled=true
Cache__DefaultTtlSeconds=300
Cache__EnableFallback=true
Cache__FallbackMaxEntries=5000
Cache__KeyPrefix=honua:prod:
```

4. Scale horizontally. Honua is stateless — add replicas behind the load balancer; no session affinity is needed. Lower the per-replica pool size as you add replicas to respect the sizing rule in step 1.

```bash
kubectl -n honua scale deployment/honua-server --replicas=4
```

## Caching layers

| Layer | Surface | Notes |
|---|---|---|
| Edge / CDN | Tiles, public read endpoints | Recommended in production; honor `TileOptions__CacheMaxAge` |
| Redis (shared) | Service/layer metadata, output cache | Set `ConnectionStrings__Redis`; shared across replicas |
| In-memory fallback | Same surfaces, per replica | Automatic when Redis is absent or down (`Cache__EnableFallback`) |
| Npgsql prepared statements | Query plans | Internal; inspect via `GET /api/v1/admin/performance/database/query-cache/statistics` |

## What needs Redis when multi-node

- **Required**: durable job orchestration (geoprocessing, ETL, tile-cache jobs), queued imports, and workflow runs — the queue, execution logs, and run state are Redis-backed; those endpoints return `503` without it. Enable Redis AOF persistence so queued jobs survive restarts.
- **Strongly recommended**: shared caches (avoids N cold caches and inconsistent invalidation) and shared temporary-file quotas when `FileStorage` is S3/Azure Blob.
- **Not needed**: plain read/query traffic on a single node — in-memory fallback is fine.

A read-replica database connection is not currently supported; all queries use `ConnectionStrings__DefaultConnection`.

## Outbound HTTP resilience

Calls to external services (ArcGIS/GeoServer imports, geocoders, key vaults, webhooks) get automatic retries with exponential backoff and per-service circuit breakers. Three profiles exist — `FastApi`, `Standard`, `SlowService` — plus per-service overrides, all tunable under `HttpResilience__`:

```bash
HttpResilience__SlowService__TimeoutSeconds=300
HttpResilience__ServiceOverrides__arcgis-rest__MaxRetryAttempts=5
HttpResilience__ServiceOverrides__geoserver-rest__TimeoutSeconds=240
```

Key knobs per profile/override: `MaxRetryAttempts`, `BaseDelayMs`, `BackoffExponent`, `JitterPercentage`, `CircuitBreakerFailures`, `CircuitBreakDurationSeconds`, `TimeoutSeconds`. Defaults live in `src/Honua.Server/appsettings.HttpResilience.json`. Raise timeouts and retry counts for slow import sources; lower the breaker threshold for flaky webhooks so failures fail fast.

## Verify

> Use the [API explorer](../../reference/openapi-and-explorer.md) for `GET /monitoring/metrics/connection-pool`.

Expected: JSON with pool utilization well below 100% and zero (or near-zero) timeouts under normal load. Rehearse multi-instance behavior with the scale-test stack at [`docker/scale-test/compose.yml`](../../../docker/scale-test/compose.yml) (`./scripts/scale/scale-test.sh`).

## Troubleshoot

- **Timeouts and "connection pool exhausted" logs** — raise `MaxConnectionPoolSize`/`MaxConcurrentQueries` in small steps, or add replicas with a lower per-replica pool; confirm database `max_connections` headroom.
- **Slow spatial queries** — check for sequential scans (`EXPLAIN ANALYZE`), rebuild GiST indexes, and run `ANALYZE` after bulk imports.
- **Latency spikes after deploys despite healthy app metrics** — cold caches; watch `GET /api/v1/metrics/cache` hit rates recover, and consider warming critical tiles via the CDN.
- **Circuit-breaker-open errors on imports** — the external source is failing repeatedly; raise `CircuitBreakDurationSeconds` patience or fix the upstream, rather than disabling retries.

## Next steps

- [Monitor Honua Server](monitoring.md)
- [Deploy on Kubernetes](kubernetes.md)
- [Troubleshoot Honua Server](troubleshooting.md)
