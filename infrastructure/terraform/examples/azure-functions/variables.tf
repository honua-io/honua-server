variable "location" {
  description = "Azure region."
  type        = string
  default     = "eastus"
}

variable "honua_admin_password" {
  description = "Admin API password for Honua."
  type        = string
  sensitive   = true
}

variable "honua_image" {
  description = "Container image (Functions-compatible)."
  type        = string
}

variable "plan_sku_name" {
  description = "Function App plan SKU (EP* for Premium, Y1 for Consumption)."
  type        = string
  default     = "EP1"
}
