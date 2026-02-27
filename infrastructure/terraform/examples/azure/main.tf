provider "azurerm" {
  features {}
}

module "honua" {
  source = "../../modules/azure-aca"

  environment                   = var.environment
  name_prefix                   = var.name_prefix
  location                      = var.location
  image                         = var.honua_image
  admin_password                = var.honua_admin_password
  db_admin_password             = var.db_admin_password
  existing_db_fqdn              = var.existing_db_fqdn
  existing_db_connection_string = var.existing_db_connection_string
  db_firewall_start_ip          = var.db_firewall_start_ip
  db_firewall_end_ip            = var.db_firewall_end_ip
  enable_postgis                = var.enable_postgis
  redis_enabled                 = var.redis_enabled
  redis_connection_string       = var.redis_connection_string
  redis_sku_name                = "Basic"
  redis_family                  = "C"
  redis_capacity                = 0
  min_replicas                  = var.min_replicas
  max_replicas                  = var.max_replicas
  key_vault_default_action      = var.key_vault_default_action
  tags                          = var.tags

  additional_env = {
    HONUA_SERVE_ADMIN_UI = "true"
    HONUA_ADMIN_UI       = "true"
    AllowedHosts         = "*"
  }
}

output "honua_url" {
  value = module.honua.container_app_fqdn
}

output "container_app_name" {
  value = module.honua.container_app_name
}

output "database_fqdn" {
  value     = module.honua.database_fqdn
  sensitive = true
}

output "resource_group_name" {
  value = "${var.name_prefix}-${var.environment}-rg"
}
