# GCP Overlay (Managed Service for Prometheus)

## Collector exporter overlay

Use this overlay with the base collector config from `docs/alerting/README.md`:

- file: `docs/alerting/gcp/collector-overlay.yaml`
- authentication: Workload Identity (recommended)

## Publish PromQL alert policies

Use Cloud Monitoring alert policies with PromQL conditions and reuse expressions from `docs/alerting/rules/honua-core.yaml`.

Example workflow:
1. Create policy JSON/YAML for each rule.
2. Apply with `gcloud monitoring policies create --policy-from-file=...`.

## Notifications

Attach notification channels (PagerDuty, email, Slack, etc.) through Cloud Monitoring.
