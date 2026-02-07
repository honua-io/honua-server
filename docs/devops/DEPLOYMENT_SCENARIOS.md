# Deployment Scenarios

This guide outlines practical deployment setups for different team sizes and operational needs. It is intentionally concise and points to deeper references.

---

## **Choose Your Scenario**

Use these quick signals:
- **Dev/Test only**: single node, Docker Compose
- **Prod single region**: managed database + container orchestration
- **Enterprise**: multi-region, HA database, full observability stack

---

## **Scenario 1: Development Team (1-5 people)**

**Use Case**: Local development or internal testing
**Infrastructure**: Single machine or small VM

**Minimal Docker Compose (trimmed):**
```yaml
services:
  honua:
    image: honuaio/honua-server:latest
    ports: ["8080:8080"]
    environment:
      - ConnectionStrings__DefaultConnection=Host=postgres;Database=honua;Username=honua;Password=honua_password
    depends_on: [postgres]

  postgres:
    image: postgis/postgis:16-3.4
    environment:
      - POSTGRES_DB=honua
      - POSTGRES_USER=honua
      - POSTGRES_PASSWORD=honua_password
```

**Start and verify:**
```bash
docker compose up -d
curl http://localhost:8080/healthz/ready
```

**Notes:**
- Use local volumes for data.
- No HA or backup automation in this setup.

---

## **Scenario 2: Small Organization (5-50 people)**

**Use Case**: Production in a single region with moderate traffic
**Infrastructure**: Managed Postgres + Redis + container orchestration

**Baseline components:**
- Managed Postgres with backups and monitoring
- Redis for caching
- Container runtime (Kubernetes or managed container service)
- Edge TLS termination and rate limiting

**Key configuration themes:**
- Separate secrets from config
- Enable health probes and metrics
- Set reasonable database pool sizes

---

## **Scenario 3: Enterprise (50+ people)**

**Use Case**: High availability and multi-region workloads
**Infrastructure**: Multi-region Kubernetes, managed database with replicas, global ingress

**Baseline components:**
- Multi-region cluster or active/standby
- Global load balancing and WAF
- Database replication and automated failover
- Centralized logging, tracing, and alerting

**Operational notes:**
- Keep rate limiting at the edge
- Use a dedicated secret manager
- Define SLOs for latency and availability

---

## **Operational Notes**

- **TLS**: Terminate at the edge (nginx, ALB, gateway).
- **Rate limiting**: Enforce at the edge; application-level rate limiting is intentionally deferred.
- **Backups**: Use managed snapshots plus point-in-time recovery where possible.
- **Monitoring**: Track request latency, error rates, and database saturation.

---

## **Related Documentation**

- [Security Configuration](SECURITY_CONFIGURATION.md)
- [Performance Monitoring](performance-monitoring.md)
- [Operational Excellence](OPERATIONAL_EXCELLENCE.md)
- [Backup and Restore](backup-restore.md)
