# Azure AKS Terraform Service Account

Creates a least-privilege Azure AD service principal and custom role for the
`modules/azure-aks` Terraform module.

## Usage
```bash
terraform init
terraform apply
```

## Notes
- Default scope is the current subscription. You can scope to a resource group by
  setting `scope`.
- The custom role is scoped to AKS resources plus required networking and
  resource group permissions.
