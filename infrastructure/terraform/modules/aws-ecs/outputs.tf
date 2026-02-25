output "alb_dns_name" {
  description = "DNS name of the Application Load Balancer."
  value       = aws_lb.this.dns_name
}

output "service_url" {
  description = "Convenience URL for the service."
  value       = local.use_https ? "https://${aws_lb.this.dns_name}" : "http://${aws_lb.this.dns_name}"
}

output "ecs_cluster_name" {
  description = "ECS cluster name."
  value       = aws_ecs_cluster.this.name
}

output "ecs_service_name" {
  description = "ECS service name."
  value       = aws_ecs_service.this.name
}

output "db_endpoint" {
  description = "RDS endpoint address."
  value       = module.rds.db_instance_address
  sensitive   = true
}

output "db_connection_secret_arn" {
  description = "Secrets Manager ARN for the DB connection string."
  value       = aws_secretsmanager_secret.db_connection.arn
}

output "admin_password_secret_arn" {
  description = "Secrets Manager ARN for the admin password."
  value       = aws_secretsmanager_secret.admin_password.arn
}

output "certificate_arn" {
  description = "ACM certificate ARN in use (if any)."
  value       = local.certificate_arn != "" ? local.certificate_arn : null
}

output "redis_connection_secret_arn" {
  description = "Secrets Manager ARN for the Redis connection string (if set)."
  value       = local.redis_connection != "" ? aws_secretsmanager_secret.redis_connection[0].arn : null
  sensitive   = true
}

output "redis_primary_endpoint" {
  description = "Redis primary endpoint address (if created)."
  value       = local.redis_create ? aws_elasticache_replication_group.redis[0].primary_endpoint_address : null
  sensitive   = true
}
