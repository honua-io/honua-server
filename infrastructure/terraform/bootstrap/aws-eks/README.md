# AWS EKS Terraform Service Account

Creates a least-privilege IAM user and policy for running the
`modules/aws-eks` Terraform module.

## Usage
```bash
terraform init
terraform apply
```

## Notes
- The policy is scoped to AWS services used by the EKS/VPC modules
  (EKS, VPC/EC2, IAM, autoscaling, CloudWatch Logs, KMS).
- Treat the access key as a secret.
