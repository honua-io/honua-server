# Database Connection Issues

Use this guide when Honua can't connect to Postgres or you see connection-related errors.

**Scope**: Connection strings, Postgres availability, PostGIS, and pool limits.

---

## Quick Checks

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

---

## Common Fixes

- **Wrong host in Docker**: use the service name (e.g., `Host=postgres`).
- **Invalid credentials**: verify user, password, and database name.
- **Pool exhaustion**: reduce `Limits__Connections__MaxConcurrentQueries` or scale out.
- **Postgres max_connections**: ensure headroom for `pool_size x replicas`.

---

## Related Docs

- [Connection Pool Sizing](../connection-pool-sizing.md)
- [Performance Monitoring](../performance-monitoring.md)
- [Deployment Scenarios](../DEPLOYMENT_SCENARIOS.md)
