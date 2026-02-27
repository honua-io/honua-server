locals {
  name = "${var.name_prefix}-${var.environment}"
  tags = merge({
    Project     = "honua-server"
    Environment = var.environment
    ManagedBy   = "terraform"
  }, var.tags)
}

resource "azurerm_resource_group" "this" {
  name     = "${local.name}-aks-rg"
  location = var.location
  tags     = local.tags
}

resource "azurerm_kubernetes_cluster" "this" {
  name                = "${local.name}-aks"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  dns_prefix          = substr(replace("${local.name}aks", "-", ""), 0, 45)
  sku_tier            = var.sku_tier

  kubernetes_version = var.kubernetes_version != "" ? var.kubernetes_version : null

  default_node_pool {
    name                 = "system"
    vm_size              = var.node_vm_size
    node_count           = var.auto_scaling_enabled ? null : var.node_count
    os_disk_size_gb      = var.node_os_disk_size_gb
    auto_scaling_enabled = var.auto_scaling_enabled
    min_count            = var.auto_scaling_enabled ? var.node_min_count : null
    max_count            = var.auto_scaling_enabled ? var.node_max_count : null
  }

  identity {
    type = "SystemAssigned"
  }

  role_based_access_control_enabled = true
  local_account_disabled            = var.local_account_disabled

  api_server_access_profile {
    authorized_ip_ranges = var.authorized_ip_ranges
  }

  network_profile {
    network_plugin    = var.network_plugin
    network_policy    = var.network_policy
    load_balancer_sku = "standard"
  }

  tags = local.tags
}

resource "azurerm_monitor_diagnostic_setting" "aks" {
  count                      = var.log_analytics_workspace_id != "" ? 1 : 0
  name                       = "${local.name}-aks-diagnostics"
  target_resource_id         = azurerm_kubernetes_cluster.this.id
  log_analytics_workspace_id = var.log_analytics_workspace_id

  enabled_log {
    category = "kube-apiserver"
  }

  enabled_log {
    category = "kube-audit"
  }

  enabled_log {
    category = "kube-controller-manager"
  }

  enabled_log {
    category = "kube-scheduler"
  }
}
