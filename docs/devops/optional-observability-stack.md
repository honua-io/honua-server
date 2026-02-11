# Optional Observability Stack (Prometheus + Grafana)

This guide covers the optional in-cluster observability add-on for Honua.

Use this only when you want self-hosted dashboards and alerts in Kubernetes.
Core Honua deployment does not require this stack.

## Scope

The Terraform module `infrastructure/terraform/modules/observability-stack` provisions:

- Prometheus (`prometheus-community/prometheus` Helm chart)
- Grafana (`grafana/grafana` Helm chart)
- Honua scrape job targeting `/metrics` with `format=prometheus`
- Alert rules from `docker/prometheus/alerts.yml`
- Dashboard provisioning from `docker/grafana/dashboards/honua-overview.json`
- Grafana admin credentials in a Kubernetes secret

## Quick Start

```bash
terraform -chdir=infrastructure/terraform/examples/observability init
terraform -chdir=infrastructure/terraform/examples/observability apply \
  -var "honua_metrics_target=honua-honua.default.svc.cluster.local:80"
```

## Configuration Knobs

Common module variables:

- `honua_metrics_target`: required scrape target (`host:port`)
- `namespace`: observability namespace
- `scrape_interval` and `evaluation_interval`: Prometheus cadence
- `alert_rules_file`: PromQL alert rules input
- `honua_dashboard_file`: Grafana dashboard JSON input
- `prometheus_persistence_enabled` / `prometheus_persistence_size`
- `grafana_persistence_enabled` / `grafana_persistence_size`
- `grafana_ingress_enabled` / `grafana_ingress_host`

## Outputs

- `prometheus_url`
- `grafana_url`
- `grafana_admin_secret_name`
- `grafana_admin_secret_keys`
- `dashboard_configmap_name`

## Security Hardening Defaults

- Grafana credentials are generated and stored as a Kubernetes secret.
- Prometheus and Grafana are internal by default (no ingress unless enabled).
- Dashboard and alert rule artifacts are source-controlled and reproducible.

Recommended hardening for production:

1. Restrict Grafana ingress with authenticated SSO and IP policies.
2. Encrypt Kubernetes secrets at rest and control RBAC access to namespace secrets.
3. Configure retention and PVC classes appropriate for incident forensics.
4. Route alert notifications through your incident platform.

## When Not to Use This Module

If you are on AWS/GCP/Azure managed monitoring, prefer the cloud-native path in `docs/alerting/README.md` and avoid running this optional stack.
