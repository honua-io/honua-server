provider "aws" {
  region = var.region
}

module "honua" {
  source = "../../modules/aws-serverless"

  environment    = "dev"
  image          = var.honua_image_uri
  admin_password = var.honua_admin_password

  additional_env = {
    HONUA_ADMIN_UI = "true"
  }
}

output "honua_url" {
  value = module.honua.api_endpoint
}
