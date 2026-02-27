provider "aws" {
  region = var.region
}

module "honua" {
  source = "../../modules/aws-ecs"

  environment                   = var.environment
  name_prefix                   = var.name_prefix
  image                         = var.honua_image
  admin_password                = var.honua_admin_password
  db_password                   = var.db_password
  existing_db_endpoint          = var.existing_db_endpoint
  existing_db_connection_string = var.existing_db_connection_string
  db_publicly_accessible        = var.db_publicly_accessible
  db_additional_ingress_cidrs   = var.db_additional_ingress_cidrs
  enable_postgis                = var.enable_postgis
  redis_enabled                 = var.redis_enabled
  redis_connection_string       = var.redis_connection_string
  desired_count                 = var.desired_count
  alb_certificate_arn           = var.alb_certificate_arn
  waf_web_acl_arn               = var.waf_web_acl_arn
  tags                          = var.tags

  additional_env = {
    HONUA_SERVE_ADMIN_UI = "true"
    HONUA_ADMIN_UI       = "true"
    AllowedHosts         = "*"
  }
}

output "honua_url" {
  value = module.honua.service_url
}

output "ecs_cluster_name" {
  value = module.honua.ecs_cluster_name
}

output "ecs_service_name" {
  value = module.honua.ecs_service_name
}

output "db_endpoint" {
  value     = module.honua.db_endpoint
  sensitive = true
}

output "redis_primary_endpoint" {
  value     = module.honua.redis_primary_endpoint
  sensitive = true
}
