provider "azurerm" {
  features {}
}

module "honua" {
  source = "../../modules/azure-aca"

  environment    = "dev"
  location       = var.location
  image          = "ghcr.io/honua-io/honua-server:latest"
  admin_password = var.honua_admin_password

  additional_env = {
    HONUA_SERVE_ADMIN_UI = "true"
    HONUA_ADMIN_UI       = "true"
  }
}

output "honua_url" {
  value = module.honua.container_app_fqdn
}
