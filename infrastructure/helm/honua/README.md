# Honua Helm Chart

Deploys Honua Server on Kubernetes with optional Bitnami PostgreSQL and Redis subcharts.

## Quick start

```bash
helm dependency update infrastructure/helm/honua
helm install honua infrastructure/helm/honua \
  --set secret.env.ConnectionStrings__DefaultConnection="Host=postgres;Database=honua;Username=honua;Password=honua" \
  --set secret.env.HONUA_ADMIN_PASSWORD="change-me" \
  --set config.env.HONUA_SERVE_ADMIN_UI="true" \
  --set config.env.HONUA_ADMIN_UI="true"
```

## Production example

Create a `values-prod.yaml`:

```yaml
replicaCount: 3

image:
  repository: ghcr.io/honua-io/honua-server
  tag: "v1.2.3-aot"   # Pin to a release AOT tag
  pullPolicy: IfNotPresent

resources:
  requests:
    cpu: 500m
    memory: 512Mi
  limits:
    cpu: "2"
    memory: 2Gi

autoscaling:
  enabled: true
  minReplicas: 2
  maxReplicas: 20
  targetCPUUtilizationPercentage: 70
  targetMemoryUtilizationPercentage: 80
  behavior:
    scaleUp:
      stabilizationWindowSeconds: 60
      policies:
        - type: Percent
          value: 50
          periodSeconds: 60
    scaleDown:
      stabilizationWindowSeconds: 300
      policies:
        - type: Percent
          value: 10
          periodSeconds: 60

ingress:
  enabled: true
  className: nginx   # or alb, traefik, etc.
  annotations:
    cert-manager.io/cluster-issuer: letsencrypt-prod
  hosts:
    - host: gis.example.com
      paths:
        - path: /
          pathType: Prefix
  tls:
    - secretName: honua-tls
      hosts:
        - gis.example.com

config:
  env:
    HONUA_SERVE_ADMIN_UI: "true"
    HONUA_ADMIN_UI: "true"
    HONUA_OBSERVABILITY: "true"
    HONUA_OPENTELEMETRY: "true"
    ASPNETCORE_ENVIRONMENT: "Production"
    ASPNETCORE_URLS: "http://+:8080"
    Public__BaseUrl: "https://gis.example.com"

secret:
  env:
    ConnectionStrings__DefaultConnection: "Host=postgis.internal;Database=honua;Username=honua;Password=<secret>;SSL Mode=Require"
    HONUA_ADMIN_PASSWORD: "<strong-secret>"
    ConnectionStrings__redis: "redis.internal:6379"
```

Deploy with:

```bash
helm dependency update infrastructure/helm/honua
helm upgrade --install honua infrastructure/helm/honua -f values-prod.yaml
```

## External PostGIS database (recommended for production)

For production, point `ConnectionStrings__DefaultConnection` at a managed PostGIS database (e.g., Amazon RDS, Azure Flexible Server) rather than using the Bitnami subchart.

> The Bitnami PostgreSQL subchart **does not include PostGIS**. Honua requires PostGIS for migrations and spatial queries. For anything beyond local development, use an external PostGIS-enabled database.

## PostgreSQL subchart (dev only)

```bash
helm upgrade --install honua infrastructure/helm/honua \
  --set postgresql.enabled=true \
  --set postgresql.auth.username=honua \
  --set postgresql.auth.password=honua \
  --set postgresql.auth.database=honua \
  --set secret.env.HONUA_ADMIN_PASSWORD="change-me"
```

When `postgresql.enabled=true`, the chart auto-populates `ConnectionStrings__DefaultConnection` if you don't supply one.

## Redis subchart

```bash
helm upgrade --install honua infrastructure/helm/honua \
  --set redis.enabled=true \
  --set secret.env.HONUA_ADMIN_PASSWORD="change-me"
```

When `redis.enabled=true`, the chart auto-populates `ConnectionStrings__redis`.

## AOT vs JIT images

The chart defaults to AOT (`latest-aot`). AOT images start faster and use less memory. To use JIT instead:

```bash
helm upgrade --install honua infrastructure/helm/honua \
  --set image.tag=latest
```

