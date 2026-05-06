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
    ports: ["8080:8080", "8081:8081"]
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
- Port `8080` is HTTP/1 REST and gRPC-Web; port `8081` is native h2c gRPC for SDK/mobile clients.
- No HA or backup automation in this setup.

---

## **Scenario 2: Small Organization (5-50 people)**

**Use Case**: Production in a single region with moderate traffic
**Infrastructure**: Managed Postgres + Redis + container orchestration

**Baseline components:**
- Managed Postgres with backups and monitoring
- Redis for caching (and **required** when running geoprocessing, ETL, or tile-cache job workloads and declarative workflow orchestration — see [Operations — Job Orchestration](operations.md#job-orchestration) and [Operations — Workflow Orchestration](operations.md#workflow-orchestration))
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
separates API-side and worker-side concerns at the service-registration level:

- `AddJobOrchestration()` registers shared queue and log store dependencies.
  Safe for a lean, request-serving image.
- `AddJobWorker()` additionally registers the execution host and
  reconciliation sweep. Intended for worker or combined-mode hosts.

**Current release:** The geoprocessing feature registration calls
`AddJobOrchestration()` to wire the shared queue and log store. The pluggable
batch-compute backend contract (`IBatchComputeBackend`) and execution-job
reconciler are registered directly in the combined host; `LocalBatchComputeBackend`
observes in-process worker progress (actual execution requires `AddJobWorker()`
wiring on a worker host). The optional `KubernetesJobBatchComputeBackend` is
also registered — when `ControlPlane:Kubernetes` is configured and the cluster
is reachable, jobs targeting `KubernetesJob` are dispatched as Kubernetes Jobs
and the reconciler observes their lifecycle. `AddJobWorker()` (queue-based
claim/execute for dedicated worker hosts) is not yet invoked from a host
entrypoint. Separate API-only and worker-only images are a planned topology
for Scenario 3 (enterprise scale-out).

In Scenarios 1–2, the combined host is the expected deployment mode. The
registration split exists so that future enterprise deployments can scale API
and worker replicas independently and keep heavyweight execution dependencies
out of the request path.

---

## **Related Documentation**

- [Security](security.md)
- [Monitoring](monitoring.md)
- [Operations](operations.md)
