# Terraform Modules

This directory contains infrastructure modules and examples for Honua Server.

## Modules
- `modules/aws-ecs` – ECS/Fargate + RDS + ALB
- `modules/azure-aca` – Azure Container Apps + PostgreSQL Flexible Server + Key Vault
- `modules/aws-serverless` – Lambda + API Gateway + RDS
- `modules/azure-functions` – Azure Functions + PostgreSQL Flexible Server
- `modules/aws-eks` – EKS + VPC for managed Kubernetes validation
- `modules/azure-aks` – AKS for managed Kubernetes validation
- `modules/observability-stack` – optional Prometheus + Grafana add-on for Kubernetes

## Examples
- `examples/aws`
- `examples/azure`
- `examples/aws-serverless`
- `examples/azure-functions`
- `examples/aws-eks`
- `examples/azure-aks`
- `examples/observability`

## Bootstrap service accounts (least privilege)
- `bootstrap/aws-ecs` – IAM user + policy for ECS/Fargate deployments
- `bootstrap/aws-serverless` – IAM user + policy for Lambda/API Gateway deployments
- `bootstrap/aws-eks` – IAM user + policy for EKS deployments
- `bootstrap/azure-aca` – Azure AD service principal + custom role for Container Apps
- `bootstrap/azure-functions` – Azure AD service principal + custom role for Functions
- `bootstrap/azure-aks` – Azure AD service principal + custom role for AKS deployments

These templates include permissions for Postgres (RDS / Flexible Server) and Redis provisioning.

## Quick validation

```bash
terraform fmt -recursive infrastructure/terraform/modules

terraform -chdir=infrastructure/terraform/examples/aws init
terraform -chdir=infrastructure/terraform/examples/aws validate

terraform -chdir=infrastructure/terraform/examples/azure init
terraform -chdir=infrastructure/terraform/examples/azure validate

terraform -chdir=infrastructure/terraform/examples/aws-serverless init
terraform -chdir=infrastructure/terraform/examples/aws-serverless validate

terraform -chdir=infrastructure/terraform/examples/azure-functions init
terraform -chdir=infrastructure/terraform/examples/azure-functions validate

terraform -chdir=infrastructure/terraform/examples/aws-eks init
terraform -chdir=infrastructure/terraform/examples/aws-eks validate

terraform -chdir=infrastructure/terraform/examples/azure-aks init
terraform -chdir=infrastructure/terraform/examples/azure-aks validate

terraform -chdir=infrastructure/terraform/examples/observability init
terraform -chdir=infrastructure/terraform/examples/observability validate

checkov -d infrastructure/terraform/modules --download-external-modules true --compact
```

## Policy and drift scripts

```bash
./infrastructure/terraform/scripts/shared/terraform-policy-gate.sh
./infrastructure/terraform/scripts/shared/run-terraform-drift-detection.sh --root infrastructure/terraform/examples/azure
```

## Manual GitHub Actions validation

Use `.github/workflows/terraform-manual-validation.yml` to run Terraform validation on demand (not nightly). The workflow runs static checks, policy gates, optional live applies (Azure/AWS/Kubernetes/AKS/EKS), and optional drift detection.

See the full runbook in `docs/devops/terraform-validation.md` for required secrets, repository variables, and dispatch examples.

## AWS and Azure approach

Both clouds follow the same high-level pattern:

1. Split slow data resources from compute resources.
2. Run live integration checks against real cloud deployments.
3. Reuse data resources across runs to reduce validation time and cost.
4. Keep bootstrap auth separate from per-stack least-privilege identities.

### Azure approach

- Data stack: `examples/azure-data` (PostgreSQL Flexible Server + Redis).
- Compute stacks: `examples/azure` (ACA) and `examples/azure-functions`.
- Runner: `infrastructure/terraform/scripts/azure/run-azure-terraform-integration.sh`.
- Behavior:
  - If existing DB/Redis values are not provided, the script auto-provisions the Azure data stack, then runs ACA/Functions validation.
  - Data outputs are cached to `/tmp/honua-azure-data-reuse.env` (configurable via `HONUA_AZURE_DATA_CACHE_FILE`) and auto-reused in later runs.
  - Reuse mode is supported by providing:
    - `HONUA_AZURE_EXISTING_DB_FQDN`
    - `HONUA_AZURE_EXISTING_DB_CONNECTION_STRING`
    - `HONUA_AZURE_EXISTING_REDIS_CONNECTION_STRING`
  - Compute resources auto-destroy by default; data is retained by default for reuse and only destroyed when `--destroy-data` (or `HONUA_AZURE_DESTROY_DATA=true`) is set.

### AWS approach

