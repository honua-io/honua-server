provider "azurerm" {
  features {}
}

module "honua" {
  source = "../../modules/azure-functions"

  environment       = var.environment
  name_prefix       = var.name_prefix
  location          = var.location
  image             = var.honua_image
  admin_password    = var.honua_admin_password
  plan_sku_name     = var.plan_sku_name
  db_admin_password = var.db_admin_password
  enable_postgis    = var.enable_postgis
  redis_enabled     = var.redis_enabled
  skip_migrations   = var.skip_migrations
  tags              = var.tags

  additional_env = {
    HONUA_SERVE_ADMIN_UI = "true"
    HONUA_ADMIN_UI       = "true"
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
