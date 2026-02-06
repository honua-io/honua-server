# AWS ECS/Fargate Module

Provisions Honua Server on ECS/Fargate with an ALB and an RDS PostgreSQL instance.

## Features
- ECS Fargate cluster + service
- ALB with health checks (`/healthz/ready`)
- RDS PostgreSQL instance
- Secrets Manager entries for connection string and admin password

## Usage

```hcl
module "honua" {
  source = "../../modules/aws-ecs"

  environment    = "dev"
  image          = "ghcr.io/honua-io/honua-server:latest"
  admin_password = var.honua_admin_password

  additional_env = {
    HONUA_ADMIN_UI = "true"
  }
}
```

Image tags and registries are documented in `docs/devops/CONTAINER_IMAGES.md`.

## HTTPS
Provide an ACM certificate for the HTTPS listener:

```hcl
alb_certificate_arn = "arn:aws:acm:us-east-1:123456789012:certificate/..."
```

HTTP redirect is enabled by default when HTTPS is enabled. Port 80 inherits the HTTPS CIDRs unless `allow_http_ingress_cidrs` is set; disable redirects with `alb_enable_http_redirect = false`.

### ACM-managed certificate (Route53)
If you own a Route53 zone, you can have the module provision an ACM certificate and DNS validation records:

```hcl
domain_name      = "api.example.com"
route53_zone_id  = "Z1234567890ABC"
```

If no certificate is provided, the module falls back to HTTP for out-of-the-box access.

## PostGIS
RDS requires enabling PostGIS manually or via the optional local-exec:

```hcl
enable_postgis = true
```

This requires `psql` and network access to the RDS endpoint from the machine running `terraform apply`.

## Outputs
See `outputs.tf` for ALB URL, secrets, and database endpoint.
