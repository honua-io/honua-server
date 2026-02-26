provider "aws" {
  region = var.region
}

module "honua" {
  source = "../../modules/aws-serverless"

  environment                 = var.environment
  name_prefix                 = var.name_prefix
  image                       = var.honua_image_uri
  admin_password              = var.honua_admin_password
  db_password                 = var.db_password
  db_publicly_accessible      = var.db_publicly_accessible
  db_additional_ingress_cidrs = var.db_additional_ingress_cidrs
  enable_postgis              = var.enable_postgis
  redis_enabled               = var.redis_enabled
  skip_migrations             = var.skip_migrations
  tags                        = var.tags

  additional_env = {
    HONUA_SERVE_ADMIN_UI = "true"
    HONUA_ADMIN_UI       = "true"
  }
}

output "honua_url" {
  value = module.honua.api_endpoint
}

output "lambda_function_name" {
  value = module.honua.lambda_function_name
}

output "db_endpoint" {
  value     = module.honua.db_endpoint
  sensitive = true
}

output "redis_connection_string" {
  value     = module.honua.redis_connection_string
  sensitive = true
}
