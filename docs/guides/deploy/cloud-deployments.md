# Deploy on AWS and Azure

You'll pick a managed-cloud deployment pattern for Honua — ECS/Fargate, Lambda, Azure Container Apps, or Azure Functions — and know which image, configuration, and rollout mechanism each one uses.

**Prerequisites:** A PostGIS database (RDS or Azure Database for PostgreSQL Flexible Server), the required env vars from [Configure Honua Server](configuration.md) (`ConnectionStrings__DefaultConnection`, `HONUA_ADMIN_PASSWORD`, `Security__ConnectionEncryption__MasterKey`, `Cors__AllowedOrigins__0`), and TLS terminated at the edge (ALB, API Gateway, Front Door) — Honua does not terminate TLS.

Infrastructure-as-code for all of these patterns ships as private Terraform modules (honua-iac), available to customers through support.

## Pick a pattern

| Pattern | Best for | Image family | Rollout mechanism |
|---|---|---|---|
| AWS ECS/Fargate + ALB | Steady production traffic | `*-ecs-aot` (arm64) | ALB weighted target groups (canary) |
| AWS Lambda | Spiky/low traffic, scale-to-zero | `*-lambda-aot` (arm64) | Alias weighted versions (canary) |
| Azure Container Apps | Steady production traffic on Azure | generic web image | Revision traffic splitting (canary) |
| Azure Functions | Spiky/low traffic on Azure | `*-functions-aot` (amd64) | Staging slot swap (atomic) |
| Kubernetes (EKS/AKS) | Existing cluster estate | generic web image (multi-arch) | See [Deploy on Kubernetes](kubernetes.md) |

Generic web images are published to Docker Hub (`honuaio/honua-server`) and GHCR (`ghcr.io/honua-io/honua-server`). Cloud-targeted tags (`*-ecs`, `*-lambda`, `*-functions`, each with `-aot` variants) are published by CI to ECR/ACR. Prefer the AOT tags — faster start, lower memory; keep JIT tags as a debug fallback.

### Which tag to pull

On the generic web image (Docker Hub + GHCR), tags have distinct contracts. Pick by whether you want the latest *release* or the latest *trunk* build:

| Tag | Means | Moves when | Published by |
|---|---|---|---|
| `latest` / `latest-aot` | Latest stable **release** | A `v*` release tag is cut | `deploy.yml` |
| `vX.Y.Z` / `vX.Y.Z-aot` | A specific pinned release | Never (immutable) | `deploy.yml` |
| `trunk` / `trunk-aot` | Latest **trunk** build (HEAD of the default branch) | Every nightly build | `nightly-container-build.yml` |
| `nightly` / `nightly-aot`, `nightly-YYYYMMDD`, `nightly-<sha>` | Same trunk build as `trunk`/`trunk-aot`, plus dated/sha-pinned variants | Every nightly build | `nightly-container-build.yml` |

- **Production / demos**: pull `latest` (or a pinned `vX.Y.Z`). `latest` deliberately tracks the latest *release*, not trunk, so it never silently advances to an unreleased build.
- **Trunk-following consumers** (certification harnesses, "test against current trunk" CI, bleeding-edge previews): pull `trunk` / `trunk-aot`. These are the documented, obviously-named moving tags for the head of the default branch and are refreshed by the nightly build. Do **not** reach for `latest` expecting trunk — it can lag a release cycle behind.

## Pattern by team size

- **Dev/test (1–5 people)**: single host with [Docker Compose](docker-compose.md); no HA, local volumes.
- **Production, single region (5–50)**: managed Postgres with backups, Redis (required for durable jobs/queued imports/workflows), one container service from the table above, edge TLS and rate limiting.
- **Enterprise (50+)**: multi-region or active/standby, database replication and failover, global load balancing/WAF, centralized logging and alerting. Honua replicas stay stateless in every tier — scale-out is adding replicas.

## AWS ECS/Fargate

The default AWS runtime target is arm64 Fargate behind an ALB.

- Use the `vX.Y.Z-ecs-aot` tag from ECR; container ports 8080 (HTTP) and 8081 (h2c gRPC).
- Health checks: ALB target group on `/healthz/ready`; container health on `/healthz/live`.
- Inject secrets via ECS task-definition `secrets` from Secrets Manager or SSM Parameter Store.
- Canary rollouts: provision a stable and a canary ECS service on two ALB target groups, then drive weighted traffic shifts through Honua's deploy API (backend `honua-aws-ecs-alb`) with an automatic telemetry gate — see [Upgrade and roll back](upgrade-and-rollback.md).

```bash
aws ecs describe-services --cluster honua-prod --services honua-server \
  --query 'services[0].deployments[].{status:status,running:runningCount,taskDef:taskDefinition}'
```

## AWS Lambda

Lambda images are built from [`docker/Dockerfile.lambda`](../../../docker/Dockerfile.lambda) and [`docker/Dockerfile.lambda.aot`](../../../docker/Dockerfile.lambda.aot) (a `docker/Dockerfile.lambda.aot.simple` variant also exists); the Lambda host shim lives in [`docker/cloud`](../../../docker/cloud).

