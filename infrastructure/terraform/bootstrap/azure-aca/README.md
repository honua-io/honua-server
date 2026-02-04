# Azure Container Apps Terraform Service Account

Creates a least-privilege Azure AD service principal and custom role for the
`modules/azure-aca` Terraform module.

## Usage
```bash
terraform init
terraform apply
```

## Notes
- Default scope is the current subscription. You can scope to a resource group by
  setting `scope`.
- The custom role is scoped to the ACA module's resource types (Container Apps,
  Log Analytics, Postgres Flexible Server, Redis, Key Vault, Managed Identity, Resource Groups).