- Data stack: `examples/aws-data` (RDS + Redis + VPC/subnets).
- Compute stacks: `examples/aws` (ECS) and `examples/aws-serverless`.
- Runner: `infrastructure/terraform/scripts/aws/run-aws-terraform-integration.sh`.
- Behavior:
  - If existing DB/Redis/VPC values are not provided, the script auto-provisions the AWS data stack first.
  - Data outputs are cached to `/tmp/honua-aws-data-reuse.env` (configurable via `HONUA_AWS_DATA_CACHE_FILE`) and auto-reused in later runs.
  - Compute resources auto-destroy by default; data is retained by default for reuse and only destroyed when `--destroy-data` (or `HONUA_AWS_DESTROY_DATA=true`) is set.

### Validation checks in both clouds

- Terraform init/plan/apply/destroy orchestration.
- PostGIS and `postgis_raster` extension verification.
- Protocol smoke checks (REST/OGC/OData).
- Admin API smoke (`create connection -> publish layer -> query`).
- Quick scale check (ACA or ECS path).
- Optional idempotency, quota preflight, and DB backup/restore drill.

### Is Azure already designed well?

Yes, the Azure structure is sound:

- It already has the right module split (`azure-data`, `azure-aca`, `azure-functions`).
- It already supports data reuse by accepting existing DB/Redis connection inputs.
- It now includes automatic local data reuse cache behavior via `HONUA_AZURE_DATA_CACHE_FILE`.
- It already runs the same smoke, resiliency, and scale validations as AWS.

## Live Azure integration test (apply + destroy)

Use the integration runner to validate real Azure provisioning for both Container Apps and Functions (Redis enabled, PostGIS + PostGIS Raster checks, health checks, admin CRUD/query smoke, quick ACA scale check, compute auto-destroy by default, and reusable data-stack retention by default):

```bash
export ARM_CLIENT_ID="<service-principal-client-id>"
export ARM_CLIENT_SECRET="<service-principal-client-secret>"
export ARM_TENANT_ID="<tenant-id>"
export ARM_SUBSCRIPTION_ID="<subscription-id>"
export HONUA_ADMIN_PASSWORD="<admin-password-at-least-32-chars>"
export HONUA_DB_PASSWORD="<postgres-admin-password>"

./infrastructure/terraform/scripts/azure/run-azure-terraform-integration.sh --stack both
```

## Live AWS integration test (apply + destroy)

Use the integration runner to validate real AWS provisioning for both ECS and serverless (Redis enabled, PostGIS + PostGIS Raster checks, health/load checks, admin CRUD/query smoke, quick ECS scale check, and auto-destroy by default):

```bash
export AWS_ACCESS_KEY_ID="<access-key-id>"
export AWS_SECRET_ACCESS_KEY="<secret-access-key>"
export AWS_SESSION_TOKEN="<session-token-if-applicable>"
export HONUA_ADMIN_PASSWORD="<admin-password-at-least-32-chars>"
export HONUA_DB_PASSWORD="<postgres-admin-password>"
export HONUA_AWS_SERVERLESS_IMAGE="<ecr-image-uri>"

./infrastructure/terraform/scripts/aws/run-aws-terraform-integration.sh --stack both
```

## Live AKS integration test (Terraform cluster + Kubernetes checks)

Use the AKS integration runner to provision AKS via Terraform, run the Kubernetes integration checks against that cluster, and auto-destroy by default:

```bash
export ARM_CLIENT_ID="<service-principal-client-id>"
export ARM_CLIENT_SECRET="<service-principal-client-secret>"
export ARM_TENANT_ID="<tenant-id>"
export ARM_SUBSCRIPTION_ID="<subscription-id>"
export HONUA_ADMIN_PASSWORD="<admin-password>"

./infrastructure/terraform/scripts/azure/run-aks-terraform-integration.sh
```

## Live EKS integration test (Terraform cluster + Kubernetes checks)

Use the EKS integration runner to provision EKS via Terraform, run the Kubernetes integration checks against that cluster, and auto-destroy by default:

```bash
export AWS_ACCESS_KEY_ID="<access-key-id>"
export AWS_SECRET_ACCESS_KEY="<secret-access-key>"
export AWS_SESSION_TOKEN="<session-token-if-applicable>"
export HONUA_ADMIN_PASSWORD="<admin-password>"

./infrastructure/terraform/scripts/aws/run-eks-terraform-integration.sh
```

## Live Kubernetes integration test (k3d + Helm + Terraform apply + destroy)

Use the Kubernetes integration runner to validate Helm deployment plus Terraform observability module provisioning (PostGIS + PostGIS Raster checks, protocol/admin smoke checks, admin CRUD/query smoke, quick scale check, and auto-destroy by default):

```bash
./infrastructure/terraform/scripts/k8s/run-k8s-terraform-integration.sh
```

No cloud credentials are required for this path; it runs against a local k3d cluster.
