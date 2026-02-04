variable "name_prefix" {
  description = "Name prefix for resources."
  type        = string
  default     = "honua"
}

variable "environment" {
  description = "Environment name (dev, staging, prod)."
  type        = string
  default     = "dev"
}

variable "location" {
  description = "Azure region."
  type        = string
  default     = "eastus"
}

variable "tags" {
  description = "Additional tags to apply to resources."
  type        = map(string)
  default     = {}
}

variable "image" {
  description = "Container image (AOT or JIT)."
  type        = string
  default     = "ghcr.io/honua-io/honua-server:latest"
}

variable "container_cpu" {
  description = "Container CPU cores."
  type        = number
  default     = 0.5
}

variable "container_memory" {
  description = "Container memory in GiB."
  type        = number
  default     = 1.0
}

variable "container_port" {
  description = "Container port exposed by Honua Server."
  type        = number
  default     = 8080
}

variable "min_replicas" {
  description = "Minimum replicas for Container Apps."
  type        = number
  default     = 1
}

variable "max_replicas" {
  description = "Maximum replicas for Container Apps."
  type        = number
  default     = 5
}

variable "admin_password" {
  description = "Admin API password for Honua (required in non-dev)."
  type        = string
  sensitive   = true
}

variable "db_admin_username" {
  description = "PostgreSQL admin username."
  type        = string
  default     = "honua"
}

variable "db_admin_password" {
  description = "PostgreSQL admin password. Leave null to auto-generate."
  type        = string
  sensitive   = true
  default     = null
}

variable "db_name" {
  description = "PostgreSQL database name."
  type        = string
  default     = "honua"
}

variable "db_sku_name" {
  description = "SKU name for Azure Database for PostgreSQL Flexible Server."
  type        = string
  default     = "B_Standard_B1ms"
}

variable "db_storage_mb" {
  description = "Storage in MB for PostgreSQL Flexible Server."
  type        = number
  default     = 32768
}

variable "db_version" {
  description = "PostgreSQL version."
  type        = string
  default     = "16"
}

variable "db_public_network_access" {
  description = "Enable public network access to the PostgreSQL server."
  type        = bool
  default     = true
}

variable "db_geo_redundant_backup_enabled" {
  description = "Enable geo-redundant backups for PostgreSQL Flexible Server."
  type        = bool
  default     = true
}

variable "enable_postgis" {
  description = "Attempt to enable PostGIS via local-exec (requires psql + network access)."
  type        = bool
  default     = false
}

variable "additional_env" {
  description = "Additional environment variables for the container."
  type        = map(string)
  default     = {}
}

variable "redis_connection_string" {
  description = "Redis connection string for multi-node mode. Leave empty to create Redis."
  type        = string
  default     = ""
  sensitive   = true
}

variable "redis_enabled" {
  description = "Provision Azure Cache for Redis."
  type        = bool
  default     = true
}

variable "redis_sku_name" {
  description = "Azure Cache for Redis SKU name."
  type        = string
  default     = "Basic"
}

variable "redis_family" {
  description = "Azure Cache for Redis family."
  type        = string
  default     = "C"
}

variable "redis_capacity" {
  description = "Azure Cache for Redis capacity."
  type        = number
  default     = 1
}

variable "redis_enable_non_ssl_port" {
  description = "Enable non-SSL port for Azure Cache for Redis."
  type        = bool
  default     = false
}

variable "registry_server" {
  description = "Container registry server (optional)."
  type        = string
  default     = ""
}

variable "registry_username" {
  description = "Container registry username (optional)."
  type        = string
  default     = ""
}

variable "registry_password" {
  description = "Container registry password (optional)."
  type        = string
  default     = ""
  sensitive   = true
}

variable "key_vault_purge_protection_enabled" {
  description = "Enable purge protection on the Key Vault."
  type        = bool
  default     = true
}

variable "key_vault_public_network_access_enabled" {
  description = "Allow public network access to Key Vault."
  type        = bool
  default     = true
}

variable "key_vault_default_action" {
  description = "Default action for Key Vault network ACLs."
  type        = string
  default     = "Deny"
}

variable "key_vault_bypass" {
  description = "Key Vault network ACL bypass."
  type        = string
  default     = "AzureServices"
}

variable "key_vault_ip_rules" {
  description = "IP rules allowed to access Key Vault."
  type        = list(string)
  default     = []
}

variable "secret_expiration_days" {
  description = "Days until Key Vault secrets expire."
  type        = number
  default     = 365
}

variable "enable_ingress" {
  description = "Expose Container App via external ingress."
  type        = bool
  default     = true
}

variable "log_analytics_enabled" {
  description = "Enable Log Analytics workspace for Container Apps environment."
  type        = bool
  default     = true
}
