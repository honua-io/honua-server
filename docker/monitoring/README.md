# Monitoring Docker Assets

This directory contains reusable self-hosted monitoring assets for Docker-based environments.

## Layout

- `prometheus/alerts.yml` - bundled Prometheus alert rules.
- `grafana/dashboards/` - Grafana dashboard provisioning and dashboard JSON.
- `grafana/datasources/` - Grafana datasource provisioning.

The local scale-test stack mounts these files from `docker/scale-test/compose.yml`. Operator docs also reference them as examples for self-hosted Prometheus and Grafana deployments.
