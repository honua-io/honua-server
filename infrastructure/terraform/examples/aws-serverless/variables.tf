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
  default     = "honuasl"
}

variable "honua_admin_password" {
  description = "Admin API password for Honua."
  type        = string
  sensitive   = true
}

variable "db_password" {
  description = "PostgreSQL admin password used for deterministic integration tests."
  type        = string
  sensitive   = true
  default     = null
}

variable "honua_image_uri" {
  description = "ECR image URI for Honua (Lambda-compatible)."
  type        = string
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

variable "skip_migrations" {
  description = "Skip migrations on Lambda startup."
  type        = bool
  default     = true
}

variable "tags" {
  description = "Additional tags for resources."
  type        = map(string)
  default     = {}
}
