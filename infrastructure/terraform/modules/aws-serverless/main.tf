data "aws_availability_zones" "available" {
  state = "available"
}

data "aws_region" "current" {}

data "aws_caller_identity" "current" {}

locals {
  name = "${var.name_prefix}-${var.environment}"
  tags = merge({
    Project     = "honua-server"
    Environment = var.environment
    ManagedBy   = "terraform"
  }, var.tags)
  redis_enabled    = var.redis_enabled || var.redis_connection_string != ""
  redis_create     = var.redis_enabled && var.redis_connection_string == ""
  redis_auth_token = var.redis_auth_token != "" ? var.redis_auth_token : (local.redis_create ? random_password.redis_auth[0].result : "")
  redis_connection = var.redis_connection_string != "" ? var.redis_connection_string : (local.redis_create ? "${aws_elasticache_replication_group.redis[0].primary_endpoint_address}:${var.redis_port},password=${local.redis_auth_token},ssl=true" : "")
}

resource "random_password" "db" {
  count            = var.db_password == null ? 1 : 0
  length           = 32
  special          = true
  override_special = "#%*()-_=+[]{}:?"
}

resource "random_password" "redis_auth" {
  count            = local.redis_create && var.redis_auth_token == "" ? 1 : 0
  length           = 32
  special          = true
  override_special = "#%*()-_=+[]{}:?"
}

locals {
  db_password          = var.db_password != null ? var.db_password : random_password.db[0].result
  db_ssl               = var.db_require_ssl ? ";SSL Mode=Require;Trust Server Certificate=false" : ""
  db_connection_string = "Host=${module.rds.db_instance_address};Port=5432;Database=${var.db_name};Username=${var.db_username};Password=${local.db_password}${local.db_ssl}"
  # NOTE: Lambda environment variables are encrypted at rest by AWS KMS but are visible
  # in the AWS Console/API. For enhanced secret management, use the AWS Parameters and
  # Secrets Lambda Extension to resolve secrets from Secrets Manager at runtime.
  # See: https://docs.aws.amazon.com/systems-manager/latest/userguide/ps-integration-lambda-extensions.html
  lambda_environment = merge({
    ConnectionStrings__DefaultConnection = local.db_connection_string
    HONUA_ADMIN_PASSWORD                 = var.admin_password
    HONUA_SKIP_MIGRATIONS                = var.skip_migrations ? "true" : "false"
    }, local.redis_connection != "" ? {
    ConnectionStrings__redis = local.redis_connection
  } : {}, var.additional_env)
}

#checkov:skip=CKV_TF_1: Registry modules are version-pinned.
module "vpc" {
  #checkov:skip=CKV_TF_1: Registry modules are version-pinned.
  #checkov:skip=CKV2_AWS_12: Default SG is managed via module inputs.
  source  = "terraform-aws-modules/vpc/aws"
  version = "~> 5.0"

  name = "${local.name}-vpc"
  cidr = var.vpc_cidr

  azs             = slice(data.aws_availability_zones.available.names, 0, length(var.public_subnet_cidrs))
  public_subnets  = var.public_subnet_cidrs
  private_subnets = var.private_subnet_cidrs

  enable_nat_gateway             = var.enable_nat_gateway
  enable_dns_support             = true
  enable_dns_hostnames           = true
  manage_default_security_group  = true
  default_security_group_ingress = []
  default_security_group_egress  = []

  tags = local.tags
}

#checkov:skip=CKV2_AWS_5: Security group is attached to the Lambda function.
resource "aws_security_group" "lambda" {
  #checkov:skip=CKV2_AWS_5: Security group is attached to the Lambda function.
  name_prefix = "${local.name}-lambda-"
  description = "Lambda security group"
  vpc_id      = module.vpc.vpc_id

  egress {
    description = "Database and Redis access"
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = [module.vpc.vpc_cidr_block]
  }

  egress {
    description = "Outbound HTTPS"
    from_port   = 443
    to_port     = 443
    protocol    = "tcp"
    cidr_blocks = ["0.0.0.0/0"]
  }

  tags = local.tags
}

