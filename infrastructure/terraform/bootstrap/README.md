# Terraform Bootstrap Service Accounts

This directory contains least-privilege **service account templates** for running Honua's
Terraform modules. Separate templates are provided for ECS/Fargate vs serverless runtimes, and
for AWS vs Azure.

## Templates
- `aws-ecs` — IAM user + policy for the ECS/Fargate + RDS + ALB module.
- `aws-serverless` — IAM user + policy for Lambda/API Gateway style deployments.
- `azure-aca` — Azure AD service principal + custom role for Azure Container Apps.
- `azure-functions` — Azure AD service principal + custom role for Azure Functions.

> These are least-privilege *starting points* scoped to the services used by each template.
> If you disable optional features (WAF, Route53, ACM, etc.) you can remove the related
> permissions. If you add new components, expand the policy accordingly.

## Usage
Each template is a standalone Terraform project. Example:

```bash
cd infrastructure/terraform/bootstrap/aws-ecs
terraform init
terraform apply
```

Each template outputs the credentials (or client secret) needed by your CI or local Terraform
runs. Treat these as secrets.
