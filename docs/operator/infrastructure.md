# Deploying Honua Server

All deployment options require a PostGIS-enabled PostgreSQL database. Redis is optional (recommended for multi-node deployments). Container images are target-specific (web, Lambda, Functions) with shared app code.

## Pick a deployment path

| Scenario | Option | Guide |
|----------|--------|-------|
| **Local dev / single-server** | Docker Compose | [Docker Compose Sample](docker-compose.md) |
| **Kubernetes** | Helm chart | Use the separate [`honua-helm`](https://github.com/honua-io/honua-helm) repository |
| **AWS / Azure (managed cloud)** | Terraform (separate repo) | Use the dedicated `honua-terraform` repository |

If you just want to try Honua locally, the root `docker-compose.yml` in the repo root is the fastest option — see the Quick Start in the main README.

The repo keeps the default web image and local compose entrypoints at the root (`Dockerfile`, `docker-compose.yml`, `docker-compose.scale-test.yml`). Specialized container variants and supporting assets live under `docker/`.

## Control plane and GitOps direction

Honua is building its own control plane for deploy coordination and change management.

- Honua is not integrating with Flux or Argo CD as its primary rollout controller.
- Helm charts and Terraform modules remain deployment and infrastructure packaging surfaces.
- Instance-local deploy readiness, migration state, and upgrade APIs live in `honua-server`.
- Fleet rollout coordination is expected to live in the Honua control plane rather than in external GitOps products.

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
- **Redis** is optional for caching in single-node deployments (Honua falls back to in-memory caching there). Redis is required when running job orchestration workloads (geoprocessing, ETL, tile-cache jobs) or declarative workflow orchestration because the durable job queue, execution log store, reconciliation state, and workflow run/definition stores all use Redis-backed storage. Redis is also required when you use shared cloud-backed temporary files (`FileStorage=AwsS3` or `AzureBlob`) so temporary-file quotas stay correct across replicas. Enable Redis persistence (AOF recommended, or RDB with a short save interval) so that queued and in-flight job state and workflow run state survive Redis restarts; without persistence, a Redis restart loses all pending jobs and workflow runs and forces reconciliation recovery for any claimed work. See [Operations — Job Orchestration](operations.md#job-orchestration) and [Operations — Workflow Orchestration](operations.md#workflow-orchestration).
- **Object storage** (S3/MinIO) is optional, used only for file import workflows.
- **TLS and rate limiting** are handled at the edge (ALB, API Gateway, Ingress Controller). Honua does not terminate TLS.

## Container images

Web runtime tags are published to Docker Hub (`honuaio/honua-server`) and GHCR (`ghcr.io/honua-io/honua-server`).
Cloud-targeted platform tags (`*-ecs`, `*-ecs-aot`, `*-lambda`, `*-lambda-aot`, `*-functions`, `*-functions-aot`) are published by CI directly to cloud registries (ECR/ACR).

| Tag | Build | Use for | Registry |
|-----|-------|---------|----------|
| `vX.Y.Z-aot` | AOT | Production (recommended) | GHCR + Docker Hub |
| `vX.Y.Z` | JIT | Production (if AOT incompatible) | GHCR + Docker Hub |
| `vX.Y.Z-ecs-aot` | AOT | AWS ECS/Fargate (preferred) | ECR (and optional ACR mirror) |
| `vX.Y.Z-ecs` | JIT | AWS ECS/Fargate debug fallback | ECR (and optional ACR mirror) |
| `vX.Y.Z-lambda-aot` | AOT | AWS Lambda (preferred) | ECR (and optional ACR mirror) |
| `vX.Y.Z-lambda` | JIT | AWS Lambda debug fallback | ECR (and optional ACR mirror) |
| `vX.Y.Z-functions-aot` | AOT | Azure Functions (preferred) | ACR (and optional ECR mirror) |
| `vX.Y.Z-functions` | JIT | Azure Functions debug fallback | ACR (and optional ECR mirror) |
| `latest-aot` | AOT | Development (tracks trunk) | GHCR + Docker Hub |
| `latest` | JIT | Development (tracks trunk) | GHCR + Docker Hub |
| `latest-ecs-aot` | AOT | ECS validation / dev | ECR (and optional ACR mirror) |
| `latest-ecs` | JIT | ECS debug fallback / dev | ECR (and optional ACR mirror) |
| `latest-lambda-aot` | AOT | Lambda validation / dev | ECR (and optional ACR mirror) |
| `latest-lambda` | JIT | Lambda debug fallback / dev | ECR (and optional ACR mirror) |
| `latest-functions-aot` | AOT | Functions validation / dev | ACR (and optional ECR mirror) |
| `latest-functions` | JIT | Functions debug fallback / dev | ACR (and optional ECR mirror) |
| `nightly-aot` | AOT | CI / experiments | GHCR + Docker Hub |
| `nightly` | JIT | CI / experiments | GHCR + Docker Hub |

### Platform Image Publish CI

Workflow: `.github/workflows/deploy-platform-images.yml`

- Publishes only platform tags to cloud registries (`ECR` and/or `ACR`).
- Publishes AWS ECS tags (`*-ecs`, `*-ecs-aot`) as `linux/arm64` only, because Honua's ECS/Fargate default runtime target is Arm.
- Publishes Lambda tags for AWS as `linux/arm64` only, because Honua's AWS default runtime target is Arm.
- Publishes Azure Functions tags as `linux/amd64` only, because Azure Functions custom containers are treated as x86-64.
- Keeps generic web images multi-arch for Kubernetes and general container use; AKS Arm node pools should pull the `arm64` variant from the generic image family.
- Requires at least one configured target (ECR or ACR), otherwise the workflow fails fast.
- ECR config:
  - Repository variable: `AWS_ECR_REGION` (required for ECR lane), `AWS_ECR_REPOSITORY` (optional; defaults to `honua-server`)
  - Secrets: `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `AWS_SESSION_TOKEN` (optional)
- ACR config:
  - Repository variables: `ACR_LOGIN_SERVER` (required for ACR lane), `ACR_REPOSITORY` (optional; defaults to `honua-server`)
  - Secrets: `ARM_CLIENT_ID`, `ARM_CLIENT_SECRET`, `ARM_TENANT_ID`, `ARM_SUBSCRIPTION_ID`
  - ACR login is derived at runtime via `az login` + `az acr login`; do not store long-lived `ACR_USERNAME` / `ACR_PASSWORD` secrets for this workflow

AOT images start faster and use less memory. Keep JIT cloud-targeted tags as debug fallback only.

## Production checklist

- [ ] Set `HONUA_ADMIN_PASSWORD` to a strong secret
- [ ] Use a managed PostGIS database (RDS, Azure Flexible Server) or a hardened self-hosted instance
- [ ] Enable Redis for multi-node deployments (and for any deployment running job or workflow orchestration workloads)
- [ ] Enable Redis persistence (AOF or RDB) when running job or workflow orchestration to preserve queue and workflow run state across restarts
- [ ] Terminate TLS at the ingress / load balancer
- [ ] Configure OIDC if you need browser-based admin access — see [Security](security.md)
- [ ] Set `HONUA_SKIP_MIGRATIONS=true` for serverless (run migrations out-of-band)
- [ ] Set up health check probes: `/healthz/live` (liveness), `/healthz/ready` (readiness)

## Cloud IaC Handoff

Terraform modules, examples, and validation workflows have been moved out of `honua-server`.
Use the dedicated `honua-terraform` repository for AWS/Azure infrastructure provisioning and Terraform CI.

This separation is intentional: infrastructure provisioning can live outside `honua-server`, while Honua's own deploy orchestration and GitOps workflow remain part of the Honua control-plane direction.

## Post-Apply Cloud Validation

Real cloud validation should run immediately after `terraform apply`, but the checks themselves should remain close to the application code.

- Use `scripts/run-cloud-post-apply-validation.sh` from this repository to run:
  - `scripts/post-deployment-verification.sh`
  - `Category=Cloud` deployed-environment integration tests
  - optional `Category=Scale` tests when the target environment exposes the extra scale-test signals and inputs
- Use the reusable GitHub Actions workflow `.github/workflows/cloud-post-apply-validation.yml` when `honua-terraform` needs a remote post-apply hook back into `honua-server`
- Keep scale tests explicit. Do not assume every real cloud deployment exposes the nginx-specific `X-Instance-ID` headers used by the local scale harness

### Expected Environment Variables

Core deployed-environment checks:

- `HONUA_CLOUD_TEST_BASE_URL`
- `HONUA_CLOUD_TEST_ADMIN_API_KEY` for admin/control-plane checks
- `HONUA_CLOUD_TEST_EXPECTED_ENVIRONMENT` optional
- `HONUA_CLOUD_TEST_EXPECTED_DEPLOYMENT_MODE` optional
- `HONUA_CLOUD_TEST_EXPECT_READY_FOR_COORDINATED_DEPLOY` optional
- `HONUA_CLOUD_TEST_PLATFORM` optional (`kubernetes`, `aws-ecs`, `aws-lambda`, `azure-functions`, `azure-container-apps`)
- `HONUA_CLOUD_TEST_EXPECT_DEPLOY_PLAN_SUPPORT` optional (code default is `true`). When `false`, the deploy-plan test asserts a `405 Method Not Allowed` contract instead of expecting a plan payload. The validation script auto-defaults to `false` for `aws-lambda`, `azure-functions`, `azure-container-apps`, and `kubernetes`; `aws-ecs` defaults to `true` (full support). Set explicitly to override.
- `HONUA_CLOUD_TEST_EXPECT_MUTATION_SUPPORT` optional (code default is `true`). When `false`, the import mutation test is skipped entirely (the multi-endpoint import flow does not expose a single unsupported-operation contract like deploy-plan does). The validation script auto-defaults to `false` for `aws-lambda`, `azure-functions`, and `azure-container-apps`; `aws-ecs` and `kubernetes` default to `true`. Set explicitly to override.
- `HONUA_CLOUD_TEST_DEPLOY_TARGET_ID` optional; when set, enables a live `POST /api/v1/admin/deploy/plan` check against a real configured target
- `HONUA_CLOUD_TEST_DEPLOY_DESIRED_REVISION` optional
- `HONUA_CLOUD_TEST_DEPLOY_CURRENT_REVISION` optional
- `HONUA_CLOUD_TEST_IMPORT_TABLE_PREFIX` optional; when set, enables a live cloud-staged import mutation test plus publish/query round-trip
- `HONUA_CLOUD_TEST_IMPORT_TIMEOUT_SECONDS` optional
- `HONUA_CLOUD_TEST_PUBLISH_DB_HOST` required when `HONUA_CLOUD_TEST_IMPORT_TABLE_PREFIX` is set
- `HONUA_CLOUD_TEST_PUBLISH_DB_PORT` optional
- `HONUA_CLOUD_TEST_PUBLISH_DB_NAME` required when `HONUA_CLOUD_TEST_IMPORT_TABLE_PREFIX` is set
- `HONUA_CLOUD_TEST_PUBLISH_DB_USERNAME` required when `HONUA_CLOUD_TEST_IMPORT_TABLE_PREFIX` is set
- `HONUA_CLOUD_TEST_PUBLISH_DB_PASSWORD` required when `HONUA_CLOUD_TEST_IMPORT_TABLE_PREFIX` is set
- `HONUA_CLOUD_TEST_PUBLISH_DB_SSL_MODE` optional
- `HONUA_CLOUD_TEST_PUBLISH_DB_SSL_REQUIRED` optional

Optional scale checks:

- `INCLUDE_SCALE_TESTS=true`
- `HONUA_SCALE_TEST_BASE_URL`
- `HONUA_SCALE_TEST_ADMIN_API_KEY`
- `HONUA_SCALE_TEST_SERVICE_ID`
- `HONUA_SCALE_TEST_REDIS`

The post-apply runner can also hydrate these values from a Terraform output JSON file via `--terraform-output-json <path>`.

## Related Docs

- [Deployment Scenarios](DEPLOYMENT_SCENARIOS.md)
- [Security](security.md)
- [Monitoring](monitoring.md)
