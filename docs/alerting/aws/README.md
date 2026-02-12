# AWS Overlay (AMP + SigV4)

## Collector exporter overlay

Use this overlay with the base collector config from `docs/alerting/README.md`:

- file: `docs/alerting/aws/collector-overlay.yaml`
- environment variables:
  - `AWS_REGION`
  - `PROM_RW_ENDPOINT` (AMP remote_write URL)

## Publish PromQL rules to AMP

```bash
aws amp put-rule-groups-namespace \
  --workspace-id "$AMP_WORKSPACE_ID" \
  --name honua-core \
  --data fileb://docs/alerting/rules/honua-core.yaml
```

## Notifications

Bind AMP alertmanager routes to SNS (or managed Grafana notification policies).
