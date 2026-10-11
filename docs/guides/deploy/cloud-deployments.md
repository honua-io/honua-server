---
type: guide
title: "Deploy on AWS and Azure"
description: "You'll pick a managed-cloud deployment pattern for Honua — ECS/Fargate, Lambda, Azure Container Apps, or Azure Functions — and know which image, configuration, and rollout mechanism each one uses."
---
# Deploy on AWS and Azure

You'll pick a managed-cloud deployment pattern for Honua — ECS/Fargate, Lambda, Azure Container Apps, or Azure Functions — and know which image, configuration, and rollout mechanism each one uses.

**Prerequisites:** A PostGIS database (RDS or Azure Database for PostgreSQL Flexible Server), the required env vars from [Configure Honua Server](configuration.md) (`ConnectionStrings__DefaultConnection`, `HONUA_ADMIN_PASSWORD`, `Security__ConnectionEncryption__MasterKey`, `Cors__AllowedOrigins__0`), and TLS terminated at the edge (ALB, API Gateway, Front Door) — Honua does not terminate TLS.

**The infrastructure-as-code for every pattern below is [honua-iac](https://github.com/honua-io/honua-iac),
and it is public.** Start with its
[operator deployment guide](https://github.com/honua-io/honua-iac/blob/trunk/docs/operator-deployment.md) — this page explains
*which* pattern to pick and what each one implies; that guide is how you stand one up.

Modules are consumed by Git source pinned to a SemVer tag rather than from the public
Terraform Registry, which the Elastic License 2.0 precludes
([the recorded decision](https://github.com/honua-io/honua-iac/blob/trunk/docs/module-publishing-decision.md)):

```hcl
module "honua" {
  source = "git::https://github.com/honua-io/honua-iac.git//infrastructure/terraform/modules/aws-ecs?ref=<tag>"
  # ...module inputs...
}
```

See [how modules are versioned and consumed](https://github.com/honua-io/honua-iac/blob/trunk/docs/module-versioning.md) for the
ref policy, and [deployment presets](https://github.com/honua-io/honua-iac/blob/trunk/docs/deployment-presets.md) for sizing.

## Pick a pattern

| Pattern | Best for | Image family | Rollout mechanism | honua-iac module |
|---|---|---|---|---|
| AWS ECS/Fargate + ALB | Steady production traffic | generic web image (multi-arch index; runs `x86_64`) | ALB weighted target groups (canary) | `aws-ecs` |
| AWS Lambda | Spiky/low traffic, scale-to-zero | `*-lambda-aot` (single-arch `x86_64`/amd64) | Alias weighted versions (canary) | `aws-serverless` |
| Azure Container Apps | Steady production traffic on Azure | generic web image | Revision traffic splitting (canary) | `azure-aca` |
| Azure Functions | Spiky/low traffic on Azure | `*-functions-aot` (amd64) | Staging slot swap (atomic) | `azure-functions` |
| Kubernetes (EKS/AKS) | Existing cluster estate | generic web image (multi-arch) | See [Deploy on Kubernetes](kubernetes.md) | `aws-eks`, `azure-aks` |

The database and observability tiers are separate modules: `aws-data` / `azure-data`
for the managed PostGIS, and `observability-stack`.

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

> **No `v*` release tag has been cut yet**, so no `vX.Y.Z` image exists and `latest` has
> never moved. Until the first release, pin a digest or a dated `nightly-YYYYMMDD`
> tag — both are immutable. The getting-started pages use the 2026.1 release channel
> `ghcr.io/honua-io/honua-server:2026.1-rc` (`ghcr.io/honua-io/honua-server:2026.1` at GA), which only release
> promotion moves. The exact digest is in the release's `customer-install-manifest.json`
> (`server.image`).

- **Production / demos**: pull `latest` (or a pinned `vX.Y.Z`). Both are native AOT. `latest` deliberately tracks the latest *release*, not trunk, so it never silently advances to an unreleased build.
- **Trunk-following consumers** (certification harnesses, "test against current trunk" CI, bleeding-edge previews): pull `trunk`. It is the documented native-AOT moving tag for the head of the default branch. Do **not** reach for `latest` expecting trunk — it can lag a release cycle behind.
- **Compatibility debugging only**: use an explicitly suffixed `-jit` tag. Do not promote that image into production.

## Pattern by team size

- **Dev/test (1–5 people)**: single host with [Docker Compose](docker-compose.md); no HA, local volumes.
- **Production, single region (5–50)**: managed Postgres with backups, Redis (required for durable jobs/queued imports/workflows), one container service from the table above, edge TLS and rate limiting.
- **Enterprise (50+)**: multi-region or active/standby, database replication and failover, global load balancing/WAF, centralized logging and alerting. Honua replicas stay stateless in every tier — scale-out is adding replicas.

## Serve Honua on your own domain

Honua runs on any domain you own: any FQDN, any TLD, no `honua.io` name required. Three settings
make a deployment correct on that domain:

- **`Public__BaseUrl`** (or `PUBLIC_BASE_URL`): the public origin, for example
  `https://gis.example.com`. Honua builds its absolute links (OGC API and STAC self links, OGC
  capabilities operation URLs, admin OIDC redirect URIs) from it rather than from the request
  `Host` header. Without it, those links fall back to a loopback origin such as `http://localhost`, and
  admin OIDC refuses to start outside Development. The value must be an absolute `http`/`https` URL;
  a port and a path prefix are kept (`https://example.org:8443/gis`) and a trailing `/` is ignored.
  A bare host name such as `gis.example.com` is **ignored** (links fall back to loopback), so
  always include the scheme. Studio publication URLs (`activeUrl`, `publicationUrl`) are
  root-relative and resolve against whatever origin the client used.
- **Host validation**: outside Development the server answers only allowed hosts. With no
  `HostValidation__AllowedHosts__<n>` list, the host of `Public__BaseUrl` is allowed. With a list,
  include your domain in it.
- **`Cors__AllowedOrigins__<n>`**: the browser origins on your domain (Console, your web app), not
  the API host.

The honua-iac AWS modules set `Public__BaseUrl` for you when you give them a custom domain:
`aws-ecs` and `aws-serverless` render `https://<domain_name>` (the Lambda module also adds the
domain to host validation), and `aws-eks` puts it in `chart_config_env` for the Helm chart's
`config.env`. Each takes a `public_base_url` input that overrides it, for example when a CDN on your
domain fronts the service, and a `Public__BaseUrl` you already pass in `additional_env` is kept.
On Azure, Helm, Docker Compose, and other targets, set `Public__BaseUrl` yourself (the production
Compose file derives it from `HONUA_HOST`).

## AWS ECS/Fargate

The release-certified AWS runtime target is `x86_64` Fargate behind an ALB. The release
manifest (`platform-manifest.yaml` in honua-release) pins the generic multi-arch web image
index — not an `*-ecs-aot` tag — with `awsEcsArchitecture: x86_64`, and the `aws-ecs`
module defaults to `X86_64` to match. ARM64 Fargate is not certified for 2026.1: it stays
excluded until it is re-established on a live Fargate task (honua-release#98).

- Use the generic web image at the release digest (mirrored to ECR if your account requires it), with the task definition's `runtimePlatform.cpuArchitecture` set to `X86_64`; container ports 8080 (HTTP) and 8081 (h2c gRPC).
- Health checks: ALB target group on `/healthz/ready`; container health on `/healthz/live`.
- Inject secrets via ECS task-definition `secrets` from Secrets Manager or SSM Parameter Store.
- Canary rollouts: provision a stable and a canary ECS service on two ALB target groups, then drive weighted traffic shifts through Honua's deploy API (backend `honua-aws-ecs-alb`) with an automatic telemetry gate — see [Upgrade and roll back](upgrade-and-rollback.md).

```bash
aws ecs describe-services --cluster honua-prod --services honua-server \
  --query 'services[0].deployments[].{status:status,running:runningCount,taskDef:taskDefinition}'
```

## AWS Lambda

Lambda production images are built from [`docker/Dockerfile.lambda.aot`](../../../docker/Dockerfile.lambda.aot); the explicitly suffixed JIT debug image uses [`docker/Dockerfile.lambda`](../../../docker/Dockerfile.lambda). The `docker/Dockerfile.lambda.aot.simple` variant is a local diagnostic fallback and is not published. The Lambda host shim lives in [`docker/cloud`](../../../docker/cloud).

- Use the single-architecture `x86_64` (amd64) `*-lambda-aot` image from same-region ECR behind API Gateway or a function URL, with the function's architecture set to `x86_64`. The release manifest pins `awsLambdaArchitecture: x86_64`, and the 2026.1 Lambda GA bill rejects any other Lambda architecture.
- Set `HONUA_SKIP_MIGRATIONS=true` and run migrations out-of-band — concurrent cold starts must not race migrations.
- Publish numeric versions and route traffic through an alias; canary weight shifting and automatic promote/rollback use the deploy backend `honua-gitops-aws-lambda`.
- `v*` release tags publish the Lambda AOT image as `vX.Y.Z-lambda-aot-amd64` (the `x86_64` variant, which the unsuffixed `vX.Y.Z-lambda-aot` tag also resolves to) alongside an uncertified `vX.Y.Z-lambda-aot-arm64`, matching the nightly `nightly-lambda-aot-<sha7>-amd64` shape.

```bash
aws lambda update-function-code --function-name honua-prod \
  --image-uri 123456789012.dkr.ecr.us-east-1.amazonaws.com/honua-server:v1.2.3-lambda-aot
aws lambda publish-version --function-name honua-prod
```

### Keep settings out of the 4 KB Lambda environment

AWS Lambda caps a function's environment variables at 4 KB in total. A Redis-on release cell already
uses most of it, so move settings into a **settings document**: one Secrets Manager secret whose
value is a JSON object of configuration keys. The function environment then carries a single
reference:

```bash
HONUA_SETTINGS_DOCUMENT=aws:secretsmanager:honua/prod/settings
```

`Settings__Document` is an accepted alternative spelling; setting both to different values refuses
startup. A secret name resolves in the function's own account and region (`AWS_REGION`, which
Lambda always sets); use the full ARN for a secret in another account or region.

Store the document as the secret's string value. Nested objects map to `Section:Key` exactly like
`appsettings.json`, arrays map to `Section:0`, `Section:1`, and flat `Section__Key` names work too.
This document moves CORS origins, the operation-policy rules, and the Bedrock and Amazon Location
settings out of the environment:

```json
{
  "Cors": {
    "AllowedOrigins": ["https://maps.example.com", "https://console.example.com"]
  },
  "Operations": {
    "Policy": {
      "Enabled": true,
      "DefaultDecision": "Allow",
      "Rules": [
        { "OperationId": "service.publish", "Decision": "RequireApproval", "ApprovalLane": "release" },
        { "OperationId": "*", "Role": "viewer", "Decision": "Deny" }
      ]
    }
  },
  "StudioAiProxy": {
    "Enabled": true,
    "DefaultProvider": "bedrock",
    "Providers": {
      "bedrock": {
        "Kind": "bedrock",
        "Region": "us-west-2",
        "Model": "<bedrock-model-or-inference-profile-id>"
      }
    }
  },
  "Geocoding": {
    "Providers": {
      "AmazonLocation": {
        "Enabled": true,
        "Region": "us-east-1",
        "PlaceIndexName": "honua-prod-places",
        "UseIamRole": true
      }
    }
  },
  "AuditLog__ChainVerification__Key": "aws:secretsmanager:honua/prod/audit-chain-key"
}
```

How the document behaves:

- **Precedence:** `appsettings*.json` < settings document < environment variables < command line.
  Any variable still set on the function overrides the same key in the document, so a one-off
  override never needs a document edit.
- **References inside the document:** a value can itself be an `aws:secretsmanager:` reference.
  Startup resolves it exactly as if it had come from the environment (the admin password,
  connection-encryption master key, audit-chain key, Redis and database connection strings,
  license content, key-ring certificate). Keep key material in its own secret and reference it;
  do not paste plaintext keys into the document.
- **Fail-closed:** an unreadable secret, a value that is not JSON, a root that is not a JSON
  object, a document larger than 64 KB, a key set twice, or a document that names another document
  refuses startup. The error names the document reference, never its content.
- **Logging:** startup logs one Information line with the number of keys loaded and the document
  reference. Values are never logged.
- **Reload:** the document is read once at cold start. A new version takes effect on the next
  cold start (publish a new function version to force one).

The execution role needs read access to the document secret, and to every secret the document
references:

```json
{
  "Effect": "Allow",
  "Action": "secretsmanager:GetSecretValue",
  "Resource": [
    "arn:aws:secretsmanager:us-east-1:123456789012:secret:honua/prod/settings-*",
    "arn:aws:secretsmanager:us-east-1:123456789012:secret:honua/prod/audit-chain-key-*"
  ]
}
```

If the secret uses a customer-managed KMS key, also grant `kms:Decrypt` on that key.

### How the control-plane event functions dispatch

The three control-plane event functions run with `ControlPlane__TriggerMode=Event`, and `HONUA_CONTROL_PLANE_LAMBDA_HANDLER` picks the handler: `batch-event` (alias `reconcile`) reconciles the execution job named by an EventBridge "Batch Job State Change" event's `detail.jobId`, `backstop` runs one execution-job sweep and one workflow-operation sweep, and `scheduled-tick` runs the tick named by the schedule's `{"kind": "<ScheduledTickKind>"}` input. By default they run the `*-lambda-aot` image. Its Lambda Web Adapter posts every non-HTTP invocation as raw JSON to `POST /events` on `127.0.0.1:8080` (the adapter's `AWS_LWA_PASS_THROUGH_PATH`, default `/events`), and the server maps that route only when `HONUA_CONTROL_PLANE_LAMBDA_HANDLER` is set. The route runs the handler in-process and answers only loopback callers, so it needs no token. On these functions host validation also accepts a loopback Host header on a connection that is loopback at both ends, because the adapter's requests never carry the public hosts in `HostValidation__AllowedHosts`. The API function sets no selector, so it keeps strict host validation and has no `/events` route. An unknown selector returns `400` with `control_plane_lambda_handler_unknown`. The optional dedicated image, [`docker/Dockerfile.controlplane-lambda`](../../../docker/Dockerfile.controlplane-lambda), runs the thin `Honua.ControlPlane.Lambda` bootstrap. It accepts the same selector values and forwards each one to the token-guarded `/internal/control-plane/...` routes, so it also needs `ControlPlane__EventToken` and `ControlPlane__ScheduledTickToken`.

### Budget database connections on Lambda

Every Lambda instance has its own connection pool, and an instance that is frozen between invocations runs nothing, pool pruning included, so it keeps its idle connections open. The three control-plane event functions (`batch-event` reconcile, `backstop`, `scheduled-tick`) are invoked by EventBridge and EventBridge Scheduler every minute, often on new instances, and on a small RDS class they can use up every connection slot (`53300: remaining connection slots are reserved`, after which new instances abort at the PostGIS preflight). The event functions therefore close their pooled database connections at the end of every invocation. This is `ControlPlane:Lambda:ReleaseConnectionsAfterInvocation` (`ControlPlane__Lambda__ReleaseConnectionsAfterInvocation`). It defaults to `true` when `HONUA_CONTROL_PLANE_LAMBDA_HANDLER` is set and `false` everywhere else, including the API function, which keeps its pool warm between requests. Each function's pool is still capped by `Limits__Connections__MaxConnectionPoolSize`: the `aws-serverless` module sets 8 for the API (`lambda_db_max_pool_size`), 2 for each control-plane function (`control_plane_db_max_pool_size`), and 4 for each GP Batch job (`gp_batch_db_max_pool_size`). Reserved concurrency caps how many instances each function can run. The worst case is the sum over functions of reserved concurrency times pool size, plus the Batch workers; keep that below the instance's `max_connections`. For production, put RDS Proxy between the functions and the database so that instance churn never reaches the database's connection limit.

### Start degraded when the database is out of connections

If the database is unreachable when an instance cold-starts, the instance starts in degraded mode instead of exiting. This covers no free connection slots (SQLSTATE `53300` or any other class `53` code), `57P03`, class `08` connection errors, timeouts and socket failures. Exiting would make every route return a 5xx without CORS headers and spin up more instances. The startup PostGIS preflight retries such a failure twice within about 1.5 seconds. It then reports the database as unavailable, not incompatible. Degraded start is on by default when `AWS_LAMBDA_FUNCTION_NAME` is set. An explicit `Database:StartupResilience:DegradedStartEnabled` or `HONUA_DB_DEGRADED_START` overrides that default, and `false` restores fail-fast. A degraded instance answers `/healthz/live` with 200 and `/healthz/ready` with 503 and the `X-Honua-Readiness-Reason: database-startup-degraded` header. The body names the startup phase and the failure class, for example `PostGIS preflight: SQLSTATE 53300 (class 53: insufficient resources)`, and never includes the connection string or the provider message. A background loop retries with backoff. Once a check succeeds it runs the deferred migrations, or the journal/schema guard when `HONUA_SKIP_MIGRATIONS=true`, and readiness turns ready. A database that is reachable but incompatible still stops startup, for example when the PostGIS extension is missing, the password is wrong or the schema guard refuses. The API function and the control-plane event functions run the same server startup, so both behave this way. Hosts outside Lambda, GP Batch jobs included, keep the fail-fast default unless you turn degraded start on.

### Session settings behind RDS Proxy

By default Honua sets `lock_timeout`, `statement_timeout` and `idle_in_transaction_session_timeout` (and `search_path` when `Database__Schema` is set) with a `SET` on every new database connection. Behind RDS Proxy a session-level `SET` [pins](https://docs.aws.amazon.com/AmazonRDS/latest/UserGuide/rds-proxy-pinning.html) the client connection to one database connection until the client disconnects. The proxy still caps the total number of database connections, but it cannot multiplex: demand above its ceiling waits up to the borrow timeout.

To let the proxy multiplex, move the settings onto the database role and tell Honua to stop setting them:

1. As an administrator, on the direct (non-proxy) endpoint, set the defaults on the role Honua connects as. The values below are the `Limits__Connections__*` defaults; use your configured values.

   ```sql
   ALTER ROLE "honua" SET lock_timeout = '30s';
   ALTER ROLE "honua" SET statement_timeout = '30s';
   ALTER ROLE "honua" SET idle_in_transaction_session_timeout = '60s';
   -- only when Database__Schema is set, for example to "honua":
   ALTER ROLE "honua" SET search_path = "honua", public;
   ```

   Role defaults apply to new sessions only. RDS Proxy keeps its database connections open, so let them recycle (or reboot the proxy's target) before you rely on the new values.

2. Set `Database__SessionSettings__Mode=role-defaults` on every function and job that connects through the proxy. Honua then issues no session `SET` on its main data source. The default, `connection`, keeps today's behaviour; use it for direct connections.

3. Optionally set `Database__QueryCache__EnableAutomaticCaching=false`. Honua's prepared-statement cache prepares frequently run queries on the server, and a server-side prepared statement is session state the proxy can also pin on.

At startup, in `role-defaults` mode, Honua reads the effective values once and logs a warning (event 5870) for each setting that is unset or differs from `Limits__Connections__*`, with the exact `ALTER ROLE` statement to run. It never fails startup over this and never runs `ALTER ROLE` itself. `GET /api/v1/admin/deploy/preflight?includeDiagnostics=true` reports the mode, the verification result (`verified`, `mismatch`, `pending` or `unavailable`) and the same statements under `databaseSessionSettings`. The mode covers the `DefaultConnection` data source only: named secure connections (`Database__SecureConnection__Name`), source-bound data sources, migrations and the PostGIS preflight still set their own values. With schema-per-tenant routing (`MultiTenancy__SchemaRouting__Enabled=true`) Honua still sets `search_path` per request, which pins.

See [Connection poolers and proxies](../../reference/configuration/data-sources/postgis.md#connection-poolers-and-proxies-rds-proxy-pgbouncer).

## Azure Container Apps

- Use the generic web image (`ghcr.io/honua-io/honua-server` at a pinned digest or dated `nightly-YYYYMMDD` tag until the first release; `latest` afterwards); it is native AOT. The `latest-aot` alias is retained for compatibility.
- Configure secrets as Container Apps secrets referenced from env vars; ingress handles TLS.
- Rollouts use revision traffic splitting: immediate cutover or canary percentage, driven by the deploy backend `honua-azure-container-apps-revision` with a telemetry gate.

```bash
az containerapp update --name honua-prod --resource-group honua \
  --image ghcr.io/honua-io/honua-server:nightly-YYYYMMDD
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

**Managed Redis is supported: AWS ElastiCache, Amazon MemoryDB and Azure Cache for Redis.** Honua needs Redis to be reachable and working. A particular persistence policy is not required (owner ruling, 2026-10-10). On ECS and Lambda, point `ConnectionStrings__redis` at the ElastiCache or MemoryDB endpoint, with TLS and auth as the cluster requires. Also supply the operation key-ring certificate, which every Redis-backed non-development deployment needs (`Operations__SecretChannel__KeyRingCertificatePath`, or `Operations__SecretChannel__KeyRingCertificatePkcs12` from Secrets Manager on Lambda).

**Durability is a property of your Redis deployment.** Choose it to match how much acknowledged job and proposal state you can afford to lose:

| Service | Durability you can rely on |
|---|---|
| Amazon MemoryDB | A durable Multi-AZ transaction log; acknowledged writes survive node failure |
| ElastiCache (Redis OSS / Valkey) | Replication with Multi-AZ failover plus optional snapshots; Redis 7 offers no AOF, so the last writes before a failure can be lost |
| Self-managed Redis | `appendonly yes`, `appendfsync everysec` (or `always`), `maxmemory-policy noeviction` |

At startup the server tries to read the policy (`INFO persistence` plus `CONFIG GET` for `appendonly`, `appendfsync` and `maxmemory-policy`). It publishes the result as information, never as a gate. The capability manifest (`GET /api/v1/capabilities/manifest`) shows it under `limits.job.redisDurability`:

- `status: "attested"`: the policy was read and is durable.
- `status: "unverified"`, `cause: "RedisAttestationUnavailable"`: the policy could not be read. ElastiCache and MemoryDB block `CONFIG`, so this is what a managed cell reports. No action is needed.
- `status: "not-durable"`, with `RedisPersistenceDisabled`, `RedisWritePolicyUnsafe` or `RedisEvictionPolicyUnsafe`: the policy was read and is not durable.

In all three cases `operations.proposals` and `jobs.runner` report `available: true`, `limits.job.durableJobRuntimeAvailable` is `true`, and the `redis` health entry stays `Healthy` with the outcome in its data. Those capabilities read `dependency-unavailable` only when Redis is absent or did not connect at startup, and `license-required` when it is unentitled. `Jobs__RequireDurableStore=true` makes the server refuse to start unless the status is `attested`. ElastiCache and MemoryDB can never satisfy it, so leave it unset there.

On **serverless** substrates a frozen or torn-down process keeps no state of its own, so Redis is the only home for acknowledged job and proposal state. Choose MemoryDB, or accept ElastiCache's replication-and-snapshot durability knowingly.

**The local batch-compute backends are single-host only.** The in-process `local` backend and the child-process `honua-local-process` pool track launched jobs in an in-process registry that cannot survive a host restart or be observed from another node. They are the zero-dependency executors for single-host / air-gapped deployments; they **cannot** work on:

- a **serverless** substrate (Lambda, Functions, Cloud Run), whose process and filesystem are frozen or torn down between invocations, or
- a **multi-node** deployment without a shared work directory, where a job launched on one node is invisible to its siblings.

On those substrates, route geoprocessing/import workloads to a remote batch backend (`honua-aws-batch`, Azure Batch, or a Kubernetes Job) instead. Declare the substrate so the server can fail closed rather than churn: set `ControlPlane:Substrate:Profile` to `MultiNode` or `Serverless` (serverless runtimes are also auto-detected from `AWS_LAMBDA_FUNCTION_NAME` / `FUNCTIONS_WORKER_RUNTIME` / `K_SERVICE`), and `ControlPlane:Substrate:SharedWorkDir=true` if a multi-node deployment provides shared storage. When a local backend is registered on an incompatible substrate, the server raises a persistent Critical ops finding (`local-backend-substrate-incompatible`, visible via `GET /monitoring/health/comprehensive` and the ops-findings feed) instead of silently re-queuing doomed jobs.

**The remote batch worker runs a worker-only profile.** Every remote backend (AWS Batch, Azure Batch, a Kubernetes Job, the local process pool) launches the same server image for one job and passes `HONUA_OPERATION_ID`, `HONUA_JOB_KIND`, `HONUA_WORKLOAD_NAME`, `HONUA_EXECUTION_ATTEMPT` and `HONUA_CONTRACT_VERSION`. Workload `env.*` parameters cannot override these. When `HONUA_OPERATION_ID` is set, the process starts as a single-job execution worker. It claims only the launched attempt in the shared Redis job store, runs its executor, records the result or the typed failure there, and exits. It serves no HTTP and runs no migrations. It never submits jobs to a batch backend, runs the control-plane reconcilers or scheduled background services, or starts the proposal gateway. Give the worker the same database, `ConnectionStrings__redis` and operation key-ring certificate as the server. It refuses to start without Redis, because it could not report the job. It does not need `batch:SubmitJob`. On a Kubernetes Job, give the worker those credentials through Secret references; see [Geoprocessing on Kubernetes Jobs](kubernetes.md#geoprocessing-on-kubernetes-jobs).

## Verify

After `terraform apply`, open `https://honua.example.com/healthz/ready` and expect `Ready`, then
publish and query a layer through the public hostname as in the
[quickstart](../../get-started/quickstart.md#4-publish-a-table-and-query-it-back). The honua-iac
repository's [validation lanes](https://github.com/honua-io/honua-iac/blob/trunk/docs/devops/terraform-validation.md) run the same checks
against a deployed environment.

## Troubleshoot

- **Serverless cold starts time out** — use the `-aot` image variants and confirm `HONUA_SKIP_MIGRATIONS=true`; migrations during cold start are the usual culprit.
- **Job/import endpoints return `503`** — durable jobs, queued imports, and workflows require Redis (ElastiCache / Azure Cache for Redis); serverless patterns without Redis don't host them.
- **Logs show "Redis durability attestation: unverified" or "not-durable"**: this is information. The governed control plane and jobs stay enabled. `unverified` is expected on ElastiCache and MemoryDB, which block `CONFIG`. `not-durable` means the policy was read and does not protect acknowledged writes. Change it only if you need that durability (see the durability paragraphs above).
- **Admin calls return 401** — confirm the secret store value actually reaches the container env as `HONUA_ADMIN_PASSWORD` and requests send it in the `X-API-Key` header.

## Next steps

- [Upgrade and roll back](upgrade-and-rollback.md)
- [Monitor Honua Server](monitoring.md)
- [Scale and tune performance](scaling-and-performance.md)
