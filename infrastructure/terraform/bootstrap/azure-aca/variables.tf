variable "app_name" {
  type        = string
  description = "Azure AD application display name."
  default     = "honua-terraform-aca"
}

variable "role_name" {
  type        = string
  description = "Custom role name for the Terraform service principal."
  default     = "Honua Terraform ACA"
}

variable "scope" {
  type        = string
  description = "Scope for the role assignment (subscription or resource group). Leave empty for the current subscription."
  default     = ""
}

