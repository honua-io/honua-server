provider "azurerm" {
  features {}
}

# Provision data tier (PostgreSQL + Redis + Key Vault) independently.
module "data" {
  source = "../../modules/azure-data"

  environment              = var.environment
  name_prefix              = var.name_prefix
  location                 = var.location
  admin_password           = var.honua_admin_password
  db_admin_password        = var.db_admin_password
  db_sku_name              = var.db_sku_name
  db_storage_mb            = var.db_storage_mb
  db_public_network_access = var.db_public_network_access
  db_firewall_start_ip     = var.db_firewall_start_ip
  db_firewall_end_ip       = var.db_firewall_end_ip
  enable_postgis           = var.enable_postgis
  redis_enabled            = var.redis_enabled
  redis_sku_name           = var.redis_sku_name
  redis_family             = var.redis_family
  redis_capacity           = var.redis_capacity
  redis_public_network_access_enabled = var.redis_public_network_access_enabled
  key_vault_default_action = var.key_vault_default_action
  tags                     = var.tags
}

output "db_fqdn" {
  value = module.data.db_fqdn
}

output "db_connection_string" {
  value     = module.data.db_connection_string
  sensitive = true
}

output "redis_connection_string" {
  value     = module.data.redis_connection_string
  sensitive = true
}

output "key_vault_id" {
  value = module.data.key_vault_id
}

output "key_vault_name" {
  value = module.data.key_vault_name
}

output "resource_group_name" {
  value = module.data.resource_group_name
}
