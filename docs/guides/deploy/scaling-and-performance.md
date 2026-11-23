---
type: runbook
title: "Scale and tune performance"
description: "You'll size the database admission limits, size the per-replica cache, scale Honua horizontally, and tune outbound HTTP resilience."
resource: "honua://capability/caching.output-cache"
---
# Scale and tune performance

You'll size the database admission limits, size the per-replica cache, scale Honua horizontally, and tune outbound HTTP resilience.

**Prerequisites:** A running deployment and access to its environment variables. Confirm effective values at runtime with `GET /api/v1/admin/config` (admin auth).

## Steps

1. Size database admission. Honua gates concurrent queries and pools connections per replica; keep the total under the database's `max_connections` with headroom (`max_connections >= pool_size × replicas + headroom`).

   When you set `Deployment__MaxTasks` and `Database__InstanceClass` (or `Database__MaxConnections`), Honua reserves one dedicated realtime listener connection per task within 80% of the database limit, then divides the remaining budget between two pools per task. The idle floor drops to at most 2, and concurrent queries are capped at the pool size. Automatic sizing refuses startup if that budget cannot support at least one connection per pool; reduce the task ceiling or provide database capacity. Values you set under `Limits__Connections__` still win. Two tasks on `db.t4g.small` get 35 connections per pool. This budget covers those two pools and the listener only: named or rotating data sources, workers, optional geocoding and other database clients require additional capacity planning.

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

3. Size the per-replica cache. Each replica caches metadata and output in its own memory; the CDN is the shared cache.

```bash
Cache__Enabled=true
Cache__DefaultTtlSeconds=300
Cache__MaxEntries=50000
```

4. Scale horizontally. Honua is stateless — add replicas behind the load balancer; no session affinity is needed. Lower the per-replica pool size as you add replicas to respect the sizing rule in step 1.

```bash
kubectl -n honua scale deployment/honua-server --replicas=4
```

## Caching layers

| Layer | Surface | Notes |
|---|---|---|
| Edge / CDN | Tiles, public read endpoints | Recommended in production; honor `TileOptions__CacheMaxAge` |
| In-memory | Service/layer metadata, output cache, per replica | Automatic; entries retain their configured TTL; bounded by `Cache__MaxEntries` |
| Npgsql prepared statements | Query plans | Internal; inspect via `GET /api/v1/admin/performance/database/query-cache/statistics` |

Each replica's in-memory cache drops entries at their TTL and evicts entries once it holds `Cache__MaxEntries`. Catalog revision checks refresh metadata across replicas, and output-cache keys include the catalog revision. Other cached values retain their configured TTL.

## Shared state across replicas

- **Durable execution**: PostgreSQL holds shared job queues, execution logs, workflow state, and coordination records. Every participating replica and compatible worker needs access to the shared system database.
- **Output artifacts**: use retained storage accessible to the workers and clients that need the output; a worker's temporary file is insufficient.
- **Catalog consistency**: each replica caches metadata in-process for `Cache__MetadataGraphTtlSeconds` (default 10). Read requests revalidate the active revision with `Cache__MetadataGraphRevalidationSeconds` (default 2, range 0–60). Administrative and mutation requests require the current revision.
- **Caching**: each replica caches metadata and output in memory; the CDN provides the shared edge cache.

A read-replica database connection is not currently supported; all queries use `ConnectionStrings__DefaultConnection`.

## Durable-state capacity and latency

Include job state, execution logs, queued imports, workflow state, and coordination traffic in PostgreSQL capacity planning. Monitor database storage, CPU, active connections, lock waits, and query latency alongside queue depth and worker progress. Keep connection-pool headroom as described in step 1.

When a backlog grows:

1. Identify whether producers, worker failures, polling, database contention, or output storage are limiting progress. Inspect server and worker logs and existing job status before retrying requests.
2. Check database storage and connection headroom, slow queries, and lock waits. Review log and output retention without deleting active job or coordination records.
3. Correct the identified bottleneck through the deployment's managed configuration and infrastructure process. Include shared system state and output artifacts in backup and recovery procedures.
4. Verify that existing jobs resume or reach a terminal outcome and that retained outputs remain retrievable. Confirm recovery with queue depth, worker progress, and database latency.

## Outbound HTTP resilience

Calls to external services (GeoServices REST/GeoServer imports, geocoders, key vaults, webhooks) get automatic retries with exponential backoff and per-service circuit breakers. Three profiles exist — `FastApi`, `Standard`, `SlowService` — plus per-service overrides, all tunable under `HttpResilience__`:

Merge this fragment into the service's appsettings configuration. Keep the service name `geoserver-rest`; the hyphen makes its environment-variable key invalid as a Bash assignment name. Deployment environment variables take precedence over appsettings values.

```json
{
  "HttpResilience": {
    "SlowService": {
      "TimeoutSeconds": 300
    },
    "ServiceOverrides": {
      "geoserver-rest": {
        "TimeoutSeconds": 240
      }
    }
  }
}
```

Key knobs per profile/override: `MaxRetryAttempts`, `BaseDelayMs`, `BackoffExponent`, `JitterPercentage`, `CircuitBreakerFailures`, `CircuitBreakDurationSeconds`, `TimeoutSeconds`. Defaults live in `src/Honua.Server/appsettings.HttpResilience.json`. Raise timeouts and retry counts for slow import sources; lower the breaker threshold for flaky webhooks so failures fail fast.

## Verify

> Inspect the connection-pool panel in Honua Console's Operate view; the underlying monitoring route is not exposed by the OpenAPI explorer.

Expected: JSON with pool utilization well below 100% and zero (or near-zero) timeouts under normal load.

## Troubleshoot

- **Timeouts and "connection pool exhausted" logs** — raise `MaxConnectionPoolSize`/`MaxConcurrentQueries` in small steps, or add replicas with a lower per-replica pool; confirm database `max_connections` headroom.
- **Slow spatial queries** — check for sequential scans (`EXPLAIN ANALYZE`), rebuild GiST indexes, and run `ANALYZE` after bulk imports.
- **Latency spikes after deploys despite healthy app metrics** — cold caches; watch `GET /api/v1/metrics/cache` hit rates recover, and consider warming critical tiles via the CDN.
- **Circuit-breaker-open errors on imports** — the external source is failing repeatedly; raise `CircuitBreakDurationSeconds` patience or fix the upstream, rather than disabling retries.

## Next steps

- [Monitor Honua Server](monitoring.md)
- [Deploy on Kubernetes](kubernetes.md)
- [Troubleshoot Honua Server](troubleshooting.md)
