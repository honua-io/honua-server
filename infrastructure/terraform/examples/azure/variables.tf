variable "location" {
  description = "Azure region."
  type        = string
  default     = "eastus"
}

variable "honua_admin_password" {
  description = "Admin password for Honua."
  type        = string
  sensitive   = true
}
