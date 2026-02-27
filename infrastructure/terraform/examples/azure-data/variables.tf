variable "location" {
  description = "Azure region."
  type        = string
  default     = "eastus"
}

variable "environment" {
  description = "Environment name used in resource naming."
  type        = string
  default     = "dev"
}

variable "name_prefix" {
  description = "Prefix used for resource names."
  type        = string
  default     = "honua"
}

variable "honua_admin_password" {
  description = "Admin password for Honua."
  type        = string
  sensitive   = true
}

variable "db_admin_password" {
  description = "PostgreSQL admin password. Leave null to auto-generate."
  type        = string
  sensitive   = true
  default     = null
}

variable "db_sku_name" {
  description = "SKU for PostgreSQL Flexible Server."
  type        = string
  default     = "B_Standard_B1ms"
}

variable "db_storage_mb" {
  description = "Storage (MB) for PostgreSQL Flexible Server."
  type        = number
  default     = 32768
}

variable "db_public_network_access" {
  description = "Enable public network access to PostgreSQL for validation runs."
  type        = bool
  default     = true
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

variable "redis_sku_name" {
  description = "Redis SKU."
  type        = string
  default     = "Basic"
}

variable "redis_family" {
  description = "Redis family."
  type        = string
  default     = "C"
}

variable "redis_capacity" {
  description = "Redis capacity."
  type        = number
  default     = 0
}

variable "redis_public_network_access_enabled" {
  description = "Enable Redis public network access for validation runs."
  type        = bool
  default     = true
}

variable "key_vault_default_action" {
  description = "Key Vault network ACL default action (Allow is useful for local integration tests)."
  type        = string
  default     = "Deny"
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

variable "tags" {
  description = "Additional tags for resources."
  type        = map(string)
  default     = {}
}
