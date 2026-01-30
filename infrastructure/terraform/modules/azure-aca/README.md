# Azure Container Apps Module

Deploys Honua Server to Azure Container Apps with Azure Database for PostgreSQL Flexible Server and Key Vault-backed secrets.

## Features
- Container Apps environment + app
- PostgreSQL Flexible Server
- Key Vault for secrets
- User-assigned managed identity

## Usage

```hcl
module "honua" {
  source = "../../modules/azure-aca"

  environment    = "dev"
  location       = "eastus"
  image          = "ghcr.io/honua-io/honua-server:latest"
  admin_password = var.honua_admin_password

  additional_env = {
    HONUA_ADMIN_UI = "true"
  }
}
```

## PostGIS
Set `enable_postgis = true` to attempt to enable the PostGIS extension via `psql`. This requires local network access to the database.

## Key Vault networking
Key Vault network ACLs are enabled by default. Adjust `key_vault_default_action`, `key_vault_ip_rules`, or supply private endpoints outside the module as needed.

## Outputs
See `outputs.tf` for the Container App FQDN and Key Vault secret IDs.
