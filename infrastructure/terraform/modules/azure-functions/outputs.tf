output "function_app_name" {
  value = azurerm_linux_function_app.this.name
}

output "function_app_url" {
  value = "https://${azurerm_linux_function_app.this.default_hostname}"
}

output "db_fqdn" {
  value     = azurerm_postgresql_flexible_server.this.fqdn
  sensitive = true
}

output "db_connection_string" {
  value     = local.db_connection_string
  sensitive = true
}

output "redis_connection_string" {
  value     = local.redis_connection
  sensitive = true
}
