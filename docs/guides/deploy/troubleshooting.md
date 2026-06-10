# Troubleshoot Honua Server

You'll diagnose the most common operational failures by symptom and apply the verified fix.

**Prerequisites:** Access to server logs and the admin password (admin endpoints authenticate with the `X-API-Key` header).

## Quick triage

```bash
curl -f http://localhost:8080/healthz/live
curl -f http://localhost:8080/healthz/ready
docker logs --tail 200 honua-server
```

Expected: `Healthy` and `Ready`. If either fails, start with the startup or database tables below. Admin-only diagnostics: `GET /monitoring/health/production`, `GET /monitoring/health/comprehensive`, `GET /api/v1/admin/observability/errors`.

## Startup

| Symptom | Diagnosis | Fix |
|---|---|---|
| Exits with "Master key must be at least 32 characters long" | `Security__ConnectionEncryption__MasterKey` too short | Set a value of 32+ characters |
| Exits with "Master key not configured" | Connection encryption used without a master key | Set `Security__ConnectionEncryption__MasterKey` |
| Refuses to start, log mentions dev auth | `HONUA_DEV_AUTH=true` in a Production environment | Remove `HONUA_DEV_AUTH`; the bypass only works in `ASPNETCORE_ENVIRONMENT=Test` |
| Exits with an options-validation error naming a `Limits` or `ControlPlane` setting | Out-of-range or malformed env value (validated at startup) | Correct the named variable; compare against [.env.example](../../../.env.example) |
| Container restart loop, log shows migration failure | Database migration failed at startup | Fix DB connectivity/permissions; check `GET /api/v1/admin/observability/migrations` once up. `HONUA_SKIP_MIGRATIONS=true` defers (does not fix) the migration |
| Starts but logs "connection string not configured" | `ConnectionStrings__DefaultConnection` missing | Set it; migrations and data access are skipped without it |

## Database connections

| Symptom | Diagnosis | Fix |
|---|---|---|
| "Connection refused" with `Host=localhost` in Docker | Container resolves localhost to itself | Use the compose service name, e.g. `Host=postgres` |
| Auth failures in Postgres logs | Wrong user/password/database | Verify each part of `ConnectionStrings__DefaultConnection` |
| Function `st_intersects` does not exist | PostGIS extension missing | `CREATE EXTENSION IF NOT EXISTS postgis;` |
| Timeouts, "connection pool exhausted" logs | Pool saturation | Raise `Limits__Connections__MaxConnectionPoolSize`/`MaxConcurrentQueries` in steps, or add replicas with smaller pools; see [Scale and tune performance](scaling-and-performance.md) |
| "too many clients already" from Postgres | `pool_size × replicas` exceeds `max_connections` | Lower per-replica pool size or raise database `max_connections` |

Connectivity check: `psql -h db.example.com -U honua -d honua -c "SELECT 1;"` and pool state via `GET /monitoring/metrics/connection-pool` (admin).

## Authentication: 401 and 403

| Symptom | Diagnosis | Fix |
|---|---|---|
| 401 on every admin call in a fresh compose stack | The stock `docker-compose.yml` does not pass `HONUA_ADMIN_PASSWORD` into the container | Add it via `docker-compose.override.yml` or your production compose file, then restart |
| 401 with the password set | Request missing the header, or set after start | Send `X-API-Key: <admin password>`; restart the container after env changes |
| 401 on anonymous reads (tiles, features) that "should be public" | Anonymous access is denied until a service access policy allows it | `PUT /api/v1/admin/services/{serviceId}/access-policy` with `{"allowAnonymous": true}` |
| OIDC 401 | Provider not configured or issuer mismatch | Set `Oidc:Enabled=true`, configure a provider (`Oidc:Generic`, `Oidc:AzureAd`, `Oidc:Google`), check the authority URL against the discovery document, and sync system time |
| OIDC 403 | Token lacks an admin role | Map the role claim with `Oidc:ClaimsMapping:RoleClaimType` and set `Oidc:AdminRoles` |
| Browser calls blocked (CORS error in console) | Dev CORS is force-disabled inside containers/Kubernetes | Set `Cors__AllowedOrigins__0` to the page's exact origin (scheme + host + port) |

## Imports

| Symptom | Diagnosis | Fix |
|---|---|---|
| Upload rejected as too large | File exceeds the configured import limit | Check `GET /api/v1/admin/import/limits`; split the file or raise the limit |
| Unsupported format error | Format not in the importer matrix | Check `GET /api/v1/admin/import/formats` and convert |
| Import job fails on geometry | Invalid geometry or unsupported CRS in the source | Validate/fix geometries before import (`ST_MakeValid`), declare the correct source CRS |
| Import accepted but job never progresses | Queued imports require Redis | Set `ConnectionStrings__Redis` and ensure Redis is reachable |
| `503` on OGC Processes / GPServer job routes | Redis-backed durable job store not configured | Enable Redis; see [Scale and tune performance](scaling-and-performance.md) |

Recent jobs: `GET /api/v1/admin/import/jobs`; durable job detail and logs: `GET /api/v1/admin/jobs/{jobId}/logs`. For stuck workflow runs, see [Automate workflows](../query-analyze/automate-workflows.md).

## Tiles and rendering

| Symptom | Diagnosis | Fix |
|---|---|---|
| Queries/tiles return nothing where data exists | SRID mismatch between request and stored geometry | `SELECT ST_SRID(geometry) ... GROUP BY 1;` then match the request CRS (`inSR`/`outSR`, `bbox-crs`) |
| Tiles slow or timing out | Missing spatial index or oversized tiles | `CREATE INDEX CONCURRENTLY ... USING gist(geometry);`; lower `Limits__Tiles__MaxFeaturesPerTile`, raise `TileOptions__SimplifyZoom` |
| Some features render, others don't | Invalid geometries | Find with `ST_IsValid`, repair with `ST_MakeValid` |
| Large-area requests rejected | Bbox area guard | Intentional (`Limits__Query__MaxBboxAreaSqKm`); tile the request or raise the limit deliberately |
| Stale tiles after edits | Edge/CDN caching | Tiles honor `TileOptions__CacheMaxAge`; purge the CDN or lower the TTL |

## Memory and performance

| Symptom | Diagnosis | Fix |
|---|---|---|
| Memory climbs with large exports/queries | Oversized result sets buffered per request | Tighten `Limits__Query__MaxRecordCount`/`MaxOffset`; prefer paged/streaming clients |
| OOM kills in containers | Limit below working set | Inspect `GET /api/v1/metrics/memory` and `GET /monitoring/metrics/resources`; raise the container memory limit or tighten query/tile limits |
| High latency, healthy DB | Cold or missing shared cache | Check `GET /api/v1/metrics/cache` hit rates; configure Redis so replicas share caches |
| Slow queries after bulk load | Stale planner statistics | `ANALYZE` the affected tables |
| Throughput drops as replicas are added | Database admission ceiling | Re-run pool sizing: `max_connections >= pool_size × replicas + headroom` |

## Next steps

- [Monitor Honua Server](monitoring.md)
- [Scale and tune performance](scaling-and-performance.md)
- [Production checklist](../secure/production-checklist.md)
