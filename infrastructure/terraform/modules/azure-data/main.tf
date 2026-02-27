data "azurerm_client_config" "current" {}

locals {
  name = "${var.name_prefix}-${var.environment}"
  tags = merge({
    Project     = "honua-server"
    Environment = var.environment
    ManagedBy   = "terraform"
  }, var.tags)
  db_password            = var.db_admin_password != null ? var.db_admin_password : random_password.db[0].result
  db_connection_string   = "Host=${azurerm_postgresql_flexible_server.this.fqdn};Port=5432;Database=${var.db_name};Username=${var.db_admin_username};Password=${local.db_password};SSL Mode=Require;Trust Server Certificate=false"
  redis_connection       = var.redis_enabled ? azurerm_redis_cache.this[0].primary_connection_string : ""
  secret_expiration_date = timeadd(time_static.secret_baseline.rfc3339, format("%dh", var.secret_expiration_days * 24))
}

resource "azurerm_resource_group" "this" {
  name     = "${local.name}-data-rg"
  location = var.location
  tags     = local.tags
}

# --- Key Vault ---

#checkov:skip=CKV_AZURE_189: Private endpoints are configured outside this module.
#checkov:skip=CKV2_AZURE_32: Private endpoints are configured outside this module.
resource "azurerm_key_vault" "this" {
  #checkov:skip=CKV_AZURE_189: Private endpoints are configured outside this module.
  #checkov:skip=CKV2_AZURE_32: Private endpoints are configured outside this module.
  name                          = "${local.name}-data-kv"
  location                      = azurerm_resource_group.this.location
  resource_group_name           = azurerm_resource_group.this.name
  tenant_id                     = data.azurerm_client_config.current.tenant_id
  sku_name                      = "standard"
  purge_protection_enabled      = var.key_vault_purge_protection_enabled
  soft_delete_retention_days    = var.key_vault_soft_delete_retention_days
  public_network_access_enabled = var.key_vault_public_network_access_enabled

  network_acls {
    default_action = var.key_vault_default_action
    bypass         = var.key_vault_bypass
    ip_rules       = var.key_vault_ip_rules
  }

  tags = local.tags
}

resource "azurerm_key_vault_access_policy" "current" {
  key_vault_id = azurerm_key_vault.this.id
  tenant_id    = data.azurerm_client_config.current.tenant_id
  object_id    = data.azurerm_client_config.current.object_id

  secret_permissions = [
    "Get",
    "List",
    "Set",
    "Delete",
    "Purge",
    "Recover"
  ]
}

resource "time_static" "secret_baseline" {}

resource "azurerm_key_vault_secret" "db_connection" {
  name            = "honua-db-connection"
  value           = local.db_connection_string
  content_type    = "connection-string"
  expiration_date = local.secret_expiration_date
  key_vault_id    = azurerm_key_vault.this.id
  depends_on      = [azurerm_key_vault_access_policy.current]
}

resource "azurerm_key_vault_secret" "admin_password" {
  name            = "honua-admin-password"
  value           = var.admin_password
  content_type    = "password"
  expiration_date = local.secret_expiration_date
  key_vault_id    = azurerm_key_vault.this.id
  depends_on      = [azurerm_key_vault_access_policy.current]
}

resource "azurerm_key_vault_secret" "redis_connection" {
  count           = var.redis_enabled ? 1 : 0
  name            = "honua-redis-connection"
  value           = local.redis_connection
  content_type    = "connection-string"
  expiration_date = local.secret_expiration_date
  key_vault_id    = azurerm_key_vault.this.id
  depends_on      = [azurerm_key_vault_access_policy.current]
}

# --- PostgreSQL ---

resource "random_password" "db" {
  count            = var.db_admin_password == null ? 1 : 0
  length           = 32
  special          = true
  override_special = "#%*()-_=+[]{}:?."
}

