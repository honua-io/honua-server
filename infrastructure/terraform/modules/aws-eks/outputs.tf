output "cluster_name" {
  description = "EKS cluster name."
  value       = module.eks.cluster_name
}

output "cluster_arn" {
  description = "EKS cluster ARN."
  value       = module.eks.cluster_arn
}

output "cluster_endpoint" {
  description = "EKS cluster endpoint."
  value       = module.eks.cluster_endpoint
}

output "cluster_certificate_authority_data" {
  description = "Base64 encoded EKS cluster CA data."
  value       = module.eks.cluster_certificate_authority_data
}

output "vpc_id" {
  description = "VPC ID used by the EKS cluster."
  value       = module.vpc.vpc_id
}
