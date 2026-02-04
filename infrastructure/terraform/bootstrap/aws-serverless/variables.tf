variable "aws_region" {
  type        = string
  description = "AWS region to create the IAM user in."
  default     = "us-east-1"
}

variable "name_prefix" {
  type        = string
  description = "Prefix for the Terraform IAM user name."
  default     = "honua-terraform"
}

variable "environment" {
  type        = string
  description = "Environment name used in the IAM user name."
  default     = "dev"
}

variable "user_name" {
  type        = string
  description = "Override for the IAM user name."
  default     = ""
}

variable "create_access_key" {
  type        = bool
  description = "Whether to create an access key for the IAM user."
  default     = true
}

variable "tags" {
  type        = map(string)
  description = "Tags to apply to the IAM user and policy."
  default     = {}
}
