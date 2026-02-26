variable "region" {
  description = "AWS region."
  type        = string
  default     = "us-east-1"
}

variable "environment" {
  description = "Environment name used in resource naming."
  type        = string
  default     = "it"
}

variable "name_prefix" {
  description = "Prefix used for resource names."
  type        = string
  default     = "honuaecs"
}

variable "honua_admin_password" {
  description = "Admin password for Honua."
  type        = string
  sensitive   = true
}

variable "db_password" {
  description = "PostgreSQL admin password used for deterministic integration tests."
  type        = string
  sensitive   = true
  default     = null
}

variable "honua_image" {
  description = "Container image to deploy to ECS."
  type        = string
  default     = "ghcr.io/honua-io/honua-server:latest-aot"
}

variable "db_publicly_accessible" {
  description = "Expose RDS publicly for integration testing."
  type        = bool
  default     = true
}

variable "db_additional_ingress_cidrs" {
  description = "Extra CIDRs allowed to connect to Postgres."
  type        = list(string)
  default     = []
}

variable "enable_postgis" {
  description = "Enable PostGIS and PostGIS Raster during apply."
  type        = bool
  default     = true
}

variable "redis_enabled" {
  description = "Provision ElastiCache Redis."
  type        = bool
  default     = true
}

variable "desired_count" {
  description = "Desired number of ECS tasks."
  type        = number
  default     = 1
}

variable "alb_certificate_arn" {
  description = "ACM certificate ARN for the ALB HTTPS listener."
  type        = string
  default     = ""
}

variable "waf_web_acl_arn" {
  description = "Optional WAFv2 Web ACL ARN associated to the ALB."
  type        = string
  default     = ""
}

variable "tags" {
  description = "Additional tags for resources."
  type        = map(string)
  default     = {}
}
