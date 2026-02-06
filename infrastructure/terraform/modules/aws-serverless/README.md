# AWS Lambda (Serverless) Module

Deploys Honua Server to AWS Lambda (container image) behind an API Gateway HTTP API, plus RDS PostgreSQL and optional ElastiCache Redis.

## Features
- Lambda function (container image)
- API Gateway HTTP API
- RDS PostgreSQL instance
- Optional ElastiCache Redis
- VPC with public/private subnets

## Usage

```hcl
module "honua" {
  source = "../../modules/aws-serverless"

  environment    = "dev"
  image          = var.honua_image_uri
  admin_password = var.honua_admin_password

  additional_env = {
    HONUA_ADMIN_UI = "true"
  }
}
```

Image tags and registries are documented in `docs/devops/CONTAINER_IMAGES.md`.

## Notes
- Lambda container images must be stored in ECR.
- API Gateway HTTP API has a 30s max integration timeout; keep `lambda_timeout_seconds` in sync.
- For production, consider setting `skip_migrations = true` and running migrations separately.

## Outputs
See `outputs.tf` for API endpoint and connection strings.
