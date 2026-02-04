variable "app_name" {
  type        = string
  description = "Azure AD application display name."
  default     = "honua-terraform-functions"
}

variable "role_name" {
  type        = string
  description = "Custom role name for the Terraform service principal."
  default     = "Honua Terraform Functions"
}

variable "scope" {
  type        = string
  description = "Scope for the role assignment (subscription or resource group). Leave empty for the current subscription."
  default     = ""
}

variable "client_secret" {
  type        = string
  description = "Optional client secret value. If empty, one will be generated."
  default     = ""
}
