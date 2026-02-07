# Deploying Honua Server

All deployment options run the same Honua container image and require a PostGIS-enabled PostgreSQL database. Redis is optional (recommended for multi-node deployments).

## Pick a deployment path

| Scenario | Option | Guide |
|----------|--------|-------|
| **Local dev / single-server** | Docker Compose | [docker-compose/](docker-compose/README.md) |
| **Kubernetes** | Helm chart | [helm/honua/](helm/honua/README.md) |
| **AWS (containers)** | Terraform — ECS/Fargate | [terraform/modules/aws-ecs/](terraform/modules/aws-ecs/README.md) |
| **Azure (containers)** | Terraform — Container Apps | [terraform/modules/azure-aca/](terraform/modules/azure-aca/README.md) |
| **AWS (serverless)** | Terraform — Lambda | [terraform/modules/aws-serverless/](terraform/modules/aws-serverless/README.md) |
| **Azure (serverless)** | Terraform — Functions | [terraform/modules/azure-functions/](terraform/modules/azure-functions/README.md) |

If you just want to try Honua locally, the root `docker-compose.yml` in the repo root is the fastest option — see the [Quick Start](../README.md#quick-start) in the main README.

## Required configuration (all deployments)

```bash
ConnectionStrings__DefaultConnection="Host=...;Database=honua;Username=...;Password=..."
HONUA_ADMIN_PASSWORD="<strong-secret>"
```

See [`.env.example`](../.env.example) for every available setting.

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

Full details: [Container Images](../docs/devops/CONTAINER_IMAGES.md)

## Production checklist

- [ ] Set `HONUA_ADMIN_PASSWORD` to a strong secret
- [ ] Use a managed PostGIS database (RDS, Cloud SQL, Azure Flexible Server) or a hardened self-hosted instance
- [ ] Enable Redis for multi-node deployments
- [ ] Terminate TLS at the ingress / load balancer
- [ ] Configure OIDC if you need browser-based admin access — see [Security Configuration](../docs/devops/SECURITY_CONFIGURATION.md)
- [ ] Set `HONUA_SKIP_MIGRATIONS=true` for serverless (run migrations out-of-band)
- [ ] Review [Operational Excellence](../docs/devops/OPERATIONAL_EXCELLENCE.md) for observability and monitoring
- [ ] Set up health check probes: `/healthz/live` (liveness), `/healthz/ready` (readiness)

## Terraform bootstrap

Before deploying with Terraform, create least-privilege service accounts:

- AWS ECS: `terraform/bootstrap/aws-ecs/`
- AWS Lambda: `terraform/bootstrap/aws-serverless/`
- Azure Container Apps: `terraform/bootstrap/azure-aca/`
- Azure Functions: `terraform/bootstrap/azure-functions/`

See [terraform/README.md](terraform/README.md) for module details and validation commands.

## Serverless constraints

Serverless runtimes (Lambda, Azure Functions) require a compatibility layer in the container image. See [Serverless Deployments](../docs/devops/serverless-deployments.md) for runtime-specific constraints and image requirements.
