provider "azurerm" {
  features {}
}

module "honua" {
  source = "../../modules/azure-functions"

  environment                   = var.environment
  name_prefix                   = var.name_prefix
  location                      = var.location
  image                         = var.honua_image
  admin_password                = var.honua_admin_password
  plan_sku_name                 = var.plan_sku_name
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
  skip_migrations               = var.skip_migrations
  tags                          = var.tags

  additional_env = {
    HONUA_SERVE_ADMIN_UI = "true"
    HONUA_ADMIN_UI       = "true"
    AllowedHosts         = "*"
  }
}

output "honua_url" {
  value = module.honua.function_app_url
}

output "function_app_name" {
  value = module.honua.function_app_name
}

output "db_fqdn" {
  value     = module.honua.db_fqdn
  sensitive = true
}

output "resource_group_name" {
  value = "${var.name_prefix}-${var.environment}-rg"
}
