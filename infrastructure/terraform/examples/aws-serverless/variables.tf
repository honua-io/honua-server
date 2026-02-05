variable "region" {
  description = "AWS region."
  type        = string
  default     = "us-east-1"
}

variable "honua_admin_password" {
  description = "Admin API password for Honua."
  type        = string
  sensitive   = true
}

variable "honua_image_uri" {
  description = "ECR image URI for Honua (Lambda-compatible)."
  type        = string
}
