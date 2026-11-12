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

Each replica's in-memory cache drops entries at their TTL and evicts entries once it holds `Cache__MaxEntries`. An invalidation clears the cache of the replica that made the change; other replicas serve their copy until it expires.

## What needs Redis when multi-node

- **Required**: durable job orchestration (geoprocessing, ETL, tile-cache jobs), queued imports, and workflow runs — the queue, execution logs, and run state are Redis-backed; those endpoints return `503` without it. Enable Redis AOF persistence so queued jobs survive restarts.
- **Strongly recommended**: shared temporary-file quotas when `FileStorage` is S3/Azure Blob.
- **Catalog consistency**: each replica caches the metadata catalog in-process for `Cache__MetadataGraphTtlSeconds` (default 10). With Redis configured, a catalog write (publishing a service or layer, changing an access policy) is broadcast on the `<Cache__KeyPrefix>metadata:v2:graph:invalidations` pub/sub channel (`honua:metadata:v2:graph:invalidations` by default) so every replica drops its copy immediately. On AWS Lambda without Redis, each request instead checks the active catalog revision with a one-row database read and reloads the catalog when another instance published, so a new service is visible on the next request. Elsewhere without Redis, other replicas can serve the previous catalog until the TTL expires; set `Cache__MetadataGraphRevalidateOnRead=true` to turn the per-request check on (or `false` to turn it off on Lambda).
- **Not needed**: caching. Each replica caches metadata and output in its own memory, and the CDN is the shared cache.

A read-replica database connection is not currently supported; all queries use `ConnectionStrings__DefaultConnection`.

## Redis capacity and latency

Size Redis from observed peak memory, not its empty-instance footprint. Include durable job state and logs, queued imports, workflow state, persistence overhead, replication buffers, and failover headroom. Alert before the configured maximum leaves too little room for a traffic spike or persistence operation; the example `RedisMemoryHigh` threshold is 90%.

Use `maxmemory-policy noeviction` wherever Redis stores durable jobs or workflow state. An eviction policy such as `allkeys-lru` can make cache pressure silently delete durable state. If cache and durable workloads compete for memory, use separate Redis instances so the cache can use an eviction policy without weakening the durable-job contract. Enable AOF persistence for durable workloads.

When memory is high:

1. Confirm `used_memory`, `maxmemory`, `maxmemory_policy`, fragmentation, key count, and the largest key groups with your managed Redis metrics or `redis-cli INFO memory` and `redis-cli --bigkeys` from an approved operations host.
2. Check whether growth tracks cache churn, queued jobs, import logs, stuck workflows, or a recent TTL/configuration change. Do not run broad key deletion or `FLUSH*` commands; those can destroy active job state.
3. Add memory or move cache traffic to a separate instance before the limit is reached. For cache-only instances, tune TTLs or the eviction policy deliberately. For durable instances, retain `noeviction` and address the producer, backlog, or capacity constraint.
4. After mitigation, verify that durable jobs and imports still progress and that Redis memory returns below the alert threshold.

Treat the example `RedisLatencyHigh` threshold—p99 above 10 ms for five minutes—as a starting budget, then tune it to the managed service, region, and workload. Rising latency slows durable jobs, queued imports, and workflows, which then accumulate and may time out. Plain reads do not use Redis.

For high latency, compare server latency with network round-trip time, CPU, memory pressure, command rate, connection count, persistence/failover events, and large or blocking commands. Keep Honua and Redis in the same low-latency network, remove noisy or blocking clients, scale the Redis tier when CPU or bandwidth is saturated, and investigate memory pressure before increasing application timeouts. Confirm recovery with p50/p95/p99 latency plus job-queue depth and cache hit rate.

## Outbound HTTP resilience

Calls to external services (GeoServices REST/GeoServer imports, geocoders, key vaults, webhooks) get automatic retries with exponential backoff and per-service circuit breakers. Three profiles exist — `FastApi`, `Standard`, `SlowService` — plus per-service overrides, all tunable under `HttpResilience__`:

```bash
HttpResilience__SlowService__TimeoutSeconds=300
HttpResilience__ServiceOverrides__geoserver-rest__TimeoutSeconds=240
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
