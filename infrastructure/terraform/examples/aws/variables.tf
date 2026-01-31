variable "region" {
  description = "AWS region."
  type        = string
  default     = "us-east-1"
}

variable "honua_admin_password" {
  description = "Admin password for Honua."
  type        = string
  sensitive   = true
}

variable "alb_certificate_arn" {
  description = "ACM certificate ARN for the ALB HTTPS listener."
  type        = string
  default     = ""
}
