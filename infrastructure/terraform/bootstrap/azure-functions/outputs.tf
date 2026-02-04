output "client_id" {
  value = azuread_application.terraform.application_id
}

output "client_secret" {
  value     = azuread_service_principal_password.terraform.value
  sensitive = true
}

output "tenant_id" {
  value = data.azurerm_client_config.current.tenant_id
}

output "subscription_id" {
  value = data.azurerm_subscription.current.subscription_id
}

output "scope" {
  value = local.scope
}

output "role_definition_id" {
  value = azurerm_role_definition.terraform.role_definition_resource_id
}
