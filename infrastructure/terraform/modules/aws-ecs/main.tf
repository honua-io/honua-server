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
  db_use_existing     = var.existing_db_endpoint != "" && var.existing_db_connection_string != ""
  use_managed_cert    = var.domain_name != "" && var.route53_zone_id != ""
  use_https           = var.alb_certificate_arn != "" || local.use_managed_cert
  https_ingress_cidrs = length(var.allow_public_ingress_cidrs) > 0 ? var.allow_public_ingress_cidrs : var.allow_https_ingress_cidrs
  http_ingress_base   = length(var.allow_http_ingress_cidrs) > 0 ? var.allow_http_ingress_cidrs : local.https_ingress_cidrs
  http_ingress_cidrs  = local.use_https ? (var.alb_enable_http_redirect ? local.http_ingress_base : []) : local.http_ingress_base
  redis_enabled       = var.redis_enabled || var.redis_connection_string != ""
  redis_create        = var.redis_enabled && var.redis_connection_string == ""
  redis_auth_token    = var.redis_auth_token != "" ? var.redis_auth_token : (local.redis_create ? random_password.redis_auth[0].result : "")
  redis_connection    = var.redis_connection_string != "" ? var.redis_connection_string : (local.redis_create ? "${aws_elasticache_replication_group.redis[0].primary_endpoint_address}:${var.redis_port},password=${local.redis_auth_token},ssl=true" : "")
}

check "existing_db_inputs" {
  assert {
    condition = (
      (var.existing_db_endpoint == "" && var.existing_db_connection_string == "") ||
      (var.existing_db_endpoint != "" && var.existing_db_connection_string != "")
    )
    error_message = "existing_db_endpoint and existing_db_connection_string must both be set or both be empty."
  }
}

#checkov:skip=CKV_TF_1: Registry modules are version-pinned.
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

resource "aws_security_group" "alb" {
  #checkov:skip=CKV2_AWS_5: Security group is attached to the ALB.
  #checkov:skip=CKV_AWS_260: HTTP ingress is optional and disabled by default.
  name_prefix = "${local.name}-alb-"
  description = "ALB security group"
  vpc_id      = module.vpc.vpc_id

  dynamic "ingress" {
    for_each = length(local.http_ingress_cidrs) > 0 ? [1] : []
    content {
      description = "HTTP ingress"
      from_port   = 80
      to_port     = 80
      protocol    = "tcp"
      cidr_blocks = local.http_ingress_cidrs
    }
  }

  dynamic "ingress" {
    for_each = local.use_https ? [1] : []
    content {
      description = "HTTPS ingress"
      from_port   = 443
      to_port     = 443
      protocol    = "tcp"
      cidr_blocks = local.https_ingress_cidrs
    }
  }

  egress {
    description = "ALB to ECS targets"
    from_port   = var.container_port
    to_port     = var.container_port
    protocol    = "tcp"
    cidr_blocks = [module.vpc.vpc_cidr_block]
  }

  tags = local.tags
}

#checkov:skip=CKV2_AWS_5: Security group is attached to the ECS service.
resource "aws_security_group" "ecs" {
  #checkov:skip=CKV2_AWS_5: Security group is attached to the ECS service.
  name_prefix = "${local.name}-ecs-"
  description = "ECS service security group"
  vpc_id      = module.vpc.vpc_id

  ingress {
    description     = "ALB ingress"
    from_port       = var.container_port
    to_port         = var.container_port
    protocol        = "tcp"
    security_groups = [aws_security_group.alb.id]
  }

  egress {
    description = "Database access"
    from_port   = 5432
    to_port     = 5432
    protocol    = "tcp"
    cidr_blocks = local.db_use_existing ? ["0.0.0.0/0"] : [module.vpc.vpc_cidr_block]
  }

  egress {
    description = "Outbound HTTPS"
    from_port   = 443
    to_port     = 443
    protocol    = "tcp"
    cidr_blocks = ["0.0.0.0/0"]
  }

  dynamic "egress" {
    for_each = local.redis_enabled ? [1] : []
    content {
      description = "Redis access"
      from_port   = var.redis_port
      to_port     = var.redis_port
      protocol    = "tcp"
      cidr_blocks = [module.vpc.vpc_cidr_block]
    }
  }

  tags = local.tags
}