#checkov:skip=CKV2_AZURE_57: Private endpoints are configured outside this module.
resource "azurerm_postgresql_flexible_server" "this" {
  #checkov:skip=CKV2_AZURE_57: Private endpoints are configured outside this module.
  name                   = "${local.name}-pg"
  resource_group_name    = azurerm_resource_group.this.name
  location               = azurerm_resource_group.this.location
  version                = var.db_version
  administrator_login    = var.db_admin_username
  administrator_password = local.db_password
  storage_mb             = var.db_storage_mb
  sku_name               = var.db_sku_name

  backup_retention_days = var.db_backup_retention_days

  public_network_access_enabled = var.db_public_network_access
  geo_redundant_backup_enabled  = var.db_geo_redundant_backup_enabled

  tags = local.tags
}

resource "azurerm_postgresql_flexible_server_configuration" "require_secure_transport" {
  name      = "require_secure_transport"
  server_id = azurerm_postgresql_flexible_server.this.id
  value     = "on"
}

resource "azurerm_postgresql_flexible_server_firewall_rule" "validation" {
  count = var.db_public_network_access && var.db_firewall_start_ip != "" && var.db_firewall_end_ip != "" ? 1 : 0

  name             = "validation-access"
  server_id        = azurerm_postgresql_flexible_server.this.id
  start_ip_address = var.db_firewall_start_ip
  end_ip_address   = var.db_firewall_end_ip
}

resource "azurerm_postgresql_flexible_server_database" "this" {
  name      = var.db_name
  server_id = azurerm_postgresql_flexible_server.this.id
  charset   = "UTF8"
  collation = "en_US.utf8"
}

resource "azurerm_postgresql_flexible_server_configuration" "postgis" {
  count     = var.enable_postgis ? 1 : 0
  name      = "azure.extensions"
  server_id = azurerm_postgresql_flexible_server.this.id
  value     = "POSTGIS,POSTGIS_RASTER"
}

resource "null_resource" "enable_postgis" {
  count = var.enable_postgis ? 1 : 0

  triggers = {
    db_endpoint = azurerm_postgresql_flexible_server.this.fqdn
  }

  provisioner "local-exec" {
    command = <<-EOT
      set -e
      echo "Waiting for PostgreSQL readiness on ${azurerm_postgresql_flexible_server.this.fqdn}"
      for attempt in $(seq 1 30); do
        if PGCONNECT_TIMEOUT=5 psql \
          --host=${azurerm_postgresql_flexible_server.this.fqdn} \
          --username=${var.db_admin_username} \
          --dbname=${var.db_name} \
          --command="SELECT 1;" >/dev/null 2>&1; then
          break
        fi
        if [ "$attempt" -eq 30 ]; then
          echo "PostgreSQL readiness check failed after 30 attempts" >&2
          exit 1
        fi
        sleep 10
      done

      echo "Enabling PostGIS + PostGIS Raster on ${azurerm_postgresql_flexible_server.this.fqdn}"
      PGCONNECT_TIMEOUT=5 psql \
        --host=${azurerm_postgresql_flexible_server.this.fqdn} \
        --username=${var.db_admin_username} \
        --dbname=${var.db_name} \
        --set=ON_ERROR_STOP=1 \
        --command="CREATE EXTENSION IF NOT EXISTS postgis; CREATE EXTENSION IF NOT EXISTS postgis_raster;"
    EOT

    environment = {
      PGPASSWORD = local.db_password
    }
  }

  depends_on = [
    azurerm_postgresql_flexible_server_database.this,
    azurerm_postgresql_flexible_server_firewall_rule.validation
  ]
}

# --- Redis ---

#checkov:skip=CKV_AZURE_89: Public access can be enabled for MVP deployments; private endpoints configured externally.
resource "azurerm_redis_cache" "this" {
  #checkov:skip=CKV_AZURE_89: Public access can be enabled for MVP deployments; private endpoints configured externally.
  count                         = var.redis_enabled ? 1 : 0
  name                          = "${local.name}-redis"
  location                      = azurerm_resource_group.this.location
  resource_group_name           = azurerm_resource_group.this.name
  capacity                      = var.redis_capacity
  family                        = var.redis_family
  sku_name                      = var.redis_sku_name
  non_ssl_port_enabled          = var.redis_enable_non_ssl_port
  public_network_access_enabled = var.redis_public_network_access_enabled
  subnet_id                     = var.redis_subnet_id != "" ? var.redis_subnet_id : null
  minimum_tls_version           = "1.2"
  tags                          = local.tags
}
