# Troubleshooting

Quick triage steps and fixes for common Honua Server operational issues.

---

## Quick Triage

```bash
# Health
curl -f http://localhost:8080/healthz/live
curl -f http://localhost:8080/healthz/ready

# Logs
docker logs --tail 200 honua-server

# Database connectivity
psql -h localhost -U honua -d honua -c "SELECT 1;"

# Metrics
curl http://localhost:8080/api/v1/metrics/performance
curl http://localhost:8080/api/v1/metrics/database
curl -H "X-API-Key: your-admin-key" http://localhost:8080/metrics | head -n 20
```

---

## Database Connection Issues

**Connection string present**:
```bash
echo "$ConnectionStrings__DefaultConnection"
```

**Postgres reachable**:
```bash
psql -h localhost -U honua -d honua -c "SELECT 1;"
```

**PostGIS installed**:
```sql
CREATE EXTENSION IF NOT EXISTS postgis;
SELECT PostGIS_Version();
```

**Common fixes**:
- **Wrong host in Docker**: use the service name (e.g., `Host=postgres`).
- **Invalid credentials**: verify user, password, and database name.
- **Pool exhaustion**: reduce `Limits__Connections__MaxConcurrentQueries` or scale out.
- **Postgres max_connections**: ensure headroom for `pool_size x replicas`.

---

## Performance and Latency

**Quick checks**:
- `GET /api/v1/metrics/performance`, `GET /api/v1/metrics/database`, and `GET /metrics` with admin auth
- Review logs for slow queries or timeouts.

**Common causes**:
- Missing spatial or attribute indexes
- Excessive result sizes (large bbox, no limit, high offset)
- Connection pool saturation
- Stale database statistics after bulk import

**Fixes**:
- Tighten `Limits__Query__MaxRecordCount` and `Limits__Query__MaxBboxAreaSqKm`.
- Reduce large offsets and add server-side filters.
- Refresh database stats: `ANALYZE honua.features;`
- Add or rebuild spatial indexes.

---

## Authentication Problems

**API key (401)**:
```bash
curl -H "X-API-Key: your-admin-key" http://localhost:8080/api/v1/admin/config
```

Check:
- `HONUA_ADMIN_PASSWORD` is set and non-empty.
- Requests include the `X-API-Key` header.
- The service was restarted after configuration changes.

**OIDC (401/403)**:
```bash
curl -H "Authorization: Bearer <jwt>" http://localhost:8080/api/v1/admin/config
```

Check:
- `Oidc:Enabled` is true.
- At least one provider is configured (`Oidc:Generic`, `Oidc:AzureAd`, or `Oidc:Google`).
- The issuer/authority URL matches the provider's discovery document.
- System time is in sync (token lifetimes are strict).

**Authorization (403)**:
- Ensure your token includes an admin role.
- Adjust role mapping with `Oidc:ClaimsMapping:RoleClaimType` if your IdP uses a custom claim.
- Set admin role names via `Oidc:AdminRoles`.

---

## Import Process Issues

**Supported formats**:
```bash
curl http://localhost:8080/api/v1/admin/import/formats
```

**Current import limits**:
```bash
curl http://localhost:8080/api/v1/admin/import/limits
```

**Check recent jobs**:
```bash
curl http://localhost:8080/api/v1/admin/import/jobs
```

**Common causes**: file size exceeds limits, invalid geometry or unsupported CRS, network timeouts on large uploads, missing PostGIS extension.

**Fixes**: reduce file size or split into chunks, validate geometry before import, increase `Limits__Imports__MaxImportSize` if appropriate.

---

## Job Orchestration Issues

**Symptom**: jobs stuck in Provisioning or Running without progress.

**Quick triage**:
```bash
# Check worker host logs for heartbeat/claim events
docker logs honua-worker 2>&1 | grep -E "JobExecutionService|JobReconciliationService|RedisJobQueue"

# Verify Redis connectivity (job queue, execution logs, and claim state require Redis)
redis-cli -h redis ping
redis-cli -h redis ZCARD controlplane:jobqueue:pending
```

**Common causes and fixes**:

| Symptom | Likely cause | Fix |
|---------|-------------|-----|
| Jobs stay Queued (local backend) | Worker host not wired or worker failed silently | The combined host queues local jobs but does not execute them — execution requires a worker host registered via `AddJobWorker()`. Check that a worker host is running and healthy; the local backend observes worker-published progress and the reconciler bridges it into the job store |
| Jobs stay Queued (remote backend) | No `IBatchComputeBackend` registered for the job's `(Backend, TargetKind)` | Verify the backend adapter is registered and the `ControlPlane:ExecutionWorkloads` catalog entry matches |
| Heartbeat expiry warnings | Worker crashed or network partition | Check worker health; reconciler auto-recovers |
| Retry exhaustion errors | Executor fails repeatedly | Check executor logs for root cause |
| Claim rollback warnings | Transient Redis error during claim | Self-healing; monitor frequency |
| Claim scan traverse threshold | Queue front dominated by delayed retries | Review retry backoff settings; clear stale entries |
| `503` on OGC Processes or GPServer job routes | Redis not configured | Enable Redis — see [Infrastructure](infrastructure.md) |

