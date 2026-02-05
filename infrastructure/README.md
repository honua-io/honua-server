# Infrastructure Getting Started

This folder contains deployment templates for Honua Server across local, Kubernetes, and cloud runtimes. All options run the same Honua container image and require a PostGIS database.

## Architecture Overview

- Honua Server runs as a containerized API service (JIT or AOT images).
- A PostGIS-enabled PostgreSQL database is required for all deployments.
- Redis is optional and recommended for multi-node deployments and caching.
- Object storage (S3/MinIO) is optional for file import workflows.
- TLS, rate limiting, and edge security are handled by the platform ingress (ALB, API Gateway, Ingress Controller, or similar).
- Serverless runtimes require a compatible adapter (Lambda Runtime API or Azure Functions custom handler).

## Deployment Options

- Local Docker Compose: `docker-compose/README.md`
- Kubernetes (Helm): `helm/README.md` and `helm/honua/README.md`
- Terraform (Cloud): `terraform/README.md`
- AWS ECS/Fargate module: `terraform/modules/aws-ecs/README.md`
- Azure Container Apps module: `terraform/modules/azure-aca/README.md`
- AWS Lambda (serverless) module: `terraform/modules/aws-serverless/README.md`
- Azure Functions (serverless) module: `terraform/modules/azure-functions/README.md`
- Terraform bootstrap service accounts: `terraform/bootstrap/README.md`

## Images

Published base images and tags are documented in `../docs/CONTAINER_IMAGES.md`. Serverless deployments may require images that include runtime adapters for Lambda or Azure Functions.

## Required Configuration

- `ConnectionStrings__DefaultConnection`
- `HONUA_ADMIN_PASSWORD`

See `../docs/SECURITY_CONFIGURATION.md` for production hardening guidance and `../docs/serverless-deployments.md` for serverless-specific constraints.
