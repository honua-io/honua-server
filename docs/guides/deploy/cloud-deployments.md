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

Generic web images are published to Docker Hub (`honuaio/honua-server`) and GHCR (`ghcr.io/honua-io/honua-server`). The unsuffixed release and trunk tags are the canonical native-AOT production artifact. The `-aot` generic tags remain compatibility aliases to the same image. JIT images are explicitly suffixed `-jit` and are development/conformance/debugging aids, not supported production serving artifacts.

Cloud-targeted production tags remain explicit: `*-ecs-aot`, `*-lambda-aot`, and `*-functions-aot`; their shorter `*-ecs`, `*-lambda`, and `*-functions` compatibility aliases resolve to those same verified AOT images. Their JIT debugging counterparts end in `-jit` (`*-ecs-jit`, `*-lambda-jit`, `*-functions-jit`) so a production tag can never resolve to JIT.

The serving-image boundary is strict: the native-AOT web images contain no GDAL/OGR CLI, native GDAL/PROJ/GEOS libraries, or .NET GDAL bindings. PostGIS may use its own database-side GDAL support, and native raster/ETL jobs may use the independently built `honua-worker-etl` image; neither dependency is copied into the web container.

### Which tag to pull

On the generic web image (Docker Hub + GHCR), tags have distinct contracts. Pick by whether you want the latest *release* or the latest *trunk* build:

| Tag | Means | Moves when | Published by |
|---|---|---|---|
| `latest` / `latest-aot` | Latest stable native-AOT **release** (`-aot` is an alias) | A `v*` release tag is cut | `deploy.yml` |
| `vX.Y.Z` / `vX.Y.Z-aot` | A specific native-AOT release (`-aot` is an alias) | Never (immutable) | `deploy.yml` |
| `trunk` / `trunk-aot` | Latest native-AOT **trunk** build (`-aot` is an alias) | Every nightly build | `nightly-container-build.yml` |
| `nightly`, `nightly-YYYYMMDD`, `nightly-<sha>` | Native-AOT trunk build with moving, dated, and SHA-pinned variants | Every nightly build | `nightly-container-build.yml` |
| `latest-jit`, `vX.Y.Z-jit`, `trunk-jit`, `nightly-jit*` | Non-production JIT compatibility/debug image | Corresponding release or nightly build | `deploy.yml` / `nightly-container-build.yml` |

- **Production / demos**: pull `latest` (or a pinned `vX.Y.Z`). Both are native AOT. `latest` deliberately tracks the latest *release*, not trunk, so it never silently advances to an unreleased build.
- **Trunk-following consumers** (certification harnesses, "test against current trunk" CI, bleeding-edge previews): pull `trunk`. It is the documented native-AOT moving tag for the head of the default branch. Do **not** reach for `latest` expecting trunk — it can lag a release cycle behind.
- **Compatibility debugging only**: use an explicitly suffixed `-jit` tag. Do not promote that image into production.

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

Lambda production images are built from [`docker/Dockerfile.lambda.aot`](../../../docker/Dockerfile.lambda.aot); the explicitly suffixed JIT debug image uses [`docker/Dockerfile.lambda`](../../../docker/Dockerfile.lambda). The `docker/Dockerfile.lambda.aot.simple` variant is a local diagnostic fallback and is not published. The Lambda host shim lives in [`docker/cloud`](../../../docker/cloud).

- Use the `vX.Y.Z-lambda-aot` tag (arm64) from ECR behind API Gateway or a function URL.
- Set `HONUA_SKIP_MIGRATIONS=true` and run migrations out-of-band — concurrent cold starts must not race migrations.
- Publish numeric versions and route traffic through an alias; canary weight shifting and automatic promote/rollback use the deploy backend `honua-gitops-aws-lambda`.

```bash
aws lambda update-function-code --function-name honua-prod \
  --image-uri 123456789012.dkr.ecr.us-east-1.amazonaws.com/honua-server:v1.2.3-lambda-aot
aws lambda publish-version --function-name honua-prod
```

## Azure Container Apps

- Use the generic web image (`ghcr.io/honua-io/honua-server:latest` or a pinned version); it is native AOT. The `latest-aot` alias is retained for compatibility.
- Configure secrets as Container Apps secrets referenced from env vars; ingress handles TLS.
- Rollouts use revision traffic splitting: immediate cutover or canary percentage, driven by the deploy backend `honua-azure-container-apps-revision` with a telemetry gate.

```bash
az containerapp update --name honua-prod --resource-group honua \
  --image ghcr.io/honua-io/honua-server:v1.2.3-aot
```

## Azure Functions

Functions production images are built from [`docker/Dockerfile.functions.aot`](../../../docker/Dockerfile.functions.aot) (amd64); the explicitly suffixed JIT debug image uses [`docker/Dockerfile.functions`](../../../docker/Dockerfile.functions). The Functions host shim lives in [`docker/cloud`](../../../docker/cloud).

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

Expected: the script runs `scripts/cloud/post-deployment-verification.sh` plus the `Category=Cloud` integration tests and exits 0. For a plain readiness check, open `https://honua.example.com/healthz/ready` in a browser and expect `Ready`.

## Troubleshoot

- **Serverless cold starts time out** — use the `-aot` image variants and confirm `HONUA_SKIP_MIGRATIONS=true`; migrations during cold start are the usual culprit.
- **Job/import endpoints return `503`** — durable jobs, queued imports, and workflows require Redis (ElastiCache / Azure Cache for Redis); serverless patterns without Redis don't host them.
- **Deploy-plan validation fails on Lambda/Functions/Container Apps** — full deploy-plan support is ECS-first; set `HONUA_CLOUD_TEST_EXPECT_DEPLOY_PLAN_SUPPORT=false` (the script auto-defaults this per platform).
- **Admin calls return 401** — confirm the secret store value actually reaches the container env as `HONUA_ADMIN_PASSWORD` and requests send it in the `X-API-Key` header.

## Next steps

- [Upgrade and roll back](upgrade-and-rollback.md)
- [Monitor Honua Server](monitoring.md)
- [Scale and tune performance](scaling-and-performance.md)
