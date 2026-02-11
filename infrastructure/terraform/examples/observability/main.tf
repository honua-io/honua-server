provider "kubernetes" {
  config_path = var.kubeconfig_path
}

provider "helm" {
  kubernetes = {
    config_path = var.kubeconfig_path
  }
}

module "observability" {
  source = "../../modules/observability-stack"

  namespace            = var.namespace
  honua_metrics_target = var.honua_metrics_target

  grafana_ingress_enabled = var.grafana_ingress_host != ""
  grafana_ingress_host    = var.grafana_ingress_host
}

output "prometheus_url" {
  value = module.observability.prometheus_url
}

output "grafana_url" {
  value = module.observability.grafana_url
}

output "grafana_admin_secret_name" {
  value = module.observability.grafana_admin_secret_name
}
