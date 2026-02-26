# AWS EKS Module

Provisions a minimal Amazon EKS cluster and VPC for integration validation flows.

## What it provisions

- VPC with public/private subnets and NAT gateway
- EKS control plane
- EKS managed node group

## Example

```hcl
module "eks" {
  source = "../../modules/aws-eks"

  name_prefix = "honua"
  environment = "it"
}
```

## Outputs

- `cluster_name`
- `cluster_arn`
- `cluster_endpoint`
- `cluster_certificate_authority_data`
- `vpc_id`
