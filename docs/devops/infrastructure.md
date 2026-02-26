# Deploying Honua Server

All deployment options run the same Honua container image and require a PostGIS-enabled PostgreSQL database. Redis is optional (recommended for multi-node deployments).

## Pick a deployment path

| Scenario | Option | Guide |
|----------|--------|-------|
| **Local dev / single-server** | Docker Compose | [Docker Compose Sample](docker-compose.md) |
| **Kubernetes** | Helm chart | `infrastructure/helm/honua/README.md` |
| **AWS (containers)** | Terraform — ECS/Fargate | `infrastructure/terraform/modules/aws-ecs/` |
| **Azure (containers)** | Terraform — Container Apps | `infrastructure/terraform/modules/azure-aca/` |
| **AWS (serverless)** | Terraform — Lambda | `infrastructure/terraform/modules/aws-serverless/` |
| **Azure (serverless)** | Terraform — Functions | `infrastructure/terraform/modules/azure-functions/` |

If you just want to try Honua locally, the root `docker-compose.yml` in the repo root is the fastest option — see the Quick Start in the main README.

## Required configuration (all deployments)

```bash
ConnectionStrings__DefaultConnection="Host=...;Database=honua;Username=...;Password=..."
HONUA_ADMIN_PASSWORD="<strong-secret>"
```

See `.env.example` in the repo root for every available setting.

## Architecture overview

```
                    ┌─────────────────────┐
                    │   Ingress / Edge     │  TLS, rate limiting, WAF
                    │  (ALB, NGINX, etc.)  │
                    └─────────┬───────────┘
                              │
                    ┌─────────▼───────────┐
                    │   Honua Server      │  Stateless container (AOT recommended)
                    │   (port 8080)       │
                    └──┬──────────────┬───┘
                       │              │
              ┌────────▼──────┐  ┌────▼─────┐
              │  PostGIS DB   │  │  Redis   │  (optional)
              │  (required)   │  │  cache   │
              └───────────────┘  └──────────┘
```

- **Honua Server** is a stateless container. Scale horizontally by adding replicas.
- **PostGIS** is the only required dependency. All protocols read from and write to the same database.
- **Redis** is optional. Without it, Honua falls back to in-memory caching (fine for single-node).
- **Object storage** (S3/MinIO) is optional, used only for file import workflows.
- **TLS and rate limiting** are handled at the edge (ALB, API Gateway, Ingress Controller). Honua does not terminate TLS.

## Container images

Published to Docker Hub (`honuaio/honua-server`) and GHCR (`ghcr.io/honua-io/honua-server`).

| Tag | Build | Use for |
|-----|-------|---------|
| `vX.Y.Z-aot` | AOT | Production (recommended) |
| `vX.Y.Z` | JIT | Production (if AOT incompatible) |
| `latest-aot` | AOT | Development (tracks trunk) |
| `latest` | JIT | Development (tracks trunk) |
| `nightly-aot` | AOT | CI / experiments |
| `nightly` | JIT | CI / experiments |

AOT images start faster and use less memory. Use JIT only if you encounter AOT compatibility issues.

## Production checklist

- [ ] Set `HONUA_ADMIN_PASSWORD` to a strong secret
- [ ] Use a managed PostGIS database (RDS, Azure Flexible Server) or a hardened self-hosted instance
- [ ] Enable Redis for multi-node deployments
- [ ] Terminate TLS at the ingress / load balancer
- [ ] Configure OIDC if you need browser-based admin access — see [Security](security.md)
- [ ] Set `HONUA_SKIP_MIGRATIONS=true` for serverless (run migrations out-of-band)
- [ ] Set up health check probes: `/healthz/live` (liveness), `/healthz/ready` (readiness)

## Terraform bootstrap

Before deploying with Terraform, create least-privilege service accounts. Bootstrap templates are in the `infrastructure/terraform/bootstrap/` directory for each cloud provider.

## Terraform validation

For AWS/Azure/Kubernetes integration testing (including Redis, PostGIS raster checks, scale checks, and auto-destroy defaults), use the on-demand runbook:

- [Terraform Validation Runbook](terraform-validation.md)

## Related Docs

- [Deployment Scenarios](DEPLOYMENT_SCENARIOS.md)
- [Security](security.md)
- [Monitoring](monitoring.md)
- [Terraform Validation Runbook](terraform-validation.md)
