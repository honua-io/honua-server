# AWS ECS/Fargate Module

Provisions Honua Server on ECS/Fargate with an ALB, RDS PostgreSQL, optional ElastiCache Redis, and supporting infrastructure (VPC, secrets, logging).

## Quick start (dev)

```hcl
module "honua" {
  source = "../../modules/aws-ecs"

  environment    = "dev"
  image          = "ghcr.io/honua-io/honua-server:latest-aot"
  admin_password = var.honua_admin_password
  enable_postgis = true  # Required — Honua needs PostGIS for migrations

  additional_env = {
    HONUA_ADMIN_UI = "true"
  }
}
```

> **PostGIS is required.** Set `enable_postgis = true` to enable the PostGIS extension on the RDS instance via a local-exec provisioner. This requires `psql` on the machine running `terraform apply` and network access to the RDS endpoint. If you cannot run local-exec, enable PostGIS manually after apply.

## Production example

```hcl
module "honua" {
  source = "../../modules/aws-ecs"

  environment = "prod"
  name_prefix = "honua"

  # Container
  image            = "ghcr.io/honua-io/honua-server:v1.2.3-aot"  # Pin to a release AOT tag
  container_cpu    = 1024   # 1 vCPU
  container_memory = 2048   # 2 GB
  desired_count    = 2      # Minimum 2 for HA

  # Database
  admin_password       = var.honua_admin_password
  db_instance_class    = "db.r6g.large"    # Production-grade instance
  db_allocated_storage = 100               # GB
  db_multi_az          = true              # Failover replica
  db_require_ssl       = true
  enable_postgis       = true

  # Redis (multi-node caching)
  redis_enabled            = true
  redis_node_type          = "cache.r6g.large"
  redis_num_cache_clusters = 2

  # Networking
  vpc_cidr             = "10.0.0.0/16"
  enable_nat_gateway   = true
  assign_public_ip     = false

  # HTTPS
  alb_certificate_arn     = var.acm_certificate_arn
  alb_deletion_protection = true

  # Logging and monitoring
  log_retention_days         = 365
  enable_container_insights  = true
  alb_access_logs_enabled    = true

  # Security
  waf_web_acl_arn = var.waf_acl_arn  # Optional WAFv2

  additional_env = {
    HONUA_ADMIN_UI       = "true"
    HONUA_OBSERVABILITY  = "true"
    HONUA_OPENTELEMETRY  = "true"
    Public__BaseUrl      = "https://gis.example.com"
  }

  tags = {
    Project     = "honua"
    Environment = "prod"
  }
}
```

## HTTPS

Provide an ACM certificate for the HTTPS listener:

```hcl
alb_certificate_arn = "arn:aws:acm:us-east-1:123456789012:certificate/..."
```

HTTP-to-HTTPS redirect is enabled by default when a certificate is provided. Disable with `alb_enable_http_redirect = false`.

### ACM with Route 53 (auto-provisioned)

If you own a Route 53 zone, the module can create and validate the certificate for you:

```hcl
domain_name     = "gis.example.com"
route53_zone_id = "Z1234567890ABC"
```

## Key variables

| Variable | Default | Description |
|----------|---------|-------------|
| `image` | `ghcr.io/.../latest-aot` | Container image. AOT recommended. Pin to `vX.Y.Z-aot` for production. |
| `container_cpu` | 512 | Fargate CPU units (256/512/1024/2048/4096). |
| `container_memory` | 1024 | Fargate memory in MiB. |
| `desired_count` | 1 | Number of tasks. Use 2+ for production. |
| `enable_postgis` | **false** | Enable PostGIS extension on RDS. **Set to true.** |
| `db_instance_class` | `db.t3.micro` | RDS instance class. Use `db.r6g.*` for production. |
| `db_multi_az` | false | Enable Multi-AZ failover. Recommended for production. |
| `db_require_ssl` | true | Append SSL requirements to the connection string. |
| `redis_enabled` | true | Provision ElastiCache Redis. |
| `redis_node_type` | `cache.t3.micro` | ElastiCache node type. |
| `alb_certificate_arn` | `""` | ACM certificate ARN. Falls back to HTTP if empty. |
| `waf_web_acl_arn` | `""` | WAFv2 Web ACL ARN for the ALB. |
| `enable_nat_gateway` | true | NAT gateways for private subnets (required for outbound). |
| `log_retention_days` | 365 | CloudWatch log retention. |
| `kms_key_arn` | `""` | Existing KMS key for logs/secrets. Creates one if empty. |

See `variables.tf` for the complete list.

## Outputs

See `outputs.tf` for ALB URL, RDS endpoint, secrets ARNs, and connection strings.

## After apply

1. Verify PostGIS: `psql $CONNECTION_STRING -c "SELECT PostGIS_Version();"`
2. Health check: `curl -f https://<alb-url>/healthz/ready`
3. If using OIDC, configure env vars per [Security Configuration](../../../../docs/devops/SECURITY_CONFIGURATION.md)
