output "user_name" {
  value = aws_iam_user.terraform.name
}

output "policy_arn" {
  value = aws_iam_policy.terraform.arn
}

output "access_key_id" {
  value       = try(aws_iam_access_key.terraform[0].id, null)
  description = "Access key ID for the Terraform IAM user."
}

output "secret_access_key" {
  value       = try(aws_iam_access_key.terraform[0].secret, null)
  description = "Secret access key for the Terraform IAM user."
  sensitive   = true
}
