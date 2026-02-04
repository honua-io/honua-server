# Azure Functions Terraform Service Account

Creates a least-privilege Azure AD service principal and custom role for
Azure Functions style deployments.

## Usage
```bash
terraform init
terraform apply
```

## Notes
- Default scope is the current subscription. You can scope to a resource group by
  setting `scope`.
- The custom role is scoped to Function App resources (App Service, Storage, App Insights).