#checkov:skip=CKV2_AWS_5: Security group is attached to the RDS instance.
resource "aws_security_group" "rds" {
  count = local.db_use_existing ? 0 : 1
  #checkov:skip=CKV2_AWS_5: Security group is attached via the RDS module.
  name_prefix = "${local.name}-rds-"
  description = "RDS security group"
  vpc_id      = module.vpc.vpc_id

  ingress {
    description     = "PostgreSQL from ECS"
    from_port       = 5432
    to_port         = 5432
    protocol        = "tcp"
    security_groups = [aws_security_group.ecs.id]
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
    description     = "Redis from ECS"
    from_port       = var.redis_port
    to_port         = var.redis_port
    protocol        = "tcp"
    security_groups = [aws_security_group.ecs.id]
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
  automatic_failover_enabled = true
  multi_az_enabled           = true
  num_cache_clusters         = var.redis_num_cache_clusters
  subnet_group_name          = aws_elasticache_subnet_group.redis[0].name
  security_group_ids         = [aws_security_group.redis[0].id]
  at_rest_encryption_enabled = true
  transit_encryption_enabled = true
  auth_token                 = local.redis_auth_token
  kms_key_id                 = local.kms_key_arn
  apply_immediately          = true
  tags                       = local.tags

  lifecycle {
    precondition {
      condition     = var.redis_num_cache_clusters >= 2
      error_message = "redis_num_cache_clusters must be >= 2 when provisioning Redis with multi-AZ failover."
    }
  }
}

#checkov:skip=CKV2_AWS_28: WAF association is optional via waf_web_acl_arn.
resource "aws_lb" "this" {
  #checkov:skip=CKV2_AWS_76: WAF AMR configuration is managed via waf_web_acl_arn association.
  #checkov:skip=CKV2_AWS_20: HTTP redirect is conditional based on certificate availability.
  name                       = "${local.name}-alb"
  load_balancer_type         = "application"
  internal                   = false
  security_groups            = [aws_security_group.alb.id]
  subnets                    = module.vpc.public_subnets
  enable_deletion_protection = var.alb_deletion_protection
  drop_invalid_header_fields = var.alb_drop_invalid_headers

  access_logs {
    enabled = var.alb_access_logs_enabled
    bucket  = local.alb_logs_bucket_name
    prefix  = var.alb_access_logs_prefix
  }

  tags = local.tags
}

resource "aws_wafv2_web_acl_association" "this" {
  count        = var.waf_web_acl_arn != "" ? 1 : 0
  resource_arn = aws_lb.this.arn
  web_acl_arn  = var.waf_web_acl_arn
}

resource "aws_acm_certificate" "this" {
  count                     = local.use_managed_cert ? 1 : 0
  domain_name               = var.domain_name
  subject_alternative_names = var.subject_alternative_names
  validation_method         = "DNS"
  tags                      = local.tags

  lifecycle {
    create_before_destroy = true
  }
}

resource "aws_route53_record" "cert_validation" {
  for_each = local.use_managed_cert ? {
    for dvo in aws_acm_certificate.this[0].domain_validation_options : dvo.domain_name => {
      name   = dvo.resource_record_name
      record = dvo.resource_record_value
      type   = dvo.resource_record_type
    }
  } : {}

  zone_id = var.route53_zone_id
  name    = each.value.name
  type    = each.value.type
  records = [each.value.record]
  ttl     = 60
}

resource "aws_acm_certificate_validation" "this" {
  count                   = local.use_managed_cert ? 1 : 0
  certificate_arn         = aws_acm_certificate.this[0].arn
  validation_record_fqdns = [for record in aws_route53_record.cert_validation : record.fqdn]
}

resource "aws_s3_bucket" "alb_logs" {
  #checkov:skip=CKV_AWS_18: Access log bucket doesn't require its own access logs.
  #checkov:skip=CKV2_AWS_62: Event notifications are optional for log buckets.
  #checkov:skip=CKV2_AWS_61: Lifecycle policies are optional for log buckets.
  #checkov:skip=CKV_AWS_144: Cross-region replication is optional for log buckets.
  #checkov:skip=CKV_AWS_145: Encryption enforced via separate configuration resource.
  #checkov:skip=CKV_AWS_21: Versioning enforced via separate configuration resource.
  #checkov:skip=CKV2_AWS_6: Public access block enforced via separate resource.
  count         = var.alb_access_logs_enabled && var.alb_access_logs_bucket_name == "" ? 1 : 0
  bucket        = local.alb_logs_bucket_name
  force_destroy = var.alb_access_logs_force_destroy
  tags          = local.tags
}

resource "aws_s3_bucket_public_access_block" "alb_logs" {
  count                   = var.alb_access_logs_enabled && var.alb_access_logs_bucket_name == "" ? 1 : 0
  bucket                  = aws_s3_bucket.alb_logs[0].id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_s3_bucket_versioning" "alb_logs" {
  count  = var.alb_access_logs_enabled && var.alb_access_logs_bucket_name == "" ? 1 : 0
  bucket = aws_s3_bucket.alb_logs[0].id
  versioning_configuration {
    status = "Enabled"
  }
}

resource "aws_s3_bucket_server_side_encryption_configuration" "alb_logs" {
  count  = var.alb_access_logs_enabled && var.alb_access_logs_bucket_name == "" ? 1 : 0
  bucket = aws_s3_bucket.alb_logs[0].id

  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm     = "aws:kms"
      kms_master_key_id = local.kms_key_arn
    }
  }
}

