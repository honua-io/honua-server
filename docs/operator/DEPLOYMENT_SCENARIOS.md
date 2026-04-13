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
- Redis for caching (and **required** when running geoprocessing, ETL, or tile-cache job workloads — see [Operations — Job Orchestration](operations.md#job-orchestration))
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

---

## **API/Worker Host Separation**

Honua's durable job orchestration substrate
([ADR-0031](../contributor/adr/0031-durable-job-orchestration-substrate.md))
separates API-side and worker-side concerns:

- **API-only image**: Registers shared queue and log store
  (`AddJobOrchestration`). Stays lean — no execution or reconciliation
  overhead. Suitable for request-serving replicas.
- **Worker or combined image**: Additionally registers the execution host and
  reconciliation sweep (`AddJobWorker`). Claims and runs queued
  geoprocessing, ETL, and tile-cache jobs.

In Scenarios 1–2, a combined image running both API and worker is typical. In
Scenario 3, consider separating API-serving replicas from worker replicas to
scale them independently and keep heavyweight execution dependencies out of the
request path.

---

## **Related Documentation**

- [Security](security.md)
- [Monitoring](monitoring.md)
- [Operations](operations.md)