For production, pin to a release tag: `v1.2.3-aot` (AOT) or `v1.2.3` (JIT).

## Using an existing secret

Instead of chart-managed secrets, reference a pre-existing Kubernetes secret:

```yaml
secret:
  create: false
  name: my-honua-secret   # Must contain ConnectionStrings__DefaultConnection and HONUA_ADMIN_PASSWORD
```

## Key values

| Value | Default | Description |
|-------|---------|-------------|
| `replicaCount` | 1 | Number of pods. Use 3+ for production. |
| `image.tag` | `latest-aot` | Image tag. AOT recommended. Pin to `vX.Y.Z-aot` for production. |
| `resources` | `{}` | CPU/memory requests and limits. **Set for production.** |
| `autoscaling.enabled` | false | Enable HPA. |
| `autoscaling.targetCPUUtilizationPercentage` | `70` | CPU utilization threshold for scale decisions. |
| `autoscaling.targetMemoryUtilizationPercentage` | `80` | Memory utilization threshold for scale decisions. |
| `autoscaling.behavior` | scale up/down policies | autoscaling/v2 behavior policies and stabilization windows. |
| `minReadySeconds` | `10` | Minimum time a pod must stay Ready before rollout proceeds. |
| `deploymentStrategy` | RollingUpdate (`maxUnavailable: 0`, `maxSurge: 1`) | Default rollout strategy tuned for no-downtime upgrades. |
| `podDisruptionBudget.enabled` | false | Create a PodDisruptionBudget for voluntary disruptions. |
| `podDisruptionBudget.minAvailable` | `1` | Minimum ready pods to keep available when PDB is enabled. |
| `ingress.enabled` | false | Enable ingress. |
| `config.env.*` | — | Non-secret environment variables (stored in ConfigMap). |
| `secret.env.*` | — | Secret environment variables (stored in Secret). |
| `secret.name` | `""` | Reference an existing secret instead of chart-managed. |
| `extraEnv` | `[]` | Additional env vars from external sources (e.g. `valueFrom`). |
| `postgresql.enabled` | false | Enable Bitnami PostgreSQL subchart (dev only). |
| `redis.enabled` | false | Enable Bitnami Redis subchart. |

See `values.yaml` for the complete reference.

## Health checks

The chart configures probes on:
- **Liveness**: `/healthz/live` (is the process alive?)
- **Readiness**: `/healthz/ready` (is the database connected?)
- **Startup**: `/healthz/live` with 30 retries (initial boot tolerance)

## Upgrade safety defaults

The chart ships with conservative rollout defaults for production-style upgrades:
- `RollingUpdate` with `maxUnavailable: 0` and `maxSurge: 1`
- `minReadySeconds: 10` so a pod must stay healthy briefly before rollout continues
- optional `podDisruptionBudget` support for voluntary disruption protection

For single-replica installs, these settings still protect startup/readiness behavior but cannot guarantee zero client-visible downtime. For production zero-downtime upgrades, run at least two replicas and keep database migrations backward-compatible across one rollout window.

## Geospatial HPA tuning guidance

The default HPA thresholds are tuned for mixed geospatial workloads:
- `targetCPUUtilizationPercentage: 70` for CPU-heavy spatial predicates and tile generation.
- `targetMemoryUtilizationPercentage: 80` for bursty map rendering and large feature payloads.
- `scaleUp` stabilization of 60s with 50% growth to react quickly to traffic ramps.
- `scaleDown` stabilization of 300s with 10% shrink to avoid thrash after short spikes.

For dataset-specific tuning:
- Increase `maxReplicas` only after validating PostgreSQL connection limits.
- Raise memory targets (for example 85-90) if large map exports are common and pod OOM is not observed.
- Reduce `scaleDown` aggressiveness further for workloads with repeated 3-10 minute query bursts.

## Local validation

```bash
helm dependency update infrastructure/helm/honua
helm lint infrastructure/helm/honua
helm template honua infrastructure/helm/honua
helm test honua  # After install, runs the test hook
```

For ingress testing on a local Kubernetes cluster, see [K3d + Helm guide](../../../docs/contributor/development/k3d-helm.md).
