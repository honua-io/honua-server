# AWS ECS/Fargate Terraform Service Account

Creates a least-privilege IAM user and policy for running the `modules/aws-ecs` Terraform module.

## Usage
```bash
terraform init
terraform apply
```

## Notes
- The policy is scoped to the AWS services used by the ECS/Fargate module (VPC, ECS, ALB, RDS,
  CloudWatch Logs, Secrets Manager, KMS, S3, ACM, Route53, WAF).
- If you disable optional features (WAF, Route53, ACM, ALB access logs), you can remove those
  permissions from `main.tf`.
- Treat the access key as a secret.