#checkov:skip=CKV2_AWS_5: Security group is attached to the RDS instance.
resource "aws_security_group" "rds" {
  #checkov:skip=CKV2_AWS_5: Security group is attached to the RDS instance.
  name_prefix = "${local.name}-rds-"
  description = "RDS security group"
  vpc_id      = module.vpc.vpc_id

  ingress {
    description     = "PostgreSQL from Lambda"
    from_port       = 5432
    to_port         = 5432
    protocol        = "tcp"
    security_groups = [aws_security_group.lambda.id]
  }

  dynamic "ingress" {
    for_each = toset(var.db_additional_ingress_cidrs)
    content {
      description = "PostgreSQL additional CIDR ingress"
      from_port   = 5432
      to_port     = 5432
      protocol    = "tcp"
      cidr_blocks = [ingress.value]
    }
  }

  egress {
    description = "Outbound HTTPS"
    from_port   = 443
    to_port     = 443
    protocol    = "tcp"
    cidr_blocks = ["0.0.0.0/0"]
  }

  tags = local.tags
}

resource "aws_security_group" "redis" {
  count       = local.redis_create ? 1 : 0
  name_prefix = "${local.name}-redis-"
  description = "Redis security group"
  vpc_id      = module.vpc.vpc_id

  ingress {
    description     = "Redis from Lambda"
    from_port       = var.redis_port
    to_port         = var.redis_port
    protocol        = "tcp"
    security_groups = [aws_security_group.lambda.id]
  }

  egress {
    description = "Redis outbound"
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = [module.vpc.vpc_cidr_block]
  }

  tags = local.tags
}

resource "aws_elasticache_subnet_group" "redis" {
  count       = local.redis_create ? 1 : 0
  name        = "${local.name}-redis"
  subnet_ids  = module.vpc.private_subnets
  description = "Redis subnet group"
  tags        = local.tags
}

resource "aws_elasticache_replication_group" "redis" {
  count                      = local.redis_create ? 1 : 0
  replication_group_id       = "${local.name}-redis"
  description                = "Honua Redis"
  node_type                  = var.redis_node_type
  engine                     = "redis"
  engine_version             = var.redis_engine_version
  port                       = var.redis_port
  parameter_group_name       = var.redis_parameter_group_name
  automatic_failover_enabled = var.redis_num_cache_clusters >= 2
  multi_az_enabled           = var.redis_num_cache_clusters >= 2
  num_cache_clusters         = var.redis_num_cache_clusters
  subnet_group_name          = aws_elasticache_subnet_group.redis[0].name
  security_group_ids         = [aws_security_group.redis[0].id]
  at_rest_encryption_enabled = true
  transit_encryption_enabled = true
  auth_token                 = local.redis_auth_token
  apply_immediately          = true
  tags                       = local.tags

  lifecycle {
    precondition {
      condition     = var.redis_num_cache_clusters >= 2
      error_message = "redis_num_cache_clusters must be >= 2 when provisioning Redis with multi-AZ failover."
    }
  }
}

#checkov:skip=CKV_TF_1: Registry modules are version-pinned.
#checkov:skip=CKV_AWS_133: Backup retention is configured in this module call.
#checkov:skip=CKV_AWS_304: Secret rotation is handled outside this module.
module "rds" {
  #checkov:skip=CKV_TF_1: Registry modules are version-pinned.
  #checkov:skip=CKV_AWS_133: Backup retention is configured in this module call.
  #checkov:skip=CKV_AWS_304: Secret rotation is handled outside this module.
  source  = "terraform-aws-modules/rds/aws"
  version = "~> 6.0"

  identifier = "${local.name}-postgres"

  engine               = "postgres"
  engine_version       = "15.4"
  family               = "postgres15"
  major_engine_version = "15"
  instance_class       = var.db_instance_class

  allocated_storage     = var.db_allocated_storage
  max_allocated_storage = 100
  storage_encrypted     = true

  db_name  = var.db_name
  username = var.db_username
  password = local.db_password
  port     = 5432

  vpc_security_group_ids = [aws_security_group.rds.id]
  subnet_ids             = module.vpc.private_subnets

  publicly_accessible = var.db_publicly_accessible
  multi_az            = var.db_multi_az

  backup_retention_period = var.environment == "prod" ? 7 : 3
  maintenance_window      = "Sun:04:00-Sun:05:00"

  tags = local.tags
}

data "aws_iam_policy_document" "lambda_assume" {
  statement {
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["lambda.amazonaws.com"]
    }
  }
}

