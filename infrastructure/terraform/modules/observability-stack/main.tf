locals {
  default_alert_rules_file = "${path.module}/../../../../docker/prometheus/alerts.yml"
  default_dashboard_file   = "${path.module}/../../../../docker/grafana/dashboards/honua-overview.json"
  alert_rules_file         = var.alert_rules_file != "" ? var.alert_rules_file : local.default_alert_rules_file
  honua_dashboard_file     = var.honua_dashboard_file != "" ? var.honua_dashboard_file : local.default_dashboard_file
  alert_rules              = yamldecode(file(local.alert_rules_file))

  honua_scrape_config = merge(
    {
      job_name     = "honua"
      metrics_path = var.honua_metrics_path
      static_configs = [
        {
          targets = [var.honua_metrics_target]
        }
      ]
    },
    var.honua_metrics_format != "" ? {
      params = {
        format = [var.honua_metrics_format]
      }
    } : {}
  )

  prometheus_values = {
    alertmanager = {
      enabled = true
    }
    kube-state-metrics = {
      enabled = false
    }
    prometheus-node-exporter = {
      enabled = false
    }
    server = {
      persistentVolume = {
        enabled = var.prometheus_persistence_enabled
        size    = var.prometheus_persistence_size
      }
    }
    serverFiles = {
      "prometheus.yml" = {
        global = {
          scrape_interval     = var.scrape_interval
          evaluation_interval = var.evaluation_interval
        }
        scrape_configs = [
          local.honua_scrape_config
        ]
      }
      "alerting_rules.yml" = local.alert_rules
    }
  }

  prometheus_server_url = "http://${var.prometheus_release_name}-server.${var.namespace}.svc.cluster.local"

  grafana_values = {
    admin = {
      existingSecret = kubernetes_secret_v1.grafana_admin.metadata[0].name
      userKey        = "admin-user"
      passwordKey    = "admin-pass"
    }
    persistence = {
      enabled = var.grafana_persistence_enabled
      size    = var.grafana_persistence_size
    }
    datasources = {
      "datasources.yaml" = {
        apiVersion = 1
        datasources = [
          {
            name      = "Prometheus"
            type      = "prometheus"
            access    = "proxy"
            url       = local.prometheus_server_url
            isDefault = true
            editable  = true
          }
        ]
      }
    }
    dashboardProviders = {
      "dashboardproviders.yaml" = {
        apiVersion = 1
        providers = [
          {
            name            = "honua"
            orgId           = 1
            folder          = "Honua"
            type            = "file"
            disableDeletion = false
            editable        = true
            options = {
              path = "/var/lib/grafana/dashboards/honua"
            }
          }
        ]
      }
    }
    dashboardsConfigMaps = {
      honua = kubernetes_config_map_v1.honua_dashboard.metadata[0].name
    }
    ingress = {
      enabled          = var.grafana_ingress_enabled
      ingressClassName = var.grafana_ingress_class_name
      annotations      = var.grafana_ingress_annotations
      hosts            = var.grafana_ingress_host != "" ? [var.grafana_ingress_host] : []
      tls              = []
    }
  }
}

resource "kubernetes_namespace_v1" "this" {
  count = var.create_namespace ? 1 : 0

  metadata {
    name = var.namespace
  }
}

resource "random_password" "grafana_admin" {
  length  = 32
  special = true
}

resource "kubernetes_secret_v1" "grafana_admin" {
  metadata {
    name      = "${var.grafana_release_name}-admin"
    namespace = var.namespace
  }

  data = {
    "admin-user" = var.grafana_admin_user
    "admin-pass" = random_password.grafana_admin.result
  }

  depends_on = [kubernetes_namespace_v1.this]
}

resource "kubernetes_config_map_v1" "honua_dashboard" {
  metadata {
    name      = "honua-overview-dashboard"
    namespace = var.namespace
  }

  data = {
    "honua-overview.json" = file(local.honua_dashboard_file)
  }

  depends_on = [kubernetes_namespace_v1.this]
}

resource "helm_release" "prometheus" {
  name             = var.prometheus_release_name
  repository       = "https://prometheus-community.github.io/helm-charts"
  chart            = "prometheus"
  version          = var.prometheus_chart_version
  namespace        = var.namespace
  create_namespace = var.create_namespace

  values = [yamlencode(local.prometheus_values)]
}

resource "helm_release" "grafana" {
  name             = var.grafana_release_name
  repository       = "https://grafana.github.io/helm-charts"
  chart            = "grafana"
  version          = var.grafana_chart_version
  namespace        = var.namespace
  create_namespace = var.create_namespace

  values = [yamlencode(local.grafana_values)]

  depends_on = [
    helm_release.prometheus,
    kubernetes_secret_v1.grafana_admin,
    kubernetes_config_map_v1.honua_dashboard,
  ]
}