resource "aws_s3_bucket_ownership_controls" "alb_logs" {
  count  = var.alb_access_logs_enabled && var.alb_access_logs_bucket_name == "" ? 1 : 0
  bucket = aws_s3_bucket.alb_logs[0].id

  rule {
    object_ownership = "BucketOwnerEnforced"
  }
}

resource "aws_s3_bucket_policy" "alb_logs" {
  count  = var.alb_access_logs_enabled && var.alb_access_logs_bucket_name == "" ? 1 : 0
  bucket = aws_s3_bucket.alb_logs[0].id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Effect = "Allow"
        Principal = {
          Service = "logdelivery.elasticloadbalancing.amazonaws.com"
        }
        Action   = "s3:PutObject"
        Resource = "${aws_s3_bucket.alb_logs[0].arn}/AWSLogs/${data.aws_caller_identity.current.account_id}/*"
      },
      {
        Effect = "Allow"
        Principal = {
          Service = "logdelivery.elasticloadbalancing.amazonaws.com"
        }
        Action   = "s3:GetBucketAcl"
        Resource = aws_s3_bucket.alb_logs[0].arn
      }
    ]
  })
}
#checkov:skip=CKV_AWS_378: Target group uses HTTP for in-VPC traffic.
resource "aws_lb_target_group" "this" {
  #checkov:skip=CKV_AWS_378: Target group uses HTTP for in-VPC traffic.
  name        = "${local.name}-tg"
  port        = var.container_port
  protocol    = "HTTP"
  vpc_id      = module.vpc.vpc_id
  target_type = "ip"

  health_check {
    path                = var.health_check_path
    interval            = 30
    timeout             = 5
    healthy_threshold   = 2
    unhealthy_threshold = 3
    matcher             = "200-399"
  }

  tags = local.tags
}

