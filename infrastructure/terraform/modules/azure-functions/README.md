# Azure Functions (Serverless) Module

Deploys Honua Server to Azure Functions (custom container) with Azure Database for PostgreSQL Flexible Server and optional Azure Cache for Redis.

## Features
- Linux Function App with custom container
- Consumption or Premium plan
- PostgreSQL Flexible Server
- Optional Azure Cache for Redis

## Usage

```hcl
module "honua" {
  source = "../../modules/azure-functions"

  environment    = "dev"
  location       = "eastus"
  image          = "ghcr.io/honua-io/honua-server:latest"
  admin_password = var.honua_admin_password

  additional_env = {
    HONUA_ADMIN_UI = "true"
  }
}
```

Image tags and registries are documented in `docs/devops/CONTAINER_IMAGES.md`.

## Notes
- Azure Functions custom containers require Functions host settings (`FUNCTIONS_WORKER_RUNTIME=custom`) and a handler configuration inside the image.
- The default `plan_sku_name` is `EP1` (Premium). Set it to `Y1` for Consumption if you accept stricter limits.

## Outputs
See `outputs.tf` for the Function App URL and connection strings.
