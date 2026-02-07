# Azure Container Apps Module

Deploys Honua Server to Azure Container Apps with PostgreSQL Flexible Server, Key Vault-backed secrets, optional Azure Cache for Redis, and Log Analytics.

## Quick start (dev)

```hcl
module "honua" {
  source = "../../modules/azure-aca"

  environment    = "dev"
  location       = "eastus"
  image          = "ghcr.io/honua-io/honua-server:latest-aot"
  admin_password = var.honua_admin_password
  enable_postgis = true  # Required — Honua needs PostGIS for migrations

  additional_env = {
    HONUA_ADMIN_UI = "true"
  }
}
```

> **PostGIS is required.** Set `enable_postgis = true` to enable the PostGIS extension via a local-exec provisioner. This requires `psql` on the machine running `terraform apply` and network access to the database. If you cannot run local-exec, enable PostGIS manually after apply.

## Production example

```hcl
module "honua" {
  source = "../../modules/azure-aca"

  environment = "prod"
  location    = "eastus"
  name_prefix = "honua"

  # Container
  image            = "ghcr.io/honua-io/honua-server:v1.2.3-aot"  # Pin to a release AOT tag
  container_cpu    = 1.0     # 1 vCPU
  container_memory = 2.0     # 2 GiB
  min_replicas     = 2       # Minimum 2 for HA
  max_replicas     = 10

  # Database
  admin_password                = var.honua_admin_password
  db_sku_name                   = "GP_Standard_D2s_v3"   # General Purpose, production-grade
  db_storage_mb                 = 65536                   # 64 GB
  db_version                    = "16"
  db_geo_redundant_backup_enabled = true
  enable_postgis                = true

  # Redis (multi-node caching)
  redis_enabled  = true
  redis_sku_name = "Standard"
  redis_capacity = 2

  # Networking
  enable_ingress        = true
  db_public_network_access = false  # Use private access in production

  # Key Vault
  key_vault_purge_protection_enabled = true
  key_vault_default_action           = "Deny"

  # Monitoring
  log_analytics_enabled = true

  additional_env = {
    HONUA_ADMIN_UI      = "true"
    HONUA_OBSERVABILITY = "true"
    Public__BaseUrl     = "https://gis.example.com"
  }

  tags = {
    Project     = "honua"
    Environment = "prod"
  }
}
```

## Key variables

| Variable | Default | Description |
|----------|---------|-------------|
| `image` | `ghcr.io/.../latest-aot` | Container image. AOT recommended. Pin to `vX.Y.Z-aot` for production. |
| `container_cpu` | 0.5 | CPU cores (0.25, 0.5, 1.0, 2.0, 4.0). |
| `container_memory` | 1.0 | Memory in GiB. |
| `min_replicas` / `max_replicas` | 1 / 5 | Scaling range. Use min 2 for production. |
| `enable_postgis` | **false** | Enable PostGIS extension. **Set to true.** |
| `db_sku_name` | `B_Standard_B1ms` | PostgreSQL SKU. Use `GP_Standard_*` for production. |
| `db_storage_mb` | 32768 | Database storage in MB. |
| `db_geo_redundant_backup_enabled` | true | Geo-redundant backups. |
| `redis_enabled` | true | Provision Azure Cache for Redis. |
| `redis_sku_name` | `Standard` | Redis SKU (Basic, Standard, Premium). |
| `key_vault_default_action` | `Deny` | Key Vault network ACL default. |
| `enable_ingress` | true | Expose Container App via external ingress. |
| `log_analytics_enabled` | true | Enable Log Analytics workspace. |

See `variables.tf` for the complete list.

## Key Vault networking

Key Vault network ACLs default to `Deny`. Adjust `key_vault_ip_rules` to allowlist your CI/CD runner IPs, or supply private endpoints outside the module.

## Private container registry

For private registries (e.g. ACR), provide credentials:

```hcl
registry_server   = "myregistry.azurecr.io"
registry_username = var.acr_username
registry_password = var.acr_password
```

## Outputs

See `outputs.tf` for the Container App FQDN, Key Vault secret IDs, and database connection string.

## After apply

1. Verify PostGIS: `psql $CONNECTION_STRING -c "SELECT PostGIS_Version();"`
2. Health check: `curl -f https://<app-fqdn>/healthz/ready`
3. If using OIDC, configure env vars per [Security Configuration](../../../../docs/devops/SECURITY_CONFIGURATION.md)