resource "aws_lb_listener" "https" {
  count             = local.use_https ? 1 : 0
  load_balancer_arn = aws_lb.this.arn
  port              = 443
  protocol          = "HTTPS"
  ssl_policy        = "ELBSecurityPolicy-TLS13-1-2-2021-06"
  certificate_arn   = local.certificate_arn

  default_action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.this.arn
  }
}

resource "aws_lb_listener" "http_redirect" {
  #checkov:skip=CKV_AWS_2: HTTP listener is used for redirect to HTTPS.
  #checkov:skip=CKV_AWS_103: HTTP listener is required for redirect when HTTPS is enabled.
  count             = local.use_https && var.alb_enable_http_redirect && length(local.http_ingress_cidrs) > 0 ? 1 : 0
  load_balancer_arn = aws_lb.this.arn
  port              = 80
  protocol          = "HTTP"

  default_action {
    type = "redirect"

    redirect {
      port        = "443"
      protocol    = "HTTPS"
      status_code = "HTTP_301"
    }
  }
}

resource "aws_lb_listener" "http" {
  #checkov:skip=CKV_AWS_2: HTTP listener is used when no HTTPS certificate is configured.
  #checkov:skip=CKV_AWS_103: HTTP listener is required when no HTTPS certificate is configured.
  count             = local.use_https ? 0 : (length(local.http_ingress_cidrs) > 0 ? 1 : 0)
  load_balancer_arn = aws_lb.this.arn
  port              = 80
  protocol          = "HTTP"

  default_action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.this.arn
  }
}

resource "aws_ecs_cluster" "this" {
  name = "${local.name}-cluster"

  setting {
    name  = "containerInsights"
    value = var.enable_container_insights ? "enabled" : "disabled"
  }

  tags = local.tags
}

resource "aws_cloudwatch_log_group" "this" {
  name              = "/honua/${local.name}"
  retention_in_days = var.log_retention_days
  kms_key_id        = local.kms_key_arn
  tags              = local.tags
}

resource "aws_iam_role" "task_execution" {
  name               = "${local.name}-ecs-exec"
  assume_role_policy = data.aws_iam_policy_document.ecs_task_assume.json
  tags               = local.tags
}

resource "aws_iam_role_policy_attachment" "task_execution" {
  role       = aws_iam_role.task_execution.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy"
}

resource "aws_iam_role" "task" {
  name               = "${local.name}-ecs-task"
  assume_role_policy = data.aws_iam_policy_document.ecs_task_assume.json
  tags               = local.tags
}

resource "aws_iam_policy" "secrets" {
  name        = "${local.name}-secrets"
  description = "Allow ECS task to read Honua secrets"

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Effect = "Allow"
        Action = ["secretsmanager:GetSecretValue", "secretsmanager:DescribeSecret"]
        Resource = compact([
          aws_secretsmanager_secret.db_connection.arn,
          aws_secretsmanager_secret.admin_password.arn,
          local.redis_connection != "" ? aws_secretsmanager_secret.redis_connection[0].arn : null
        ])
      }
    ]
  })
}

resource "aws_iam_role_policy_attachment" "task_secrets" {
  role       = aws_iam_role.task.name
  policy_arn = aws_iam_policy.secrets.arn
}

resource "random_password" "db" {
  count            = var.db_password == null && !local.db_use_existing ? 1 : 0
  length           = 32
  special          = true
  override_special = "#%*()-_=+[]{}:?."
}

resource "random_password" "redis_auth" {
  count   = local.redis_create && var.redis_auth_token == "" ? 1 : 0
  length  = 32
  special = false
}

resource "random_id" "alb_logs_suffix" {
  byte_length = 4
}

