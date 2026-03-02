data "aws_availability_zones" "available" {
  state = "available"
}

locals {
  name = "${var.name_prefix}-${var.environment}"
  tags = merge({
    Project     = "honua-server"
    Environment = var.environment
    ManagedBy   = "terraform"
  }, var.tags)

  db_password          = var.db_password != null ? var.db_password : random_password.db[0].result
  db_ssl               = var.db_require_ssl ? ";SSL Mode=Require;Trust Server Certificate=false" : ""
  db_endpoint          = module.rds.db_instance_address
  db_connection_string = "Host=${local.db_endpoint};Port=5432;Database=${var.db_name};Username=${var.db_username};Password=${local.db_password}${local.db_ssl}"
  redis_auth_token     = var.redis_auth_token != "" ? var.redis_auth_token : (var.redis_enabled ? random_password.redis_auth[0].result : "")
  redis_connection     = var.redis_enabled ? "${aws_elasticache_replication_group.redis[0].primary_endpoint_address}:${var.redis_port},password=${local.redis_auth_token},ssl=true" : ""
}

resource "random_password" "db" {
  count            = var.db_password == null ? 1 : 0
  length           = 32
  special          = true
  override_special = "#%*()-_=+[]{}:?."
}

resource "random_password" "redis_auth" {
  count   = var.redis_enabled && var.redis_auth_token == "" ? 1 : 0
  length  = 32
  special = false
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
  single_nat_gateway             = var.single_nat_gateway
  enable_dns_support             = true
  enable_dns_hostnames           = true
  manage_default_security_group  = true
  default_security_group_ingress = []
  default_security_group_egress  = []

  tags = local.tags
}

locals {
  db_subnet_ids = var.db_publicly_accessible ? module.vpc.public_subnets : module.vpc.private_subnets
}

resource "aws_security_group" "rds" {
  name_prefix = "${local.name}-rds-"
  description = "RDS security group"
  vpc_id      = module.vpc.vpc_id

  ingress {
    description = "PostgreSQL from VPC"
    from_port   = 5432
    to_port     = 5432
    protocol    = "tcp"
    cidr_blocks = [module.vpc.vpc_cidr_block]
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
  count       = var.redis_enabled ? 1 : 0
  name_prefix = "${local.name}-redis-"
  description = "Redis security group"
  vpc_id      = module.vpc.vpc_id

  ingress {
    description = "Redis from VPC"
    from_port   = var.redis_port
    to_port     = var.redis_port
    protocol    = "tcp"
    cidr_blocks = [module.vpc.vpc_cidr_block]
  }

  tags = local.tags
}

resource "aws_elasticache_subnet_group" "redis" {
  count       = var.redis_enabled ? 1 : 0
  name        = "${local.name}-redis"
  subnet_ids  = module.vpc.private_subnets
  description = "Redis subnet group"
  tags        = local.tags
}

resource "aws_elasticache_replication_group" "redis" {
  count                      = var.redis_enabled ? 1 : 0
  replication_group_id       = "${local.name}-redis"
  description                = "Honua Redis data stack"
  node_type                  = var.redis_node_type
  engine                     = "redis"
  engine_version             = var.redis_engine_version
  port                       = var.redis_port
  parameter_group_name       = var.redis_parameter_group_name
  automatic_failover_enabled = var.redis_num_cache_clusters > 1
  multi_az_enabled           = var.redis_num_cache_clusters > 1
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
      condition     = var.redis_num_cache_clusters >= 1
      error_message = "redis_num_cache_clusters must be >= 1."
    }
  }
}

#checkov:skip=CKV_TF_1: Registry modules are version-pinned.
module "rds" {
  #checkov:skip=CKV_TF_1: Registry modules are version-pinned.
  #checkov:skip=CKV_AWS_133: Backup retention is configured in this module call.
  #checkov:skip=CKV_AWS_304: Secret rotation is handled outside this module.
  source  = "terraform-aws-modules/rds/aws"
  version = "~> 6.0"

  identifier = "${local.name}-postgres"

  engine               = "postgres"
  engine_version       = var.db_engine_version
  family               = "postgres${split(".", var.db_engine_version)[0]}"
  major_engine_version = split(".", var.db_engine_version)[0]
  instance_class       = var.db_instance_class

  allocated_storage     = var.db_allocated_storage
  max_allocated_storage = var.db_max_allocated_storage
  storage_encrypted     = true

  db_name                     = var.db_name
  username                    = var.db_username
  password                    = local.db_password
  manage_master_user_password = false
  port                        = 5432

  vpc_security_group_ids = [aws_security_group.rds.id]
  subnet_ids             = local.db_subnet_ids
  create_db_subnet_group = true

  publicly_accessible = var.db_publicly_accessible
  multi_az            = var.db_multi_az

  backup_retention_period = var.environment == "prod" ? 7 : 3
  maintenance_window      = "Sun:04:00-Sun:05:00"

  tags = local.tags
}

resource "null_resource" "enable_postgis" {
  count = var.enable_postgis ? 1 : 0

  triggers = {
    db_endpoint = local.db_endpoint
  }

  provisioner "local-exec" {
    command = <<-EOT
      set -e
      echo "Waiting for PostgreSQL readiness on ${local.db_endpoint}"
      for attempt in $(seq 1 ${var.postgis_readiness_max_attempts}); do
        if PGCONNECT_TIMEOUT=5 psql \
          --host=${local.db_endpoint} \
          --username=${var.db_username} \
          --dbname=${var.db_name} \
          --command="SELECT 1;" >/dev/null 2>&1; then
          echo "PostgreSQL readiness check succeeded after $attempt attempt(s)"
          break
        fi
        if [ "$attempt" -eq ${var.postgis_readiness_max_attempts} ]; then
          echo "PostgreSQL readiness check failed after ${var.postgis_readiness_max_attempts} attempts" >&2
          exit 1
        fi
        sleep ${var.postgis_readiness_sleep_seconds}
      done

      echo "Enabling PostGIS + PostGIS Raster on ${local.db_endpoint}"
      PGCONNECT_TIMEOUT=5 psql \
        --host=${local.db_endpoint} \
        --username=${var.db_username} \
        --dbname=${var.db_name} \
        --set=ON_ERROR_STOP=1 \
        --command="CREATE EXTENSION IF NOT EXISTS postgis; CREATE EXTENSION IF NOT EXISTS postgis_raster;"
    EOT
    environment = {
      PGPASSWORD = local.db_password
    }
  }

  depends_on = [module.rds]
}
