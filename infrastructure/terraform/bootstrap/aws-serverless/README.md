# AWS Serverless Terraform Service Account

Creates a least-privilege IAM user and policy for Lambda/API Gateway style deployments.

## Usage
```bash
terraform init
terraform apply
```

## Notes
- This is a baseline for Lambda + API Gateway + container image deployments.
- Includes Postgres (RDS) and Redis permissions for serverless Honua stacks.
- If you are not using ECR or S3, remove those permissions.
- Treat the access key as a secret.