data "aws_iam_policy_document" "kms" {
  #checkov:skip=CKV_AWS_111: Root access is required for KMS administration.
  #checkov:skip=CKV_AWS_356: Root access is required for KMS administration.
  #checkov:skip=CKV_AWS_109: Root access is required for KMS administration.
  statement {
    actions   = ["kms:*"]
    resources = ["*"]
    principals {
      type        = "AWS"
      identifiers = ["arn:aws:iam::${data.aws_caller_identity.current.account_id}:root"]
    }
  }
}

resource "aws_kms_key" "honua" {
  count                   = var.kms_key_arn == "" ? 1 : 0
  description             = "Honua infrastructure key"
  deletion_window_in_days = var.kms_key_deletion_window_days
  enable_key_rotation     = true
  policy                  = data.aws_iam_policy_document.kms.json
  tags                    = local.tags
}

resource "aws_kms_alias" "honua" {
  count         = var.kms_key_arn == "" ? 1 : 0
  name          = "alias/${local.name}-honua"
  target_key_id = aws_kms_key.honua[0].key_id
}

locals {
  db_password          = var.db_password != null ? var.db_password : (local.db_use_existing ? "" : random_password.db[0].result)
  db_ssl               = var.db_require_ssl ? ";SSL Mode=Require;Trust Server Certificate=false" : ""
  db_endpoint          = local.db_use_existing ? var.existing_db_endpoint : module.rds[0].db_instance_address
  db_connection_string = local.db_use_existing ? var.existing_db_connection_string : "Host=${local.db_endpoint};Port=5432;Database=${var.db_name};Username=${var.db_username};Password=${local.db_password}${local.db_ssl}"
  kms_key_arn          = var.kms_key_arn != "" ? var.kms_key_arn : aws_kms_key.honua[0].arn
  alb_logs_bucket_name = var.alb_access_logs_bucket_name != "" ? var.alb_access_logs_bucket_name : "${local.name}-alb-logs-${random_id.alb_logs_suffix.hex}"
  certificate_arn      = var.alb_certificate_arn != "" ? var.alb_certificate_arn : (local.use_managed_cert ? aws_acm_certificate_validation.this[0].certificate_arn : "")
}

#checkov:skip=CKV_TF_1: Registry modules are version-pinned.
#checkov:skip=CKV_TF_1: Registry modules are version-pinned.
#checkov:skip=CKV_AWS_133: Backup retention is configured in this module call.
#checkov:skip=CKV_AWS_304: Secret rotation is handled outside this module.
module "rds" {
  count = local.db_use_existing ? 0 : 1
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

  vpc_security_group_ids = local.db_use_existing ? [] : [aws_security_group.rds[0].id]
  subnet_ids             = module.vpc.private_subnets

  publicly_accessible = var.db_publicly_accessible
  multi_az            = var.db_multi_az

  backup_retention_period = var.environment == "prod" ? 7 : 3
  maintenance_window      = "Sun:04:00-Sun:05:00"

  tags = local.tags
}

#checkov:skip=CKV2_AWS_57: Secrets rotation is handled outside the module.
resource "aws_secretsmanager_secret" "db_connection" {
  #checkov:skip=CKV2_AWS_57: Secrets rotation is handled outside the module.
  name_prefix = "${local.name}-db-"
  description = "Honua database connection string"
  kms_key_id  = local.kms_key_arn
  tags        = local.tags
}

resource "aws_secretsmanager_secret_version" "db_connection" {
  secret_id     = aws_secretsmanager_secret.db_connection.id
  secret_string = local.db_connection_string
}

#checkov:skip=CKV2_AWS_57: Secrets rotation is handled outside the module.
resource "aws_secretsmanager_secret" "admin_password" {
  #checkov:skip=CKV2_AWS_57: Secrets rotation is handled outside the module.
  name_prefix = "${local.name}-admin-"
  description = "Honua admin API password"
  kms_key_id  = local.kms_key_arn
  tags        = local.tags
}

resource "aws_secretsmanager_secret_version" "admin_password" {
  secret_id     = aws_secretsmanager_secret.admin_password.id
  secret_string = var.admin_password
}

