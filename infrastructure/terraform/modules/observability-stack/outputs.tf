output "prometheus_release" {
  description = "Prometheus Helm release name."
  value       = helm_release.prometheus.name
}

output "grafana_release" {
  description = "Grafana Helm release name."
  value       = helm_release.grafana.name
}

output "prometheus_url" {
  description = "In-cluster Prometheus URL."
  value       = "http://${var.prometheus_release_name}-server.${var.namespace}.svc.cluster.local"
}

output "grafana_url" {
  description = "URL for accessing the Grafana dashboard."
  value       = var.grafana_ingress_enabled && var.grafana_ingress_host != "" ? "${var.grafana_ingress_tls_secret != "" ? "https" : "http"}://${var.grafana_ingress_host}" : "kubectl port-forward svc/grafana 3000:80 -n ${var.namespace}"
}

output "grafana_admin_secret_name" {
  description = "Kubernetes secret containing Grafana admin credentials."
  value       = kubernetes_secret_v1.grafana_admin.metadata[0].name
}

output "grafana_admin_secret_keys" {
  description = "Secret data keys for Grafana admin credentials."
  value = {
    username = "admin-user"
    password = "admin-pass"
  }
}

output "dashboard_configmap_name" {
  description = "ConfigMap that provisions the Honua Grafana dashboard."
  value       = kubernetes_config_map_v1.honua_dashboard.metadata[0].name
}