resource "aws_iam_role" "lambda" {
  name_prefix        = "${local.name}-lambda-"
  assume_role_policy = data.aws_iam_policy_document.lambda_assume.json
  tags               = local.tags
}

resource "aws_iam_role_policy_attachment" "lambda_basic" {
  role       = aws_iam_role.lambda.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole"
}

resource "aws_iam_role_policy_attachment" "lambda_vpc" {
  role       = aws_iam_role.lambda.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AWSLambdaVPCAccessExecutionRole"
}

resource "aws_cloudwatch_log_group" "lambda" {
  name              = "/aws/lambda/${local.name}-honua"
  retention_in_days = var.log_retention_days
  tags              = local.tags
}

resource "aws_lambda_function" "this" {
  function_name = "${local.name}-honua"
  role          = aws_iam_role.lambda.arn
  package_type  = "Image"
  image_uri     = var.image

  memory_size = var.lambda_memory_size
  timeout     = var.lambda_timeout_seconds

  architectures = var.lambda_architectures

  ephemeral_storage {
    size = var.lambda_ephemeral_storage_mb
  }

  reserved_concurrent_executions = var.lambda_reserved_concurrent_executions

  vpc_config {
    subnet_ids         = module.vpc.private_subnets
    security_group_ids = [aws_security_group.lambda.id]
  }

  environment {
    variables = local.lambda_environment
  }

  depends_on = [aws_cloudwatch_log_group.lambda]

  tags = local.tags
}

resource "aws_cloudwatch_log_group" "api_gateway" {
  name              = "/aws/apigateway/${local.name}-honua"
  retention_in_days = var.log_retention_days
  tags              = local.tags
}

resource "aws_apigatewayv2_api" "this" {
  name          = "${local.name}-honua"
  protocol_type = "HTTP"
  tags          = local.tags
}

resource "aws_apigatewayv2_integration" "lambda" {
  api_id                 = aws_apigatewayv2_api.this.id
  integration_type       = "AWS_PROXY"
  integration_uri        = aws_lambda_function.this.invoke_arn
  payload_format_version = "2.0"
  timeout_milliseconds   = min(30000, var.lambda_timeout_seconds * 1000)
}

resource "aws_apigatewayv2_route" "root" {
  api_id    = aws_apigatewayv2_api.this.id
  route_key = "ANY /"
  target    = "integrations/${aws_apigatewayv2_integration.lambda.id}"
}

resource "aws_apigatewayv2_route" "proxy" {
  api_id    = aws_apigatewayv2_api.this.id
  route_key = "ANY /{proxy+}"
  target    = "integrations/${aws_apigatewayv2_integration.lambda.id}"
}

resource "aws_apigatewayv2_stage" "this" {
  api_id      = aws_apigatewayv2_api.this.id
  name        = "$default"
  auto_deploy = true

  access_log_settings {
    destination_arn = aws_cloudwatch_log_group.api_gateway.arn
    format = jsonencode({
      requestId      = "$context.requestId"
      sourceIp       = "$context.identity.sourceIp"
      requestTime    = "$context.requestTime"
      httpMethod     = "$context.httpMethod"
      path           = "$context.path"
      status         = "$context.status"
      responseLength = "$context.responseLength"
    })
  }

  default_route_settings {
    detailed_metrics_enabled = true
  }

  tags = local.tags
}

resource "aws_lambda_permission" "api_gateway" {
  statement_id  = "AllowApiGatewayInvoke"
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.this.function_name
  principal     = "apigateway.amazonaws.com"
  source_arn    = "${aws_apigatewayv2_api.this.execution_arn}/*/*"
}

resource "null_resource" "enable_postgis" {
  count = var.enable_postgis ? 1 : 0

  triggers = {
    db_endpoint = module.rds.db_instance_address
  }

  provisioner "local-exec" {
    command = <<-EOT
      set -e
      echo "Enabling PostGIS + PostGIS Raster on ${module.rds.db_instance_address}" \
        && PGPASSWORD='${local.db_password}' psql \
          --host=${module.rds.db_instance_address} \
          --username=${var.db_username} \
          --dbname=${var.db_name} \
          --command="CREATE EXTENSION IF NOT EXISTS postgis; CREATE EXTENSION IF NOT EXISTS postgis_raster;"
    EOT
  }

  depends_on = [module.rds]
}