- Use the `vX.Y.Z-lambda-aot` tag (arm64) from ECR behind API Gateway or a function URL.
- Set `HONUA_SKIP_MIGRATIONS=true` and run migrations out-of-band — concurrent cold starts must not race migrations.
- Publish numeric versions and route traffic through an alias; canary weight shifting and automatic promote/rollback use the deploy backend `honua-gitops-aws-lambda`.

```bash
aws lambda update-function-code --function-name honua-prod \
  --image-uri 123456789012.dkr.ecr.us-east-1.amazonaws.com/honua-server:v1.2.3-lambda-aot
aws lambda publish-version --function-name honua-prod
```

## Azure Container Apps

- Use the generic web image (`ghcr.io/honua-io/honua-server:latest-aot` or a pinned version).
- Configure secrets as Container Apps secrets referenced from env vars; ingress handles TLS.
- Rollouts use revision traffic splitting: immediate cutover or canary percentage, driven by the deploy backend `honua-azure-container-apps-revision` with a telemetry gate.

```bash
az containerapp update --name honua-prod --resource-group honua \
  --image ghcr.io/honua-io/honua-server:v1.2.3-aot
```

## Azure Functions

Functions images are built from [`docker/Dockerfile.functions`](../../../docker/Dockerfile.functions) and [`docker/Dockerfile.functions.aot`](../../../docker/Dockerfile.functions.aot) (amd64); the Functions host shim lives in [`docker/cloud`](../../../docker/cloud).

- Use the `vX.Y.Z-functions-aot` tag from ACR as a custom container.
- Set `HONUA_SKIP_MIGRATIONS=true` and run migrations out-of-band, as with Lambda.
- Rollouts are atomic staging-slot swaps via the deploy backend `honua-gitops-azure-functions`; a post-swap telemetry breach triggers an automatic reverse swap.

```bash
az functionapp config container set --name honua-prod --resource-group honua \
  --image honuaprod.azurecr.io/honua-server:v1.2.3-functions-aot
```

## Ops control plane and batch-compute backends

**Redis is a hard dependency for the ops control plane.** Durable jobs, queued imports, deploy workflows, and the operation gateway are all backed by Redis (ElastiCache / Azure Cache for Redis). Without it these surfaces fail closed — job/import/workflow endpoints return `503` rather than silently running node-local work — and the ops-findings recommended-action gateway reports a degraded, unavailable state. Provision Redis for any environment that runs jobs, imports, workflows, or the deploy control plane; single-host dev/test via Docker Compose is the only tier where you can skip it.

**The local batch-compute backends are single-host only.** The in-process `local` backend and the child-process `honua-local-process` pool track launched jobs in an in-process registry that cannot survive a host restart or be observed from another node. They are the zero-dependency executors for single-host / air-gapped deployments; they **cannot** work on:

- a **serverless** substrate (Lambda, Functions, Cloud Run), whose process and filesystem are frozen or torn down between invocations, or
- a **multi-node** deployment without a shared work directory, where a job launched on one node is invisible to its siblings.

On those substrates, route geoprocessing/import workloads to a remote batch backend (`honua-aws-batch`, Azure Batch, or a Kubernetes Job) instead. Declare the substrate so the server can fail closed rather than churn: set `ControlPlane:Substrate:Profile` to `MultiNode` or `Serverless` (serverless runtimes are also auto-detected from `AWS_LAMBDA_FUNCTION_NAME` / `FUNCTIONS_WORKER_RUNTIME` / `K_SERVICE`), and `ControlPlane:Substrate:SharedWorkDir=true` if a multi-node deployment provides shared storage. When a local backend is registered on an incompatible substrate, the server raises a persistent Critical ops finding (`local-backend-substrate-incompatible`, visible via `GET /monitoring/health/comprehensive` and the ops-findings feed) instead of silently re-queuing doomed jobs.

## Verify

After `terraform apply`, run the post-apply validation suite from this repository against the deployed environment:

```bash
HONUA_CLOUD_TEST_BASE_URL=https://honua.example.com \
HONUA_CLOUD_TEST_ADMIN_API_KEY=replace-with-admin-password \
./scripts/cloud/run-cloud-post-apply-validation.sh
```

Expected: the script runs `scripts/cloud/post-deployment-verification.sh` plus the `Category=Cloud` integration tests and exits 0. A plain readiness check also works: `curl -s https://honua.example.com/healthz/ready` returns `Ready`.

## Troubleshoot

- **Serverless cold starts time out** — use the `-aot` image variants and confirm `HONUA_SKIP_MIGRATIONS=true`; migrations during cold start are the usual culprit.
- **Job/import endpoints return `503`** — durable jobs, queued imports, and workflows require Redis (ElastiCache / Azure Cache for Redis); serverless patterns without Redis don't host them.
- **Deploy-plan validation fails on Lambda/Functions/Container Apps** — full deploy-plan support is ECS-first; set `HONUA_CLOUD_TEST_EXPECT_DEPLOY_PLAN_SUPPORT=false` (the script auto-defaults this per platform).
- **Admin calls return 401** — confirm the secret store value actually reaches the container env as `HONUA_ADMIN_PASSWORD` and requests send it in the `X-API-Key` header.

## Next steps

- [Upgrade and roll back](upgrade-and-rollback.md)
- [Monitor Honua Server](monitoring.md)
- [Scale and tune performance](scaling-and-performance.md)
