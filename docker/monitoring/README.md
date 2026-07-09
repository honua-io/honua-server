# Monitoring Docker Assets

Reusable self-hosted monitoring assets for Docker-based environments, plus a curated
Grafana + Prometheus bundle that deploys in one command.

The root quickstart includes a profiled Honua Console Operate service at
`http://localhost:5174/operate` once a compatible Console image is published.
Use this monitoring bundle when you want
external Prometheus storage, Grafana dashboards, or alert-rule experiments beyond
the built-in Console view.

## Quick start (one command)

With a Honua server already running (for example the root `docker compose up -d`,
listening on `:8080`), bring up the curated observability stack:

```bash
docker compose -f docker/monitoring/compose.yml up -d
```

Then open:

- **Grafana** — http://localhost:3000 (default `admin` / `admin`). The Prometheus
  datasource and three curated dashboards are provisioned automatically.
- **Prometheus** — http://localhost:9090.

The stack scrapes the server's `/metrics` endpoint. That endpoint requires admin
authorization; the API-key auth handler also accepts HTTP Basic credentials, so
Prometheus authenticates via `basic_auth` using the admin password as the Basic
password. Edit `prometheus/prometheus.yml` to change the scrape target and password
(the default matches the root compose `HONUA_ADMIN_PASSWORD` of
`quickstart-admin-password` and target `host.docker.internal:8080`).

Override host ports without editing the file:

```bash
HONUA_MONITORING_GRAFANA_PORT=3001 HONUA_MONITORING_PROMETHEUS_PORT=9091 \
  docker compose -f docker/monitoring/compose.yml up -d
```

## Curated dashboards

Provisioned from `grafana/dashboards/` into the **Honua** folder:

- **Honua Serving Overview** (`honua-serving-overview`) — GIS-aware p95/p99 by
  protocol + operation (`honua_serving_request_duration_ms`), request throughput,
  error rate including in-band (transport-masked) errors, cache hit ratio, and DB
  connection-pool saturation.
- **Honua GP / Jobs Overview** (`honua-gp-jobs-overview`) — execution-job queue depth
  by status + backend (`honua_execution_queue_depth`), job duration percentiles,
  outcomes, submissions by backend, and reconcile-cycle outcomes.
- **Honua Ops / Alerts Overview** (`honua-ops-alerts-overview`) — alert-pipeline
  backlog, dead-letters, delivery latency, and dispatch outcomes. These panels
  consume `honua_alerts_*` from the alerts-GA workstream (#2468) and show *No data*
  until that build is deployed.

The pre-existing focused dashboards (`honua-overview`, `honua-database`,
`honua-tile-cache`, `honua-feature-edits`, `honua-errors-by-protocol`,
`honua-slow-traces`, `honua-otel-runtime`, `honua-audit-log`) are also provisioned.

## Layout

- `compose.yml` - curated one-command Grafana + Prometheus bundle.
- `prometheus/prometheus.yml` - scrape config for the bundle (Honua `/metrics` + self).
- `prometheus/alerts.yml` - bundled Prometheus alert rules (mounted by the bundle and
  by the scale-test stack).
- `grafana/dashboards/` - Grafana dashboard provisioning and dashboard JSON.
- `grafana/datasources/` - Grafana datasource provisioning.

The local scale-test stack (`docker/scale-test/compose.yml`) also mounts these files.
Operator docs reference them as examples for self-hosted Prometheus and Grafana
deployments (see `docs/guides/deploy/monitoring.md`).
