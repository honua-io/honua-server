# Azure Overlay (Azure Monitor Managed Prometheus)

## Collector exporter overlay

Use this overlay with the base collector config from `docs/alerting/README.md`:

- file: `docs/alerting/azure/collector-overlay.yaml`
- authentication: Managed Identity + Azure Monitor workspace permissions

## Publish PromQL alert rules

Create Azure Monitor Prometheus rule groups from `docs/alerting/rules/honua-core.yaml` expressions.

Example workflow:
1. Define rule group ARM/Bicep/Terraform resources.
2. Apply with `az deployment` or Terraform.

## Notifications

Use Action Groups for paging and incident routing.
