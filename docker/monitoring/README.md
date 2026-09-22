# Monitoring Docker Assets

Reusable self-hosted monitoring assets for Docker-based environments, plus a curated
Grafana + Prometheus bundle that deploys in one command.

The root quickstart includes a profiled Honua Console Operate service at
`http://localhost:5174/operate` once a compatible Console image is published.
Use this monitoring bundle when you want
external Prometheus storage, Grafana dashboards, or alert-rule experiments beyond
the built-in Console view.

## Quick start

With a Honua server already running (for example the root `docker compose up -d`,
listening on `:8080`):

**1. Mint a read-only scrape key.** `/metrics` is served under the `Admin`
authorization policy, and a scrape is a `GET` — a safe method — so an API key with
the read-only `admin:read` grant is the narrowest credential that can read it. With a
full-admin credential, `POST /api/v1/admin/api-keys` with
`{"name":"prometheus-scrape","permissions":["admin:read"]}` (see
[Authenticate clients](../../docs/guides/secure/authentication.md#2-create-scoped-api-keys-for-automation)),
and write the returned `data.key` — shown once — into a file only you can read:

```bash
umask 077
printf '%s' "$HONUA_SCRAPE_KEY" > ./honua-scrape-key
```

Do not reuse `HONUA_ADMIN_PASSWORD` as the scrape credential: it carries full admin
write authority and a scrape only needs to read. Create the file before starting the
stack — Docker creates a *directory* at a bind-mount source that does not exist yet.
The compose bundle copies the file into a named volume with Prometheus' UID and
read-only `0440` mode, so the host copy can remain `0600`.

**2. Start the stack.** Both variables are required — the bundle refuses to start
without them, rather than falling back to a shipped default:

```bash
export HONUA_MONITORING_GRAFANA_PASSWORD='<a password you choose>'
export HONUA_METRICS_SCRAPE_KEY_FILE="$PWD/honua-scrape-key"
docker compose -f docker/monitoring/compose.yml up -d
```

Then open:

- **Grafana** — http://127.0.0.1:3000 (user `admin`, the password you exported). The
  Prometheus datasource and three curated dashboards are provisioned automatically.
- **Prometheus** — http://127.0.0.1:9090.

Prometheus reads the key from the private named-volume file at scrape time and sends it
in the `X-API-Key` header; nothing in `prometheus/prometheus.yml` holds a credential.
Edit that file to change the scrape target (the default is `host.docker.internal:8080`).

**Reaching the server.** `host.docker.internal` resolves to the Docker host's bridge
gateway, so the default target only works when the server publishes on an interface that
gateway can reach. The repository-root quickstart publishes on loopback only, so either
start it with `HONUA_BIND_ADDRESS=0.0.0.0` behind your own network controls, or put the
bundle on the server's compose network and scrape the service name (`honua:8080`)
instead — the latter keeps every published port on loopback.

Published ports bind to loopback by default, matching the repository-root compose
file. Widen that only deliberately, behind your own network controls:

```bash
HONUA_BIND_ADDRESS=0.0.0.0 docker compose -f docker/monitoring/compose.yml up -d
```

Override host ports without editing the file:

```bash
HONUA_MONITORING_GRAFANA_PORT=3001 HONUA_MONITORING_PROMETHEUS_PORT=9091 \
  docker compose -f docker/monitoring/compose.yml up -d
```

Prometheus runs without `--web.enable-lifecycle`: the admin lifecycle routes
(`/-/reload`, `/-/quit`) are unauthenticated when enabled, so the bundle keeps them
off and reloads by restarting the container instead.

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
