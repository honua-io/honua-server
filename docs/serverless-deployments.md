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

## Image Build Notes

- Build JIT images with `docker/Dockerfile` and AOT images with `docker/Dockerfile.aot`.
- Serverless runtimes may require a compatibility layer (Lambda Runtime API or Functions host + custom handler). Ensure the final image is Lambda/Functions-compatible before pushing to your registry.
- Honua listens on port `8080` by default; keep your runtime adapter or host configured to forward to that port.

## Published Images

Honua publishes base images you can extend for serverless runtimes:

- Docker Hub: `honuaio/honua-server`
- GHCR: `ghcr.io/honua-io/honua-server`

Common tags:
- `latest` (trunk)
- `vX.Y.Z`, `vX.Y`, `vX` (release tags)
- `nightly` (JIT)
- `nightly-aot` (AOT)

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

- Build and publish a **Lambda-compatible** JIT image.
- Deploy `examples/aws-serverless` with `honua_image_uri` set to the JIT image, then run the smoke tests above.
- Build and publish a **Lambda-compatible** AOT image.
- Re-deploy `examples/aws-serverless` with the AOT image and repeat smoke tests.
- Build and publish a **Functions-compatible** JIT image.
- Deploy `examples/azure-functions` with `honua_image` set to the JIT image, then run the smoke tests above.
- Build and publish a **Functions-compatible** AOT image.
- Re-deploy `examples/azure-functions` with the AOT image and repeat smoke tests.

## Notes

- These templates provision PostgreSQL and Redis to keep Honua stateless. If you bring existing services, set the connection string variables accordingly and disable provisioning where supported.
- Build and publish Lambda/Functions-compatible images for both JIT and AOT. The Honua Dockerfiles under `docker/` produce standard containers; serverless runtimes may require a compatibility layer.
- Real cloud account validation (AWS + Azure) is still required for final verification.
