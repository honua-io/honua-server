# Terraform Modules

This directory contains infrastructure modules and examples for Honua Server.

## Modules
- `modules/aws-ecs` – ECS/Fargate + RDS + ALB
- `modules/azure-aca` – Azure Container Apps + PostgreSQL Flexible Server + Key Vault
- `modules/aws-serverless` – Lambda + API Gateway + RDS
- `modules/azure-functions` – Azure Functions + PostgreSQL Flexible Server
- `modules/observability-stack` – optional Prometheus + Grafana add-on for Kubernetes

## Examples
- `examples/aws`
- `examples/azure`
- `examples/aws-serverless`
- `examples/azure-functions`
- `examples/observability`

## Bootstrap service accounts (least privilege)
- `bootstrap/aws-ecs` – IAM user + policy for ECS/Fargate deployments
- `bootstrap/aws-serverless` – IAM user + policy for Lambda/API Gateway deployments
- `bootstrap/azure-aca` – Azure AD service principal + custom role for Container Apps
- `bootstrap/azure-functions` – Azure AD service principal + custom role for Functions

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

terraform -chdir=infrastructure/terraform/examples/observability init
terraform -chdir=infrastructure/terraform/examples/observability validate

checkov -d infrastructure/terraform/modules --download-external-modules true --compact
```
