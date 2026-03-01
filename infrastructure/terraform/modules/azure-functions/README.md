# Azure Functions (Serverless) Module

Deploys Honua Server to Azure Functions (custom container) with PostgreSQL Flexible Server, optional Azure Cache for Redis, and Application Insights.

## Quick start

```hcl
module "honua" {
  source = "../../modules/azure-functions"

  environment    = "dev"
  location       = "eastus"
  image          = "myregistry.azurecr.io/honua-server:latest-aot"
  admin_password = var.honua_admin_password
  enable_postgis = true  # Required — Honua needs PostGIS + PostGIS Raster

  additional_env = {
    HONUA_SERVE_ADMIN_UI = "true"
    HONUA_ADMIN_UI       = "true"
  }
}
```

## Prerequisites

- **PostGIS + PostGIS Raster**: Set `enable_postgis = true` (requires `psql` on the apply machine with network access to the database).
- **Migrations**: `skip_migrations` defaults to `true` for serverless. Run migrations out-of-band before first use.
- **Custom container**: The image must be compatible with the Azure Functions custom handler model (`FUNCTIONS_WORKER_RUNTIME=custom`).

## Production example

```hcl
module "honua" {
  source = "../../modules/azure-functions"

  environment = "prod"
  location    = "eastus"
  name_prefix = "honua"

  # Function App
  image         = "myregistry.azurecr.io/honua-server:v1.2.3-aot"  # Pin to a release AOT tag
  plan_sku_name = "EP1"    # Premium plan (recommended for predictable cold starts)

  # Database
  admin_password                    = var.honua_admin_password
  db_sku_name                       = "GP_Standard_D2s_v3"
  db_storage_mb                     = 65536
  db_geo_redundant_backup_enabled   = true
  enable_postgis                    = true
  skip_migrations                   = true

  # Redis
  redis_enabled  = true
  redis_sku_name = "Standard"
  redis_capacity = 2

  # Monitoring
  app_insights_enabled = true

  additional_env = {
    HONUA_OBSERVABILITY = "true"
    Public__BaseUrl     = "https://gis.example.com"
  }
}
```

## Key variables

| Variable | Default | Description |
|----------|---------|-------------|
| `image` | `ghcr.io/.../latest-aot` | Container image. Prefer AOT tags (`latest-aot`, `vX.Y.Z-aot`). Functions-specific AOT tags can be used when published. |
| `plan_sku_name` | `EP1` | Premium (`EP1`–`EP3`) or Consumption (`Y1`). Premium recommended. |
| `enable_postgis` | **false** | Enable PostGIS + PostGIS Raster on database. **Set to true.** |
| `skip_migrations` | true | Skip auto-migrations. Run them out-of-band for serverless. |
| `existing_db_connection_string` + `existing_db_fqdn` | `""` | Reuse an existing PostgreSQL server and skip DB creation/bootstrap. |
| `db_sku_name` | `B_Standard_B1ms` | PostgreSQL SKU. Use `GP_Standard_*` for production. |
| `db_geo_redundant_backup_enabled` | true | Geo-redundant backups. |
| `redis_enabled` | true | Provision Azure Cache for Redis. |
| `redis_connection_string` | `""` | Reuse an existing Redis instance instead of provisioning one. |
| `app_insights_enabled` | true | Enable Application Insights. |

See `variables.tf` for the complete list.

## Plan selection

| SKU | Cold start | Scale | Recommended for |
|-----|-----------|-------|-----------------|
| `EP1`–`EP3` | Warm instances, faster | Auto-scale with min instances | Production |
| `Y1` | Cold start on every scale event | Auto-scale, stricter limits | Dev/testing, cost-sensitive |

## Cold starts

Use AOT images (`vX.Y.Z-aot`, `latest-aot`) for runtime performance. Use JIT tags only for debug fallback.

## Private container registry

For ACR or other private registries:

```hcl
registry_server   = "myregistry.azurecr.io"
registry_username = var.acr_username
registry_password = var.acr_password
```

## Outputs

See `outputs.tf` for the Function App URL, database connection string, and Redis endpoint.
