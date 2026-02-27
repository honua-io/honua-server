output "db_fqdn" {
  description = "PostgreSQL Flexible Server FQDN."
  value       = azurerm_postgresql_flexible_server.this.fqdn
}

output "db_connection_string" {
  description = "PostgreSQL connection string."
  value       = local.db_connection_string
  sensitive   = true
}

output "redis_connection_string" {
  description = "Redis primary connection string (empty if redis_enabled is false)."
  value       = local.redis_connection
  sensitive   = true
}

output "key_vault_id" {
  description = "Key Vault resource ID."
  value       = azurerm_key_vault.this.id
}

output "key_vault_name" {
  description = "Key Vault name."
  value       = azurerm_key_vault.this.name
}

output "db_connection_secret_id" {
  description = "Key Vault secret ID for the DB connection string."
  value       = azurerm_key_vault_secret.db_connection.id
}

output "admin_password_secret_id" {
  description = "Key Vault secret ID for the admin password."
  value       = azurerm_key_vault_secret.admin_password.id
}

output "redis_connection_secret_id" {
  description = "Key Vault secret ID for the Redis connection string (null if Redis is disabled)."
  value       = var.redis_enabled ? azurerm_key_vault_secret.redis_connection[0].id : null
  sensitive   = true
}

output "resource_group_name" {
  description = "Resource group name containing the data tier resources."
  value       = azurerm_resource_group.this.name
}
