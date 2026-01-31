# Terraform Modules

This directory contains infrastructure modules and examples for Honua Server.

## Modules
- `modules/aws-ecs` – ECS/Fargate + RDS + ALB
- `modules/azure-aca` – Azure Container Apps + PostgreSQL Flexible Server + Key Vault

## Examples
- `examples/aws`
- `examples/azure`

## Quick validation

```bash
terraform fmt -recursive infrastructure/terraform/modules

terraform -chdir=infrastructure/terraform/examples/aws init
terraform -chdir=infrastructure/terraform/examples/aws validate

terraform -chdir=infrastructure/terraform/examples/azure init
terraform -chdir=infrastructure/terraform/examples/azure validate

checkov -d infrastructure/terraform/modules --download-external-modules true --compact
```
