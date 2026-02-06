# Serverless Deployments (AWS Lambda + Azure Functions)

This guide covers the Terraform templates and runtime constraints for deploying Honua Server on serverless container platforms. The templates are starter configurations that provision the required data services (PostgreSQL + optional Redis) alongside the serverless runtime.

## Terraform Templates

- AWS Lambda: `infrastructure/terraform/modules/aws-serverless`
- Azure Functions: `infrastructure/terraform/modules/azure-functions`

Examples:
- AWS Lambda: `infrastructure/terraform/examples/aws-serverless`
- Azure Functions: `infrastructure/terraform/examples/azure-functions`

## Required Runtime Environment

Honua expects the following environment variables at runtime. The Terraform modules populate the required values for you.

Required:
- `ConnectionStrings__DefaultConnection` (PostgreSQL connection string)
- `HONUA_ADMIN_PASSWORD` (admin API key for automation)

Recommended for serverless:
- `HONUA_SKIP_MIGRATIONS=true` (run migrations out-of-band to avoid concurrent execution)

Optional:
- `ConnectionStrings__redis` (if Redis is enabled)
- `HONUA_SERVE_ADMIN_UI` / `HONUA_ADMIN_UI` (Admin UI behavior)
- `HONUA_DEV_AUTH` (development auth bypass only)

## Runtime Constraints

AWS Lambda:
- HTTP API integration timeout is 30 seconds. Keep `lambda_timeout_seconds` at or below 30.
- Lambda container images must implement the Lambda Runtime API (for example, via the Lambda Runtime Interface Client or an HTTP adapter). The Terraform module assumes you provide a Lambda-compatible image.
- For VPC-attached Lambdas, outbound access (OIDC, external APIs) requires NAT; keep `enable_nat_gateway=true` if you need outbound access.

Azure Functions:
- Custom container support varies by plan. Premium or Dedicated plans are recommended for predictable cold start and scale behavior.
- The container must be compatible with the Functions custom container model (Functions host + custom handler). The Terraform module assumes you provide a Functions-compatible image.

## Image Selection

- Use supported Honua images from Docker Hub or GHCR. See `CONTAINER_IMAGES.md` for registries and tag guidance.
- Serverless runtimes often require a compatibility layer (Lambda Runtime API or Functions host + custom handler). If required, **extend** the supported base image and publish it to your registry (ECR/ACR).
- Honua listens on port `8080` by default; keep your runtime adapter or host configured to forward to that port.

## Published Images

See `CONTAINER_IMAGES.md` for supported registries and tag guidance.

## Terraform Usage

AWS Lambda example:

```bash
terraform -chdir=infrastructure/terraform/examples/aws-serverless init
terraform -chdir=infrastructure/terraform/examples/aws-serverless apply \
  -var "honua_admin_password=change-me" \
  -var "honua_image_uri=<your-ecr-image-uri>"
```

Azure Functions example:

```bash
terraform -chdir=infrastructure/terraform/examples/azure-functions init
terraform -chdir=infrastructure/terraform/examples/azure-functions apply \
  -var "honua_admin_password=change-me" \
  -var "honua_image=<your-container-image>"
```

## Smoke Tests

After apply, use the output `honua_url` and run basic health checks:

```bash
curl -f "${HONUA_URL}/healthz/live"
curl -f "${HONUA_URL}/healthz/ready"
```

Optional admin check (requires API key):

```bash
curl -f -H "X-API-Key: ${HONUA_ADMIN_PASSWORD}" "${HONUA_URL}/api/v1/admin/config"
```

## Manual Validation Checklist (AWS + Azure)

- Publish a **Lambda-compatible** image (often by extending a supported base image).
- Deploy `examples/aws-serverless` with `honua_image_uri` set to that image, then run the smoke tests above.
- Publish a **Functions-compatible** image (often by extending a supported base image).
- Deploy `examples/azure-functions` with `honua_image` set to that image, then run the smoke tests above.

## Notes

- These templates provision PostgreSQL and Redis to keep Honua stateless. If you bring existing services, set the connection string variables accordingly and disable provisioning where supported.
- Serverless runtimes may require a compatibility layer. Extend a supported base image if needed and validate in your target environment.
- Real cloud account validation (AWS + Azure) is still required for final verification.
