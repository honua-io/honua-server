provider "azurerm" {
  features {}
}

module "honua" {
  source = "../../modules/azure-functions"

  environment    = "dev"
  location       = var.location
  image          = var.honua_image
  admin_password = var.honua_admin_password
  plan_sku_name  = var.plan_sku_name

  additional_env = {
    HONUA_ADMIN_UI = "true"
  }
}

output "honua_url" {
  value = module.honua.function_app_url
}
