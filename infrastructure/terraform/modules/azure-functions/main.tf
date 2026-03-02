data "azurerm_client_config" "current" {}

resource "random_string" "storage_suffix" {
  length  = 6
  upper   = false
  lower   = true
  numeric = true
  special = false
}

locals {
  name = "${var.name_prefix}-${var.environment}"
  tags = merge({
    Project     = "honua-server"
    Environment = var.environment
    ManagedBy   = "terraform"
  }, var.tags)
  storage_account_name = substr(replace(lower("${var.name_prefix}${var.environment}${random_string.storage_suffix.result}"), "-", ""), 0, 24)
  db_use_existing      = var.existing_db_connection_string != ""
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

resource "azurerm_user_assigned_identity" "function" {
  name                = "${local.name}-func-identity"
  resource_group_name = azurerm_resource_group.this.name
  location            = azurerm_resource_group.this.location
  tags                = local.tags
}

resource "azurerm_storage_account" "this" {
  name                     = local.storage_account_name
  resource_group_name      = azurerm_resource_group.this.name
  location                 = azurerm_resource_group.this.location
  account_tier             = var.storage_account_tier
  account_replication_type = var.storage_account_replication_type
  min_tls_version          = "TLS1_2"
  tags                     = local.tags

  network_rules {
    default_action = "Deny"
    bypass         = ["AzureServices"]
  }

  blob_properties {
    delete_retention_policy {
      days = 7
    }
    container_delete_retention_policy {
      days = 7
    }
  }
}

resource "azurerm_service_plan" "this" {
  name                = "${local.name}-plan"
  resource_group_name = azurerm_resource_group.this.name
  location            = azurerm_resource_group.this.location
  os_type             = "Linux"
  sku_name            = var.plan_sku_name
  tags                = local.tags
}

resource "random_password" "db" {
  count            = var.db_admin_password == null && !local.db_use_existing ? 1 : 0
  length           = 32
  special          = true
  override_special = "#%*()-_=+[]{}:?"
}

locals {
  db_password          = var.db_admin_password != null ? var.db_admin_password : (local.db_use_existing ? "" : random_password.db[0].result)
  db_server_fqdn       = local.db_use_existing ? var.existing_db_fqdn : azurerm_postgresql_flexible_server.this[0].fqdn
  db_connection_string = local.db_use_existing ? var.existing_db_connection_string : "Host=${azurerm_postgresql_flexible_server.this[0].fqdn};Port=5432;Database=${var.db_name};Username=${var.db_admin_username};Password=${local.db_password};SSL Mode=Require;Trust Server Certificate=false"
  redis_enabled        = var.redis_enabled || var.redis_connection_string != ""
  redis_create         = var.redis_enabled && var.redis_connection_string == ""
  redis_connection     = var.redis_connection_string != "" ? var.redis_connection_string : (local.redis_create ? azurerm_redis_cache.this[0].primary_connection_string : "")
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

  public_network_access_enabled = var.db_public_network_access
  geo_redundant_backup_enabled  = var.db_geo_redundant_backup_enabled
  backup_retention_days         = var.db_backup_retention_days

  tags = local.tags
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

resource "azurerm_postgresql_flexible_server_configuration" "require_secure_transport" {
  count     = local.db_use_existing ? 0 : 1
  name      = "require_secure_transport"
  server_id = azurerm_postgresql_flexible_server.this[0].id
  value     = "on"
}

resource "azurerm_postgresql_flexible_server_configuration" "postgis" {
  count     = !local.db_use_existing && var.enable_postgis ? 1 : 0
  name      = "azure.extensions"
  server_id = azurerm_postgresql_flexible_server.this[0].id
  value     = "POSTGIS,POSTGIS_RASTER"
}

resource "azurerm_redis_cache" "this" {
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

resource "azurerm_log_analytics_workspace" "this" {
  count               = var.app_insights_enabled ? 1 : 0
  name                = "${local.name}-logs"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  sku                 = "PerGB2018"
  retention_in_days   = 30
  tags                = local.tags
}

resource "azurerm_application_insights" "this" {
  count               = var.app_insights_enabled ? 1 : 0
  name                = "${local.name}-appinsights"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  application_type    = "web"
  workspace_id        = azurerm_log_analytics_workspace.this[0].id
  tags                = local.tags
}

resource "azurerm_key_vault" "this" {
  name                          = "${local.name}-kv"
  location                      = azurerm_resource_group.this.location
  resource_group_name           = azurerm_resource_group.this.name
  tenant_id                     = data.azurerm_client_config.current.tenant_id
  sku_name                      = "standard"
  soft_delete_retention_days    = 30
  purge_protection_enabled      = true
  public_network_access_enabled = var.key_vault_public_network_access_enabled

  tags = local.tags
}

resource "azurerm_key_vault_access_policy" "terraform" {
  key_vault_id = azurerm_key_vault.this.id
  tenant_id    = data.azurerm_client_config.current.tenant_id
  object_id    = data.azurerm_client_config.current.object_id

  secret_permissions = ["Get", "Set", "Delete", "Purge", "List"]
}

resource "azurerm_key_vault_secret" "connection_string" {
  name         = "connection-string"
  value        = local.db_connection_string
  key_vault_id = azurerm_key_vault.this.id

  depends_on = [azurerm_key_vault_access_policy.terraform]
}

resource "azurerm_key_vault_secret" "admin_password" {
  name         = "admin-password"
  value        = var.admin_password
  key_vault_id = azurerm_key_vault.this.id

  depends_on = [azurerm_key_vault_access_policy.terraform]
}

locals {
  base_app_settings = {
    FUNCTIONS_WORKER_RUNTIME                  = var.functions_worker_runtime
    FUNCTIONS_CUSTOMHANDLER_PORT              = tostring(var.container_port)
    WEBSITES_PORT                             = tostring(var.container_port)
    WEBSITES_ENABLE_APP_SERVICE_STORAGE       = "false"
    AzureWebJobsStorage                       = azurerm_storage_account.this.primary_connection_string
    ConnectionStrings__DefaultConnection      = "@Microsoft.KeyVault(SecretUri=${azurerm_key_vault_secret.connection_string.versionless_id})"
    HONUA_ADMIN_PASSWORD                      = "@Microsoft.KeyVault(SecretUri=${azurerm_key_vault_secret.admin_password.versionless_id})"
    Security__ConnectionEncryption__MasterKey = "@Microsoft.KeyVault(SecretUri=${azurerm_key_vault_secret.admin_password.versionless_id})"
    HONUA_SERVE_ADMIN_UI                      = var.serve_admin_ui ? "true" : "false"
    HONUA_ADMIN_UI                            = var.serve_admin_ui ? "true" : "false"
    HONUA_OBSERVABILITY                       = "true"
    HONUA_SKIP_MIGRATIONS                     = var.skip_migrations ? "true" : "false"
  }
  redis_settings = local.redis_connection != "" ? {
    ConnectionStrings__redis = local.redis_connection
  } : {}
  registry_settings = var.registry_server != "" ? {
    DOCKER_REGISTRY_SERVER_URL      = var.registry_server
    DOCKER_REGISTRY_SERVER_USERNAME = var.registry_username
    DOCKER_REGISTRY_SERVER_PASSWORD = var.registry_password
  } : {}
  app_insights_settings = var.app_insights_enabled ? {
    APPLICATIONINSIGHTS_CONNECTION_STRING = azurerm_application_insights.this[0].connection_string
    APPINSIGHTS_INSTRUMENTATIONKEY        = azurerm_application_insights.this[0].instrumentation_key
  } : {}
  app_settings = merge(local.base_app_settings, local.redis_settings, local.registry_settings, local.app_insights_settings, var.additional_env)

  image_parts         = split("/", var.image)
  image_registry      = local.image_parts[0]
  image_path_and_tag  = join("/", slice(local.image_parts, 1, length(local.image_parts)))
  image_path_parts    = split(":", local.image_path_and_tag)
  image_name          = local.image_path_parts[0]
  image_tag           = length(local.image_path_parts) > 1 ? local.image_path_parts[1] : "latest"
  image_registry_url  = var.registry_server != "" ? (startswith(var.registry_server, "http") ? var.registry_server : "https://${var.registry_server}") : "https://${local.image_registry}"
  image_registry_user = var.registry_username != "" ? var.registry_username : null
  image_registry_pass = var.registry_password != "" ? var.registry_password : null
}

resource "azurerm_linux_function_app" "this" {
  name                = "${local.name}-functions"
  resource_group_name = azurerm_resource_group.this.name
  location            = azurerm_resource_group.this.location
  service_plan_id     = azurerm_service_plan.this.id

  storage_account_name            = azurerm_storage_account.this.name
  storage_uses_managed_identity   = true
  key_vault_reference_identity_id = azurerm_user_assigned_identity.function.id

  https_only                  = true
  functions_extension_version = var.functions_extension_version

  app_settings = local.app_settings

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.function.id]
  }

  site_config {
    always_on                         = var.plan_sku_name != "Y1"
    health_check_path                 = "/healthz/ready"
    health_check_eviction_time_in_min = 2

    application_stack {
      docker {
        registry_url      = local.image_registry_url
        image_name        = local.image_name
        image_tag         = local.image_tag
        registry_username = local.image_registry_user
        registry_password = local.image_registry_pass
      }
    }
  }

  tags = local.tags

  # Azure injects and normalizes several Function App settings after create
  # (for example storage/account telemetry settings), which otherwise causes
  # perpetual drift during idempotency checks.
  lifecycle {
    ignore_changes = [
      app_settings["APPINSIGHTS_INSTRUMENTATIONKEY"],
      app_settings["APPLICATIONINSIGHTS_CONNECTION_STRING"],
      app_settings["AzureWebJobsStorage"],
      storage_account_access_key,
      site_config[0].application_insights_connection_string,
      site_config[0].application_insights_key
    ]
  }

  depends_on = [
    azurerm_key_vault_access_policy.function_app,
    azurerm_role_assignment.function_storage_blob,
    azurerm_role_assignment.function_storage_queue,
    azurerm_role_assignment.function_storage_table
  ]
}

resource "azurerm_key_vault_access_policy" "function_app" {
  key_vault_id = azurerm_key_vault.this.id
  tenant_id    = data.azurerm_client_config.current.tenant_id
  object_id    = azurerm_user_assigned_identity.function.principal_id

  secret_permissions = ["Get"]
}

resource "azurerm_role_assignment" "function_storage_blob" {
  scope                = azurerm_storage_account.this.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_user_assigned_identity.function.principal_id
}

resource "azurerm_role_assignment" "function_storage_queue" {
  scope                = azurerm_storage_account.this.id
  role_definition_name = "Storage Queue Data Contributor"
  principal_id         = azurerm_user_assigned_identity.function.principal_id
}

resource "azurerm_role_assignment" "function_storage_table" {
  scope                = azurerm_storage_account.this.id
  role_definition_name = "Storage Table Data Contributor"
  principal_id         = azurerm_user_assigned_identity.function.principal_id
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
