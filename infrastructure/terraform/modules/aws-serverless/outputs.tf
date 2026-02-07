output "api_endpoint" {
  value = aws_apigatewayv2_api.this.api_endpoint
}

output "lambda_function_name" {
  value = aws_lambda_function.this.function_name
}

output "db_endpoint" {
  value     = module.rds.db_instance_address
  sensitive = true
}

output "db_connection_string" {
  value     = local.db_connection_string
  sensitive = true
}

output "redis_connection_string" {
  value     = local.redis_connection
  sensitive = true
}
