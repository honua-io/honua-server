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
./scripts/terraform-policy-gate.sh
./scripts/run-terraform-drift-detection.sh --root infrastructure/terraform/examples/azure
```

## Manual GitHub Actions validation

Use `.github/workflows/terraform-manual-validation.yml` to run Terraform validation on demand (not nightly). The workflow runs static checks, policy gates, optional live applies (Azure/AWS/Kubernetes/AKS/EKS), and optional drift detection.

See the full runbook in `docs/devops/terraform-validation.md` for required secrets, repository variables, and dispatch examples.

## Live Azure integration test (apply + destroy)

Use the integration runner to validate real Azure provisioning for both Container Apps and Functions (Redis enabled, PostGIS + PostGIS Raster checks, health checks, admin CRUD/query smoke, quick ACA scale check, and auto-destroy by default):

```bash
export ARM_CLIENT_ID="<service-principal-client-id>"
export ARM_CLIENT_SECRET="<service-principal-client-secret>"
export ARM_TENANT_ID="<tenant-id>"
export ARM_SUBSCRIPTION_ID="<subscription-id>"
export HONUA_ADMIN_PASSWORD="<admin-password>"
export HONUA_DB_PASSWORD="<postgres-admin-password>"

./scripts/run-azure-terraform-integration.sh --stack both
```

## Live AWS integration test (apply + destroy)

Use the integration runner to validate real AWS provisioning for both ECS and serverless (Redis enabled, PostGIS + PostGIS Raster checks, health/load checks, admin CRUD/query smoke, quick ECS scale check, and auto-destroy by default):

```bash
export AWS_ACCESS_KEY_ID="<access-key-id>"
export AWS_SECRET_ACCESS_KEY="<secret-access-key>"
export AWS_SESSION_TOKEN="<session-token-if-applicable>"
export HONUA_ADMIN_PASSWORD="<admin-password>"
export HONUA_DB_PASSWORD="<postgres-admin-password>"
export HONUA_AWS_SERVERLESS_IMAGE="<ecr-image-uri>"

./scripts/run-aws-terraform-integration.sh --stack both
```

## Live AKS integration test (Terraform cluster + Kubernetes checks)

Use the AKS integration runner to provision AKS via Terraform, run the Kubernetes integration checks against that cluster, and auto-destroy by default:

```bash
export ARM_CLIENT_ID="<service-principal-client-id>"
export ARM_CLIENT_SECRET="<service-principal-client-secret>"
export ARM_TENANT_ID="<tenant-id>"
export ARM_SUBSCRIPTION_ID="<subscription-id>"
export HONUA_ADMIN_PASSWORD="<admin-password>"

./scripts/run-aks-terraform-integration.sh
```

## Live EKS integration test (Terraform cluster + Kubernetes checks)

Use the EKS integration runner to provision EKS via Terraform, run the Kubernetes integration checks against that cluster, and auto-destroy by default:

```bash
export AWS_ACCESS_KEY_ID="<access-key-id>"
export AWS_SECRET_ACCESS_KEY="<secret-access-key>"
export AWS_SESSION_TOKEN="<session-token-if-applicable>"
export HONUA_ADMIN_PASSWORD="<admin-password>"

./scripts/run-eks-terraform-integration.sh
```

## Live Kubernetes integration test (k3d + Helm + Terraform apply + destroy)

Use the Kubernetes integration runner to validate Helm deployment plus Terraform observability module provisioning (PostGIS + PostGIS Raster checks, protocol/admin smoke checks, admin CRUD/query smoke, quick scale check, and auto-destroy by default):

```bash
./scripts/run-k8s-terraform-integration.sh
```

No cloud credentials are required for this path; it runs against a local k3d cluster.
