variable "name_prefix" {
  description = "Name prefix for resources."
  type        = string
  default     = "honua"
}

variable "environment" {
  description = "Environment suffix (dev, staging, prod, it)."
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

variable "node_count" {
  description = "AKS system node count."
  type        = number
  default     = 2
}

variable "node_vm_size" {
  description = "AKS node VM size."
  type        = string
  default     = "Standard_B2s"
}

variable "node_os_disk_size_gb" {
  description = "OS disk size for node pool VMs."
  type        = number
  default     = 64
}

variable "kubernetes_version" {
  description = "Optional AKS Kubernetes version. Leave empty to use provider default."
  type        = string
  default     = ""
}

variable "sku_tier" {
  description = "AKS SKU tier."
  type        = string
  default     = "Free"

  validation {
    condition     = contains(["Free", "Standard", "Premium"], var.sku_tier)
    error_message = "sku_tier must be 'Free', 'Standard', or 'Premium'."
  }
}

variable "authorized_ip_ranges" {
  description = "CIDR ranges authorized to access the AKS API server. Set to null to allow all."
  type        = list(string)
  default     = null
}

variable "local_account_disabled" {
  description = "Disable local Kubernetes accounts to enforce Azure AD authentication."
  type        = bool
  default     = false
}

variable "auto_scaling_enabled" {
  description = "Enable cluster autoscaler on the default node pool."
  type        = bool
  default     = true
}

variable "node_min_count" {
  description = "Minimum node count when auto-scaling is enabled."
  type        = number
  default     = 1
}

variable "node_max_count" {
  description = "Maximum node count when auto-scaling is enabled."
  type        = number
  default     = 5
}

variable "network_plugin" {
  description = "Kubernetes network plugin (azure or kubenet)."
  type        = string
  default     = "azure"

  validation {
    condition     = contains(["azure", "kubenet"], var.network_plugin)
    error_message = "network_plugin must be 'azure' or 'kubenet'."
  }
}

variable "network_policy" {
  description = "Kubernetes network policy provider (azure or calico)."
  type        = string
  default     = "azure"

  validation {
    condition     = contains(["azure", "calico", ""], var.network_policy)
    error_message = "network_policy must be 'azure', 'calico', or empty string."
  }
}

variable "log_analytics_workspace_id" {
  description = "Log Analytics workspace ID for AKS diagnostic logs. Empty to disable."
  type        = string
  default     = ""
}
