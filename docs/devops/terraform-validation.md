# Terraform Validation Runbook

This runbook defines the on-demand Terraform validation flow for Honua across Azure, AWS, and Kubernetes.

## Scope

Validation is executed manually when Terraform changes are ready to verify. There is no nightly Terraform apply/destroy schedule in this flow.

## What gets validated

The workflow and scripts cover:

- Static validation: `terraform fmt`, `terraform init -backend=false`, `terraform validate`
- Policy/security gates: `tflint`, `checkov`, `tfsec`, and custom guard checks in `scripts/terraform-policy-gate.sh`
- Azure live integration: `examples/azure-data` bootstrap (Postgres + Redis) by default, then ACA + Functions using those existing connections; includes Redis wiring, PostGIS + raster checks, protocol/admin smoke checks, admin CRUD/query smoke (`create connection -> publish layer -> query`), idempotency, quick scale check, DB resilience drill, plan artifacts, and auto-destroy + leak check
- AWS live integration: ECS + serverless, Redis wiring, PostGIS + raster checks, protocol/admin smoke checks, admin CRUD/query smoke (`create connection -> publish layer -> query`), idempotency, quick scale check, DB resilience drill, plan artifacts, and auto-destroy + leak check
- Kubernetes live integration: k3d + Helm + observability Terraform module, Helm static validation (`lint` + `template` + `kubeconform`), PostGIS + raster checks, protocol/admin smoke checks, admin CRUD/query smoke (`create connection -> publish layer -> query`), idempotency, quick scale check, and optional DB resilience drill
- Managed Kubernetes integration: AKS and EKS Terraform cluster provisioning, then Kubernetes validation flow, then auto-destroy + leak check
- Drift detection: `terraform plan -detailed-exitcode` via `scripts/run-terraform-drift-detection.sh`

## Manual GitHub Actions workflow

Workflow: `.github/workflows/terraform-manual-validation.yml`

Dispatch inputs (10 total, within GitHub limit):

- `cloud`: `both|azure|aws`
- `deployment_profile`: `ephemeral|persistent`
- `apply_confirmation`: must be `APPROVED` when `deployment_profile=persistent`
- `run_live`: enable/disable live apply tests
- `run_k8s`: include local k3d Kubernetes validation
- `run_aks`: include AKS validation
- `run_eks`: include EKS validation
- `run_drift`: include drift detection job
- `no_destroy`: keep live resources after tests
- `allow_destroy_plan`: allow apply when plan contains destroys

Advanced controls (regions, stacks, SLO/cost caps, optional skips) are configured via repository variables instead of extra dispatch inputs.

## Required GitHub secrets

Common:

- `HONUA_ADMIN_PASSWORD`
- `HONUA_DB_PASSWORD`

Azure live / AKS:

- `ARM_CLIENT_ID`
- `ARM_CLIENT_SECRET`
- `ARM_TENANT_ID`
- `ARM_SUBSCRIPTION_ID`

AWS live / EKS:

- `AWS_ACCESS_KEY_ID`
- `AWS_SECRET_ACCESS_KEY`
- `AWS_SESSION_TOKEN` (optional)
- `HONUA_AWS_SERVERLESS_IMAGE` (required when `HONUA_AWS_VALIDATION_STACK` includes `serverless`; default stack is `both`)

Optional image override secrets:

- Azure app images: `HONUA_ACA_IMAGE`, `HONUA_FUNCTIONS_IMAGE` (ACR URI with `*-functions-aot` recommended; `*-functions` is debug fallback)
- Azure rollback images: `HONUA_ACA_PREVIOUS_IMAGE`, `HONUA_FUNCTIONS_PREVIOUS_IMAGE`
- AWS app images: `HONUA_AWS_ECS_IMAGE`, `HONUA_AWS_SERVERLESS_IMAGE` (ECR URI, `*-lambda-aot` recommended; `*-lambda` is debug fallback)
- AWS rollback images: `HONUA_AWS_ECS_PREVIOUS_IMAGE`, `HONUA_AWS_SERVERLESS_PREVIOUS_IMAGE`
- Kubernetes app images: `HONUA_K8S_IMAGE`, `HONUA_K8S_PREVIOUS_IMAGE`

## Recommended repository variables

- Region and stack selection:
  - `HONUA_AZURE_VALIDATION_REGION`, `HONUA_AWS_VALIDATION_REGION`
  - `HONUA_AZURE_VALIDATION_STACK` (`aca|functions|both`)
  - `HONUA_AWS_VALIDATION_STACK` (`ecs|serverless|both`)
- Cost/SLO:
  - `HONUA_MAX_RUN_COST_USD`
  - `HONUA_READY_SLO_SECONDS`
  - `HONUA_MAX_LOAD_ERROR_RATE_PERCENT`
  - `HONUA_TTL_HOURS`
