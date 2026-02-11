variable "kubeconfig_path" {
  description = "Path to kubeconfig file."
  type        = string
  default     = "~/.kube/config"
}

variable "namespace" {
  description = "Observability namespace."
  type        = string
  default     = "honua-observability"
}

variable "honua_metrics_target" {
  description = "Honua metrics endpoint target in host:port form."
  type        = string
}

variable "grafana_ingress_host" {
  description = "Optional Grafana ingress host. Leave empty to disable ingress."
  type        = string
  default     = ""
}
