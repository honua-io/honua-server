# Deploying Honua Server

All deployment options require a PostGIS-enabled PostgreSQL database. Redis is optional (recommended for multi-node deployments). Container images are target-specific (web, Lambda, Functions) with shared app code.

## Pick a deployment path

| Scenario | Option | Guide |
|----------|--------|-------|
| **Local dev / single-server** | Docker Compose | [Docker Compose Sample](docker-compose.md) |
| **Kubernetes** | Helm chart | `infrastructure/helm/honua/README.md` |
| **AWS / Azure (managed cloud)** | Terraform (separate repo) | Use the dedicated `honua-terraform` repository |

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

Web runtime tags are published to Docker Hub (`honuaio/honua-server`) and GHCR (`ghcr.io/honua-io/honua-server`).
Serverless platform tags (`*-lambda`, `*-lambda-aot`, `*-functions`, `*-functions-aot`) are published by CI directly to cloud registries (ECR/ACR).

| Tag | Build | Use for | Registry |
|-----|-------|---------|----------|
| `vX.Y.Z-aot` | AOT | Production (recommended) | GHCR + Docker Hub |
| `vX.Y.Z` | JIT | Production (if AOT incompatible) | GHCR + Docker Hub |
| `vX.Y.Z-lambda-aot` | AOT | AWS Lambda (preferred) | ECR (and optional ACR mirror) |
| `vX.Y.Z-lambda` | JIT | AWS Lambda debug fallback | ECR (and optional ACR mirror) |
| `vX.Y.Z-functions-aot` | AOT | Azure Functions (preferred) | ACR (and optional ECR mirror) |
| `vX.Y.Z-functions` | JIT | Azure Functions debug fallback | ACR (and optional ECR mirror) |
| `latest-aot` | AOT | Development (tracks trunk) | GHCR + Docker Hub |
| `latest` | JIT | Development (tracks trunk) | GHCR + Docker Hub |
| `latest-lambda-aot` | AOT | Lambda validation / dev | ECR (and optional ACR mirror) |
| `latest-lambda` | JIT | Lambda debug fallback / dev | ECR (and optional ACR mirror) |
| `latest-functions-aot` | AOT | Functions validation / dev | ACR (and optional ECR mirror) |
| `latest-functions` | JIT | Functions debug fallback / dev | ACR (and optional ECR mirror) |
| `nightly-aot` | AOT | CI / experiments | GHCR + Docker Hub |
| `nightly` | JIT | CI / experiments | GHCR + Docker Hub |

### Platform Image Publish CI

Workflow: `.github/workflows/deploy-platform-images.yml`

- Publishes only platform tags to cloud registries (`ECR` and/or `ACR`).
- Publishes AOT platform lanes for Lambda and Functions as multi-arch (`linux/amd64`, `linux/arm64`), with JIT lanes kept for debug fallback.
- Requires at least one configured target (ECR or ACR), otherwise the workflow fails fast.
- ECR config:
  - Repository variable: `AWS_ECR_REGION` (required for ECR lane), `AWS_ECR_REPOSITORY` (optional; defaults to `honua-server`)
  - Secrets: `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `AWS_SESSION_TOKEN` (optional)
- ACR config:
  - Secret: `ACR_LOGIN_SERVER`, `ACR_USERNAME`, `ACR_PASSWORD`
  - Repository variable: `ACR_REPOSITORY` (optional; defaults to `honua-server`)

AOT images start faster and use less memory. Keep JIT serverless tags as debug fallback only.

## Production checklist

- [ ] Set `HONUA_ADMIN_PASSWORD` to a strong secret
- [ ] Use a managed PostGIS database (RDS, Azure Flexible Server) or a hardened self-hosted instance
- [ ] Enable Redis for multi-node deployments
- [ ] Terminate TLS at the ingress / load balancer
- [ ] Configure OIDC if you need browser-based admin access — see [Security](security.md)
- [ ] Set `HONUA_SKIP_MIGRATIONS=true` for serverless (run migrations out-of-band)
- [ ] Set up health check probes: `/healthz/live` (liveness), `/healthz/ready` (readiness)

## Cloud IaC Handoff

Terraform modules, examples, and validation workflows have been moved out of `honua-server`.
Use the dedicated `honua-terraform` repository for AWS/Azure infrastructure provisioning and Terraform CI.

## Related Docs

- [Deployment Scenarios](DEPLOYMENT_SCENARIOS.md)
- [Security](security.md)
- [Monitoring](monitoring.md)
