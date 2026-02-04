output "container_app_name" {
  description = "Container App name."
  value       = azurerm_container_app.this.name
}

output "container_app_fqdn" {
  description = "Container App FQDN (if ingress enabled)."
  value       = try(azurerm_container_app.this.ingress[0].fqdn, null)
}

output "database_fqdn" {
  description = "PostgreSQL server FQDN."
  value       = azurerm_postgresql_flexible_server.this.fqdn
  sensitive   = true
}

output "key_vault_id" {
  description = "Key Vault resource ID."
  value       = azurerm_key_vault.this.id
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
  description = "Key Vault secret ID for the Redis connection string (if set)."
  value       = local.redis_connection != "" ? azurerm_key_vault_secret.redis_connection[0].id : null
}
