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
}

resource "azurerm_resource_group" "this" {
  name     = "${local.name}-rg"
  location = var.location
  tags     = local.tags
}

resource "azurerm_storage_account" "this" {
  name                     = local.storage_account_name
  resource_group_name      = azurerm_resource_group.this.name
  location                 = azurerm_resource_group.this.location
  account_tier             = var.storage_account_tier
  account_replication_type = var.storage_account_replication_type
  min_tls_version          = "TLS1_2"
  tags                     = local.tags
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
  count            = var.db_admin_password == null ? 1 : 0
  length           = 32
  special          = true
  override_special = "#%*()-_=+[]{}:?"
}

locals {
  db_password          = var.db_admin_password != null ? var.db_admin_password : random_password.db[0].result
  db_connection_string = "Host=${azurerm_postgresql_flexible_server.this.fqdn};Port=5432;Database=${var.db_name};Username=${var.db_admin_username};Password=${local.db_password};SSL Mode=Require;Trust Server Certificate=false"
  redis_enabled        = var.redis_enabled || var.redis_connection_string != ""
  redis_create         = var.redis_enabled && var.redis_connection_string == ""
  redis_connection     = var.redis_connection_string != "" ? var.redis_connection_string : (local.redis_create ? azurerm_redis_cache.this[0].primary_connection_string : "")
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

  public_network_access_enabled = var.db_public_network_access
  geo_redundant_backup_enabled  = var.db_geo_redundant_backup_enabled

  tags = local.tags
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
  value     = "POSTGIS"
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

resource "azurerm_application_insights" "this" {
  count               = var.app_insights_enabled ? 1 : 0
  name                = "${local.name}-appinsights"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  application_type    = "web"
  tags                = local.tags
}

locals {
  base_app_settings = {
    FUNCTIONS_WORKER_RUNTIME             = var.functions_worker_runtime
    FUNCTIONS_CUSTOMHANDLER_PORT         = tostring(var.container_port)
    WEBSITES_PORT                        = tostring(var.container_port)
    WEBSITES_ENABLE_APP_SERVICE_STORAGE  = "false"
    AzureWebJobsStorage                  = azurerm_storage_account.this.primary_connection_string
    ConnectionStrings__DefaultConnection = local.db_connection_string
    HONUA_ADMIN_PASSWORD                 = var.admin_password
    HONUA_SKIP_MIGRATIONS                = var.skip_migrations ? "true" : "false"
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
}

resource "azurerm_linux_function_app" "this" {
  name                = "${local.name}-functions"
  resource_group_name = azurerm_resource_group.this.name
  location            = azurerm_resource_group.this.location
  service_plan_id     = azurerm_service_plan.this.id

  storage_account_name       = azurerm_storage_account.this.name
  storage_account_access_key = azurerm_storage_account.this.primary_access_key

  https_only                  = true
  functions_extension_version = var.functions_extension_version

  app_settings = local.app_settings

  site_config {
    linux_fx_version  = "DOCKER|${var.image}"
    always_on         = var.plan_sku_name != "Y1"
    health_check_path = "/healthz/ready"
  }

  tags = local.tags
}

resource "null_resource" "enable_postgis" {
  count = var.enable_postgis ? 1 : 0

  triggers = {
    db_endpoint = azurerm_postgresql_flexible_server.this.fqdn
  }

  provisioner "local-exec" {
    command = <<-EOT
      set -e
      echo "Enabling PostGIS on ${azurerm_postgresql_flexible_server.this.fqdn}" \
        && PGPASSWORD='${local.db_password}' psql \
          --host=${azurerm_postgresql_flexible_server.this.fqdn} \
          --username=${var.db_admin_username} \
          --dbname=${var.db_name} \
          --command="CREATE EXTENSION IF NOT EXISTS postgis;"
    EOT
  }

  depends_on = [azurerm_postgresql_flexible_server_database.this]
}