The reconciliation service automatically recovers abandoned jobs when heartbeats
expire. No operator intervention is required unless retry budgets are exhausted.
For log-level details and alerting thresholds, see
[Monitoring — Job Orchestration Observability](monitoring.md#job-orchestration-observability).
For policy tuning, see
[Operations — Job Orchestration](operations.md#job-orchestration).

---

## Workflow Orchestration Issues

**Symptom**: workflow runs stuck in Pending or Running, steps not progressing.

**Quick triage**:
```bash
# Check reconciler and scheduler logs
docker logs honua-server 2>&1 | grep -E "Orchestration|8100|8101|8110"

# Verify Redis connectivity (workflow stores require Redis)
redis-cli -h redis ping

# Check for active workflow runs
redis-cli -h redis SMEMBERS orchestration:run:active
```

**Common causes and fixes**:

| Symptom | Likely cause | Fix |
|---------|-------------|-----|
| `503` on cancel for Orchestration operations | Redis not configured; orchestration engine not registered | Enable Redis — see [Infrastructure](infrastructure.md) |
| Runs stuck in Pending | Reconciler background service not running | Verify `AddOrchestrationBackgroundServices()` is wired and Redis is reachable |
| Steps not submitted | Upstream dependency not terminal | Check step `DependsOn` graph and upstream step status |
| `409 Conflict` on cancel | Reconcile lease held by concurrent tick | Retry the cancel request; this is transient |
| Scheduled workflow not firing | Invalid cron expression or time zone | Check logs for event `8116`; correct the definition |
| Step marked Failed with artifact error | Upstream artifact retrieval failed | Check logs for event `8120`; investigate upstream result storage |
| Step observation failures in run warnings | Transient job-observation failure (store outage, network) | Check logs for event `8118`; verify substrate health. The step is preserved and observation retries automatically |
| Reconciliation error spikes | Redis connectivity or data corruption | Check Redis health and `OrchestrationLog` Warning (8110) |
| Run failed with "step-set changed" | Definition steps modified while run was active | Check logs for event `8121`; avoid mutating definitions with active runs |
| Progress view stale after run update | Progress store write failed; authoritative state is still durable | Check logs for event `8122`; verify Redis health. The run is correct — only the progress projection is delayed |

The reconciler automatically resumes runs after crashes or restarts by
rehydrating state from Redis. No operator intervention is required unless
the underlying Redis store is unavailable. For observability details, see
[Monitoring — Workflow Orchestration Observability](monitoring.md#workflow-orchestration-observability).
For policy tuning, see
[Operations — Workflow Orchestration](operations.md#workflow-orchestration).

---

## Spatial Query Problems

**FeatureServer bbox query**:
```bash
curl "http://localhost:8080/rest/services/1/FeatureServer/0/query?geometry=-122.5,37.7,-122.3,37.8&geometryType=esriGeometryEnvelope&spatialRel=esriSpatialRelIntersects&f=json"
```

**OGC API Features bbox query**:
```bash
curl "http://localhost:8080/ogc/features/collections/0/items?bbox=-122.5,37.7,-122.3,37.8&limit=10"
```

### CRS Mismatch

**Symptom**: queries return no features even though data exists.

```sql
SELECT ST_SRID(geometry) AS srid, COUNT(*)
FROM honua.features
WHERE geometry IS NOT NULL
GROUP BY ST_SRID(geometry);
```

Ensure your query geometry uses the same SRID as stored data. For FeatureServer, set `inSR` and `outSR` explicitly.

### Invalid Geometry

```sql
SELECT feature_id, ST_IsValid(geometry), ST_IsValidReason(geometry)
FROM honua.features
WHERE NOT ST_IsValid(geometry)
LIMIT 10;
```

Fix:
```sql
UPDATE honua.features
SET geometry = ST_MakeValid(geometry)
WHERE NOT ST_IsValid(geometry);
```

### Missing Spatial Index

```sql
EXPLAIN (ANALYZE, BUFFERS)
SELECT * FROM honua.features
WHERE ST_Intersects(geometry, ST_MakeEnvelope(-122.5, 37.7, -122.3, 37.8, 4326));
```

Fix:
```sql
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_features_geometry
ON honua.features USING gist(geometry);
```

### Oversized Queries

Tighten limits: `Limits__Query__MaxRecordCount`, `Limits__Query__MaxBboxAreaSqKm`, `Limits__Query__QueryTimeout`, `Limits__Geometry__MaxVerticesPerGeometry`, `Limits__Geometry__SimplifyTolerance`.

---

## Incident Playbooks

For production incidents, see the [Runbooks](runbooks/README.md).
