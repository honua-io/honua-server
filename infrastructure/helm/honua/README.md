# Honua Helm Chart

This chart deploys Honua Server on Kubernetes with optional Bitnami PostgreSQL and Redis subcharts.

## Quick start

```bash
helm dependency update infrastructure/helm/honua
helm install honua infrastructure/helm/honua \
  --set secret.env.ConnectionStrings__DefaultConnection="Host=postgres;Database=honua;Username=honua;Password=honua" \
  --set secret.env.HONUA_ADMIN_PASSWORD="change-me" \
  --set config.env.HONUA_ADMIN_UI="true"
```

## AOT vs JIT images
Set the image tag to a published AOT or JIT build:

```bash
helm upgrade --install honua infrastructure/helm/honua \
  --set image.repository=ghcr.io/honua-io/honua-server \
  --set image.tag=nightly-aot
```

## PostgreSQL subchart (optional)

```bash
helm upgrade --install honua infrastructure/helm/honua \
  --set postgresql.enabled=true \
  --set postgresql.auth.username=honua \
  --set postgresql.auth.password=honua \
  --set postgresql.auth.database=honua \
  --set secret.env.HONUA_ADMIN_PASSWORD="change-me"
```

When `postgresql.enabled=true`, the chart auto-populates `ConnectionStrings__DefaultConnection` if you do not supply it.

Note: Honua requires PostGIS for migrations. The Bitnami PostgreSQL subchart does not include PostGIS.
For full functionality, point `ConnectionStrings__DefaultConnection` at a PostGIS-enabled database.

## Redis subchart (optional)

```bash
helm upgrade --install honua infrastructure/helm/honua \
  --set redis.enabled=true \
  --set secret.env.HONUA_ADMIN_PASSWORD="change-me"
```

When `redis.enabled=true`, the chart auto-populates `ConnectionStrings__redis` if you do not supply it.

## Required configuration

In non-development environments, Honua requires:
- `ConnectionStrings__DefaultConnection`
- `HONUA_ADMIN_PASSWORD`

You can supply these via `secret.env` (chart-managed secret) or by pointing `secret.name` to an existing secret.

## Health checks

The chart wires probes to:
- `/healthz/live`
- `/healthz/ready`

## Local validation

```bash
helm dependency update infrastructure/helm/honua
helm lint infrastructure/helm/honua
helm template honua infrastructure/helm/honua
```

For ingress testing on a local Kubernetes cluster, see `docs/contributor/development/k3d-helm.md`.

Run the Helm test hook:

```bash
helm test honua
```
