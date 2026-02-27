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
  description = "The scope for the role assignment. Defaults to current subscription. Recommended: set to a specific resource group ID."
  type        = string
  default     = ""

  validation {
    condition     = var.scope == "" || can(regex("^/subscriptions/", var.scope))
    error_message = "scope must be empty (for subscription) or a valid Azure resource scope starting with /subscriptions/."
  }
}
