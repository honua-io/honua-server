# Azure AKS Module

Provisions a minimal Azure Kubernetes Service (AKS) cluster for integration validation flows.

## What it provisions

- Resource Group
- AKS cluster with a single system node pool

## Example

```hcl
module "aks" {
  source = "../../modules/azure-aks"

  name_prefix = "honua"
  environment = "it"
  location    = "westus"
}
```

## Outputs

- `resource_group_name`
- `cluster_name`
- `cluster_id`
- `kube_config_raw` (sensitive)