#checkov:skip=CKV2_AWS_57: Secrets rotation is handled outside the module.
resource "aws_secretsmanager_secret" "redis_connection" {
  #checkov:skip=CKV2_AWS_57: Secrets rotation is handled outside the module.
  count       = local.redis_connection != "" ? 1 : 0
  name_prefix = "${local.name}-redis-"
  description = "Honua Redis connection string"
  kms_key_id  = local.kms_key_arn
  tags        = local.tags
}

resource "aws_secretsmanager_secret_version" "redis_connection" {
  count         = local.redis_connection != "" ? 1 : 0
  secret_id     = aws_secretsmanager_secret.redis_connection[0].id
  secret_string = local.redis_connection
}

resource "aws_ecs_task_definition" "this" {
  family                   = "${local.name}-task"
  network_mode             = "awsvpc"
  requires_compatibilities = ["FARGATE"]
  cpu                      = var.container_cpu
  memory                   = var.container_memory
  execution_role_arn       = aws_iam_role.task_execution.arn
  task_role_arn            = aws_iam_role.task.arn

  container_definitions = jsonencode([
    {
      name      = "honua"
      image     = var.image
      essential = true
      portMappings = [
        {
          containerPort = var.container_port
          hostPort      = var.container_port
          protocol      = "tcp"
        }
      ]
      environment = [
        for key, value in var.additional_env : {
          name  = key
          value = value
        }
      ]
      secrets = concat([
        {
          name      = "ConnectionStrings__DefaultConnection"
          valueFrom = aws_secretsmanager_secret.db_connection.arn
        },
        {
          name      = "HONUA_ADMIN_PASSWORD"
          valueFrom = aws_secretsmanager_secret.admin_password.arn
        }
        ], local.redis_connection != "" ? [
        {
          name      = "ConnectionStrings__redis"
          valueFrom = aws_secretsmanager_secret.redis_connection[0].arn
        }
      ] : [])
      logConfiguration = {
        logDriver = "awslogs"
        options = {
          awslogs-group         = aws_cloudwatch_log_group.this.name
          awslogs-region        = data.aws_region.current.id
          awslogs-stream-prefix = "honua"
        }
      }
    }
  ])

  tags = local.tags
}

resource "aws_ecs_service" "this" {
  name            = "${local.name}-service"
  cluster         = aws_ecs_cluster.this.id
  task_definition = aws_ecs_task_definition.this.arn
  desired_count   = var.desired_count
  launch_type     = "FARGATE"

  network_configuration {
    subnets          = module.vpc.private_subnets
    security_groups  = [aws_security_group.ecs.id]
    assign_public_ip = var.assign_public_ip
  }

  load_balancer {
    target_group_arn = aws_lb_target_group.this.arn
    container_name   = "honua"
    container_port   = var.container_port
  }

  depends_on = [aws_lb_listener.https, aws_lb_listener.http, aws_lb_listener.http_redirect]

  tags = local.tags
}

data "aws_iam_policy_document" "ecs_task_assume" {
  statement {
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["ecs-tasks.amazonaws.com"]
    }
  }
}

resource "null_resource" "enable_postgis" {
  count = var.enable_postgis && !local.db_use_existing ? 1 : 0

  triggers = {
    db_endpoint = local.db_endpoint
  }

  provisioner "local-exec" {
    command = <<-EOT
      set -e
      echo "Enabling PostGIS + PostGIS Raster on ${local.db_endpoint}" \
        && PGPASSWORD='${local.db_password}' psql \
          --host=${local.db_endpoint} \
          --username=${var.db_username} \
          --dbname=${var.db_name} \
          --command="CREATE EXTENSION IF NOT EXISTS postgis; CREATE EXTENSION IF NOT EXISTS postgis_raster;"
    EOT
  }

  depends_on = [module.rds]
}
