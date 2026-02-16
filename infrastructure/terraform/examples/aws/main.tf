provider "aws" {
  region = var.region
}

module "honua" {
  source = "../../modules/aws-ecs"

  environment         = "dev"
  image               = "ghcr.io/honua-io/honua-server:latest"
  admin_password      = var.honua_admin_password
  alb_certificate_arn = var.alb_certificate_arn
  waf_web_acl_arn     = var.waf_web_acl_arn

  additional_env = {
    HONUA_SERVE_ADMIN_UI = "true"
    HONUA_ADMIN_UI       = "true"
  }
}

output "honua_url" {
  value = module.honua.service_url
}