- Optional behavior toggles:
  - `HONUA_USE_AOT` (`true|false`; switches default images to `latest-aot` in validation scripts)
  - `HONUA_AZURE_FUNCTIONS_AOT_AUTOSWITCH` (`true|false`; defaults to `true` for AOT-first Functions image selection)
  - `HONUA_RUN_UPGRADE_ROLLBACK`
  - `HONUA_SKIP_DB_RESILIENCE`
  - `HONUA_SKIP_QUOTA_PREFLIGHT`
  - `HONUA_SKIP_HELM_STATIC_VALIDATION`
  - `HONUA_SKIP_OBSERVABILITY`
  - `HONUA_SKIP_IDEMPOTENCY`
  - `HONUA_SKIP_PROTOCOL_CHECKS`
  - `HONUA_SKIP_SCALE_CHECK`
- Optional existing dependency reuse (faster/cheaper validation runs):
  - `HONUA_AZURE_EXISTING_DB_FQDN`
  - `HONUA_AZURE_EXISTING_DB_CONNECTION_STRING`
  - `HONUA_AZURE_EXISTING_REDIS_CONNECTION_STRING`
  - `HONUA_AWS_EXISTING_DB_ENDPOINT`
  - `HONUA_AWS_EXISTING_DB_CONNECTION_STRING`
  - `HONUA_AWS_EXISTING_REDIS_CONNECTION_STRING`
- Drift:
  - `HONUA_DRIFT_ROOTS`
  - `HONUA_DRIFT_VAR_FILES`

## Manual run examples

CLI:

```bash
gh workflow run terraform-manual-validation.yml \
  -f cloud=both \
  -f deployment_profile=ephemeral \
  -f apply_confirmation= \
  -f run_live=true \
  -f run_k8s=true \
  -f run_aks=true \
  -f run_eks=true \
  -f run_drift=true \
  -f no_destroy=false \
  -f allow_destroy_plan=false
```

Local script entry points:

```bash
# Default flow provisions examples/azure-data first, then runs compute stack validation.
./scripts/run-azure-terraform-integration.sh --stack both
./scripts/run-azure-terraform-integration.sh --stack both --aot
./scripts/run-azure-terraform-integration.sh \
  --stack aca \
  --existing-db-fqdn mypg.postgres.database.azure.com \
  --existing-db-connection "Host=mypg.postgres.database.azure.com;Port=5432;Database=honua;Username=honua;Password=***;SSL Mode=Require;Trust Server Certificate=false" \
  --existing-redis-connection "myredis.redis.cache.windows.net:6380,password=***,ssl=True,abortConnect=False"
./scripts/run-aws-terraform-integration.sh --stack both
./scripts/run-aws-terraform-integration.sh --stack ecs --aot
./scripts/run-aws-terraform-integration.sh --stack serverless --serverless-image "<account>.dkr.ecr.<region>.amazonaws.com/honua-server:latest-lambda-aot"
./scripts/run-aws-terraform-integration.sh \
  --stack ecs \
  --existing-db-endpoint mydb.xxxxxxxxxxxx.us-east-1.rds.amazonaws.com \
  --existing-db-connection "Host=mydb.xxxxxxxxxxxx.us-east-1.rds.amazonaws.com;Port=5432;Database=honua;Username=honua;Password=***;SSL Mode=Require;Trust Server Certificate=false" \
  --existing-redis-connection "mycache.xxxxxx.use1.cache.amazonaws.com:6379,password=***,ssl=true"
./scripts/run-k8s-terraform-integration.sh
./scripts/run-k8s-terraform-integration.sh --aot
./scripts/run-aks-terraform-integration.sh
./scripts/run-eks-terraform-integration.sh
./scripts/terraform-policy-gate.sh
./scripts/run-terraform-drift-detection.sh --root infrastructure/terraform/examples/azure
```

## Notes

- Azure/AWS credential secrets are treated as bootstrap credentials. The workflow creates ephemeral least-privilege identities per stack (`aca`, `functions`, `ecs`, `serverless`, `aks`, `eks`), runs validation with those identities, then destroys them.
- Dedicated bootstrap modules used by the workflow:
  - `infrastructure/terraform/bootstrap/azure-aca`
  - `infrastructure/terraform/bootstrap/azure-functions`
  - `infrastructure/terraform/bootstrap/azure-aks`
  - `infrastructure/terraform/bootstrap/aws-ecs`
  - `infrastructure/terraform/bootstrap/aws-serverless`
  - `infrastructure/terraform/bootstrap/aws-eks`
- Use one database admin secret: `HONUA_DB_PASSWORD` (not separate per cloud).
- Azure script behavior: when neither `--existing-db-connection` nor `--existing-redis-connection` is provided, `scripts/run-azure-terraform-integration.sh` applies `infrastructure/terraform/examples/azure-data` first and feeds outputs into ACA/Functions applies.
- Current known issue (February 28, 2026): generic web tags (`latest`, `latest-aot`) crash on Azure Functions custom container startup (container exit code `139`). Use Functions-targeted tags (`*-functions-aot` preferred, `*-functions` debug fallback).
- Registry strategy: web runtime tags (`latest`, `latest-aot`, versioned base tags) are published to GHCR/Docker Hub, while serverless platform tags (`*-lambda`, `*-lambda-aot`, `*-functions`, `*-functions-aot`) are published by CI directly to cloud registries (ECR/ACR).
- `.terraform` directories are already ignored in `.gitignore`.
- Live scripts auto-destroy by default unless `--no-destroy` / `no_destroy=true` is set.
