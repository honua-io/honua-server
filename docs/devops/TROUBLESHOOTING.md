# Troubleshooting

Use this page to triage common operational issues and jump to the right guide.

**Scope**: Fast diagnosis and links to deeper troubleshooting pages.

---

## Quick Triage

1. **Health**
   ```bash
   curl -f http://localhost:8080/healthz/live
   curl -f http://localhost:8080/healthz/ready
   ```

2. **Logs**
   ```bash
   docker logs --tail 200 honua-server
   ```

3. **Database connectivity**
   ```bash
   psql -h localhost -U honua -d honua -c "SELECT 1;"
   ```

4. **Metrics snapshots**
   - `GET /api/v1/metrics/performance`
   - `GET /api/v1/metrics/database`

---

## Common Issue Paths

- **Database connectivity**: [Database Connection Issues](troubleshooting/database-connection-issues.md)
- **Performance and latency**: [Performance Troubleshooting](troubleshooting/performance-troubleshooting.md)
- **Import failures**: [Import Process Issues](troubleshooting/import-process-issues.md)
- **Authentication problems**: [Authentication Troubleshooting](troubleshooting/authentication-problems.md)
- **Spatial query issues**: [Spatial Query Troubleshooting](troubleshooting/spatial-query-problems.md)

---

## Incident Playbooks

For production incidents, use the runbooks:
- [Runbooks](runbooks/README.md)
