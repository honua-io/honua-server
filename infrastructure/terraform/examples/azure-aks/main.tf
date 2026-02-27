provider "azurerm" {
  features {}
}

module "aks" {
  source = "../../modules/azure-aks"

  name_prefix          = var.name_prefix
  environment          = var.environment
  location             = var.location
  tags                 = var.tags
  node_count           = var.node_count
  node_vm_size         = var.node_vm_size
  node_os_disk_size_gb = var.node_os_disk_size_gb
  kubernetes_version   = var.kubernetes_version
  sku_tier             = var.sku_tier
}

output "resource_group_name" {
  value = module.aks.resource_group_name
}

output "cluster_name" {
  value = module.aks.cluster_name
}

output "cluster_id" {
  value = module.aks.cluster_id
}
