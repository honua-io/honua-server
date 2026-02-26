variable "app_name" {
  type        = string
  description = "Azure AD application display name."
  default     = "honua-terraform-aks"
}

variable "role_name" {
  type        = string
  description = "Custom role name for the Terraform service principal."
  default     = "Honua Terraform AKS"
}

variable "scope" {
  type        = string
  description = "Scope for the role assignment (subscription or resource group). Leave empty for the current subscription."
  default     = ""
}
