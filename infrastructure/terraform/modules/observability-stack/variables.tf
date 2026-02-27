variable "namespace" {
  description = "Namespace for Prometheus and Grafana."
  type        = string
  default     = "honua-observability"
}

variable "create_namespace" {
  description = "Create the namespace when it does not exist."
  type        = bool
  default     = true
}

variable "prometheus_release_name" {
  description = "Helm release name for Prometheus."
  type        = string
  default     = "honua-prometheus"
}

variable "grafana_release_name" {
  description = "Helm release name for Grafana."
  type        = string
  default     = "honua-grafana"
}

variable "prometheus_chart_version" {
  description = "Prometheus chart version."
  type        = string
  default     = "27.34.0"
}

variable "grafana_chart_version" {
  description = "Grafana chart version."
  type        = string
  default     = "8.13.1"
}

variable "honua_metrics_target" {
  description = "Scrape target for Honua metrics (host:port)."
  type        = string
}

variable "honua_metrics_path" {
  description = "Metrics path exposed by Honua."
  type        = string
  default     = "/metrics"
}

variable "honua_metrics_format" {
  description = "Optional format query param for Honua metrics endpoint."
  type        = string
  default     = ""
}

variable "scrape_interval" {
  description = "Prometheus scrape interval."
  type        = string
  default     = "15s"
}

variable "evaluation_interval" {
  description = "Prometheus rule evaluation interval."
  type        = string
  default     = "30s"
}

variable "alert_rules_file" {
  description = "Path to a Prometheus rules YAML file. Leave empty to use the module default."
  type        = string
  default     = ""
}

variable "honua_dashboard_file" {
  description = "Path to the Honua Grafana dashboard JSON. Leave empty to use the module default."
  type        = string
  default     = ""
}

variable "prometheus_persistence_enabled" {
  description = "Enable Prometheus persistence."
  type        = bool
  default     = true
}

variable "prometheus_persistence_size" {
  description = "Prometheus PVC size."
  type        = string
  default     = "20Gi"
}

variable "grafana_persistence_enabled" {
  description = "Enable Grafana persistence."
  type        = bool
  default     = true
}

variable "grafana_persistence_size" {
  description = "Grafana PVC size."
  type        = string
  default     = "10Gi"
}

variable "grafana_ingress_enabled" {
  description = "Expose Grafana with ingress."
  type        = bool
  default     = false
}

variable "grafana_ingress_host" {
  description = "Ingress hostname for Grafana when ingress is enabled."
  type        = string
  default     = ""
}

variable "grafana_ingress_class_name" {
  description = "Optional ingress class for Grafana."
  type        = string
  default     = ""
}

variable "grafana_ingress_annotations" {
  description = "Ingress annotations for Grafana."
  type        = map(string)
  default     = {}
}

variable "grafana_admin_user" {
  description = "Grafana admin username."
  type        = string
  default     = "admin"
}

variable "prometheus_retention" {
  description = "How long Prometheus retains time series data."
  type        = string
  default     = "15d"
}

variable "prometheus_retention_size" {
  description = "Maximum size of Prometheus TSDB before oldest data is dropped."
  type        = string
  default     = "18GB"
}

variable "grafana_ingress_tls_secret" {
  description = "Kubernetes TLS secret name for Grafana ingress. Empty to disable TLS."
  type        = string
  default     = ""
}
