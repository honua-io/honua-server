data "azurerm_client_config" "current" {}

locals {
  name = "${var.name_prefix}-${var.environment}"
  tags = merge({
    Project     = "honua-server"
    Environment = var.environment
    ManagedBy   = "terraform"
  }, var.tags)
  db_use_existing = var.existing_db_connection_string != ""
}

check "existing_db_inputs" {
  assert {
    condition     = (var.existing_db_fqdn == "" && var.existing_db_connection_string == "") || (var.existing_db_fqdn != "" && var.existing_db_connection_string != "")
    error_message = "existing_db_fqdn and existing_db_connection_string must be set together."
  }
}

resource "azurerm_resource_group" "this" {
  name     = "${local.name}-rg"
  location = var.location
  tags     = local.tags
}

resource "azurerm_user_assigned_identity" "this" {
  name                = "${local.name}-identity"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  tags                = local.tags
}

#checkov:skip=CKV_AZURE_189: Private endpoints are configured outside this module.
#checkov:skip=CKV2_AZURE_32: Private endpoints are configured outside this module.
resource "azurerm_key_vault" "this" {
  #checkov:skip=CKV_AZURE_189: Private endpoints are configured outside this module.
  #checkov:skip=CKV2_AZURE_32: Private endpoints are configured outside this module.
  name                          = "${local.name}-kv"
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

resource "azurerm_key_vault_access_policy" "identity" {
  key_vault_id = azurerm_key_vault.this.id
  tenant_id    = data.azurerm_client_config.current.tenant_id
  object_id    = azurerm_user_assigned_identity.this.principal_id

  secret_permissions = [
    "Get",
    "List"
  ]
}

resource "random_password" "db" {
  count            = var.db_admin_password == null && !local.db_use_existing ? 1 : 0
  length           = 32
  special          = true
  override_special = "#%*()-_=+[]{}:?."
}

resource "time_static" "secret_baseline" {}

locals {
  db_password            = var.db_admin_password != null ? var.db_admin_password : (local.db_use_existing ? "" : random_password.db[0].result)
  db_server_fqdn         = local.db_use_existing ? var.existing_db_fqdn : azurerm_postgresql_flexible_server.this[0].fqdn
  db_connection_string   = local.db_use_existing ? var.existing_db_connection_string : "Host=${azurerm_postgresql_flexible_server.this[0].fqdn};Port=5432;Database=${var.db_name};Username=${var.db_admin_username};Password=${local.db_password};SSL Mode=Require;Trust Server Certificate=false"
  redis_enabled          = var.redis_enabled || var.redis_connection_string != ""
  redis_create           = var.redis_enabled && var.redis_connection_string == ""
  redis_connection       = var.redis_connection_string != "" ? var.redis_connection_string : (local.redis_create ? azurerm_redis_cache.this[0].primary_connection_string : "")
  secret_expiration_date = timeadd(time_static.secret_baseline.rfc3339, format("%dh", var.secret_expiration_days * 24))
}

#checkov:skip=CKV2_AZURE_57: Private endpoints are configured outside this module.
resource "azurerm_postgresql_flexible_server" "this" {
  #checkov:skip=CKV2_AZURE_57: Private endpoints are configured outside this module.
  count                  = local.db_use_existing ? 0 : 1
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
  count     = local.db_use_existing ? 0 : 1
  name      = "require_secure_transport"
  server_id = azurerm_postgresql_flexible_server.this[0].id
  value     = "on"
}

resource "azurerm_postgresql_flexible_server_firewall_rule" "validation" {
  count = !local.db_use_existing && var.db_public_network_access && var.db_firewall_start_ip != "" && var.db_firewall_end_ip != "" ? 1 : 0

  name             = "validation-access"
  server_id        = azurerm_postgresql_flexible_server.this[0].id
  start_ip_address = var.db_firewall_start_ip
  end_ip_address   = var.db_firewall_end_ip
}

resource "azurerm_postgresql_flexible_server_database" "this" {
  count     = local.db_use_existing ? 0 : 1
  name      = var.db_name
  server_id = azurerm_postgresql_flexible_server.this[0].id
  charset   = "UTF8"
  collation = "en_US.utf8"
}

resource "azurerm_postgresql_flexible_server_configuration" "postgis" {
  count     = !local.db_use_existing && var.enable_postgis ? 1 : 0
  name      = "azure.extensions"
  server_id = azurerm_postgresql_flexible_server.this[0].id
  value     = "POSTGIS,POSTGIS_RASTER"
}

resource "azurerm_redis_cache" "this" {
  #checkov:skip=CKV_AZURE_89: Public access can be enabled for MVP deployments; private endpoints configured externally.
  count                         = local.redis_create ? 1 : 0
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

resource "azurerm_key_vault_secret" "db_connection" {
  name            = "honua-db-connection"
  value           = local.db_connection_string
  content_type    = "connection-string"
  expiration_date = local.secret_expiration_date
  key_vault_id    = azurerm_key_vault.this.id
  depends_on      = [azurerm_key_vault_access_policy.identity, azurerm_key_vault_access_policy.current]
}

resource "azurerm_key_vault_secret" "admin_password" {
  name            = "honua-admin-password"
  value           = var.admin_password
  content_type    = "password"
  expiration_date = local.secret_expiration_date
  key_vault_id    = azurerm_key_vault.this.id
  depends_on      = [azurerm_key_vault_access_policy.identity, azurerm_key_vault_access_policy.current]
}

resource "azurerm_key_vault_secret" "redis_connection" {
  count           = local.redis_enabled ? 1 : 0
  name            = "honua-redis-connection"
  value           = local.redis_connection
  content_type    = "connection-string"
  expiration_date = local.secret_expiration_date
  key_vault_id    = azurerm_key_vault.this.id
  depends_on      = [azurerm_key_vault_access_policy.identity, azurerm_key_vault_access_policy.current]
}

resource "azurerm_log_analytics_workspace" "this" {
  count               = var.log_analytics_enabled ? 1 : 0
  name                = "${local.name}-logs"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  sku                 = "PerGB2018"
  retention_in_days   = 30
  tags                = local.tags
}

resource "azurerm_container_app_environment" "this" {
  name                = "${local.name}-env"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name

  log_analytics_workspace_id = var.log_analytics_enabled ? azurerm_log_analytics_workspace.this[0].id : null

  tags = local.tags
}

resource "azurerm_container_app" "this" {
  name                         = "${local.name}-app"
  resource_group_name          = azurerm_resource_group.this.name
  container_app_environment_id = azurerm_container_app_environment.this.id
  revision_mode                = "Single"

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.this.id]
  }

  dynamic "registry" {
    for_each = toset(var.registry_server != "" ? ["registry"] : [])
    content {
      server               = var.registry_server
      username             = var.registry_username
      password_secret_name = "registry-password"
    }
  }

  secret {
    name                = "db-connection"
    key_vault_secret_id = azurerm_key_vault_secret.db_connection.id
    identity            = azurerm_user_assigned_identity.this.id
  }

  secret {
    name                = "admin-password"
    key_vault_secret_id = azurerm_key_vault_secret.admin_password.id
    identity            = azurerm_user_assigned_identity.this.id
  }

  dynamic "secret" {
    for_each = toset(local.redis_enabled ? ["redis"] : [])
    content {
      name                = "redis-connection"
      key_vault_secret_id = azurerm_key_vault_secret.redis_connection[0].id
      identity            = azurerm_user_assigned_identity.this.id
    }
  }

  dynamic "secret" {
    for_each = toset(var.registry_server != "" ? ["registry"] : [])
    content {
      name  = "registry-password"
      value = var.registry_password
    }
  }

  template {
    min_replicas = var.min_replicas
    max_replicas = var.max_replicas

    http_scale_rule {
      name                = "http-scaling"
      concurrent_requests = var.scaling_concurrent_requests
    }

    container {
      name   = "honua"
      image  = var.image
      cpu    = var.container_cpu
      memory = var.container_memory

      env {
        name        = "ConnectionStrings__DefaultConnection"
        secret_name = "db-connection"
      }

      env {
        name        = "HONUA_ADMIN_PASSWORD"
        secret_name = "admin-password"
      }

      env {
        name        = "Security__ConnectionEncryption__MasterKey"
        secret_name = "admin-password"
      }

      dynamic "env" {
        for_each = toset(local.redis_enabled ? ["redis"] : [])
        content {
          name        = "ConnectionStrings__redis"
          secret_name = "redis-connection"
        }
      }

      dynamic "env" {
        for_each = var.additional_env
        content {
          name  = env.key
          value = env.value
        }
      }

      liveness_probe {
        transport = "HTTP"
        path      = "/healthz/live"
        port      = var.container_port

        initial_delay           = 10
        interval_seconds        = 30
        timeout                 = 5
        failure_count_threshold = 3
      }

      readiness_probe {
        transport = "HTTP"
        path      = "/healthz/ready"
        port      = var.container_port

        interval_seconds        = 10
        timeout                 = 5
        failure_count_threshold = 3
      }

      startup_probe {
        transport = "HTTP"
        path      = "/healthz/ready"
        port      = var.container_port

        interval_seconds        = 10
        timeout                 = 5
        failure_count_threshold = 10
      }
    }
  }

  ingress {
    external_enabled = var.enable_ingress
    target_port      = var.container_port
    transport        = "auto"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  tags = local.tags

  depends_on = [
    azurerm_key_vault_access_policy.identity
  ]
}

resource "null_resource" "enable_postgis" {
  count = !local.db_use_existing && var.enable_postgis ? 1 : 0

  triggers = {
    db_endpoint = local.db_server_fqdn
  }

  provisioner "local-exec" {
    command = <<-EOT
      set -e
      echo "Waiting for PostgreSQL readiness on ${local.db_server_fqdn}"
      for attempt in $(seq 1 30); do
        if PGCONNECT_TIMEOUT=5 psql \
          --host=${local.db_server_fqdn} \
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

      echo "Enabling PostGIS + PostGIS Raster on ${local.db_server_fqdn}"
      PGCONNECT_TIMEOUT=5 psql \
        --host=${local.db_server_fqdn} \
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
