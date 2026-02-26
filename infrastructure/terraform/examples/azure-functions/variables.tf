variable "location" {
  description = "Azure region."
  type        = string
  default     = "eastus"
}

variable "environment" {
  description = "Environment name used in resource naming."
  type        = string
  default     = "it"
}

variable "name_prefix" {
  description = "Prefix used for resource names."
  type        = string
  default     = "honuafn"
}

variable "honua_admin_password" {
  description = "Admin API password for Honua."
  type        = string
  sensitive   = true
}

variable "db_admin_password" {
  description = "PostgreSQL admin password. Set for deterministic integration tests."
  type        = string
  sensitive   = true
  default     = null
}

variable "existing_db_fqdn" {
  description = "Optional existing PostgreSQL FQDN to reuse."
  type        = string
  default     = ""
}

variable "existing_db_connection_string" {
  description = "Optional existing PostgreSQL connection string to reuse."
  type        = string
  sensitive   = true
  default     = ""
}

variable "honua_image" {
  description = "Container image (Functions-compatible)."
  type        = string
  default     = "ghcr.io/honua-io/honua-server:latest"
}

variable "plan_sku_name" {
  description = "Function App plan SKU (EP* for Premium, Y1 for Consumption)."
  type        = string
  default     = "EP1"
}

variable "enable_postgis" {
  description = "Enable PostGIS and PostGIS Raster during apply."
  type        = bool
  default     = true
}

variable "redis_enabled" {
  description = "Provision Azure Cache for Redis."
  type        = bool
  default     = true
}

variable "redis_connection_string" {
  description = "Optional existing Redis connection string to reuse."
  type        = string
  sensitive   = true
  default     = ""
}

variable "db_firewall_start_ip" {
  description = "Optional PostgreSQL firewall start IP."
  type        = string
  default     = ""
}

variable "db_firewall_end_ip" {
  description = "Optional PostgreSQL firewall end IP."
  type        = string
  default     = ""
}

variable "skip_migrations" {
  description = "Skip database migrations on startup."
  type        = bool
  default     = true
}

variable "tags" {
  description = "Additional tags for resources."
  type        = map(string)
  default     = {}
}
