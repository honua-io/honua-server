# Cloud-Native Alerting (OTLP -> Collector -> Managed Prometheus)

This guide defines Honua alerting using open standards and managed services.

## Goals

- Use OTLP for metrics/traces/logs emitted by Honua.
- Route telemetry through OpenTelemetry Collector.
- Keep one PromQL ruleset for all clouds.
- Avoid running a required self-hosted Prometheus/Grafana stack.

## Reference Architecture

1. Honua emits OTLP telemetry (`OTEL_*` environment variables).
2. OpenTelemetry Collector receives OTLP and batches data.
3. Collector exports metrics to managed Prometheus via `remote_write` (or provider equivalent).
4. Alert policies are created from a shared PromQL rules file in this repository.

```yaml
receivers:
  otlp:
    protocols:
      grpc:
      http:
processors:
  batch:
exporters:
  prometheusremotewrite:
    endpoint: ${PROM_RW_ENDPOINT}
service:
  pipelines:
    metrics:
      receivers: [otlp]
      processors: [batch]
      exporters: [prometheusremotewrite]
```

## Shared Alert Rules

- Rules file: `docs/alerting/rules/honua-core.yaml`
- Scope: API availability, 5xx rate, latency, and saturation signals.

Use this exact file across providers to keep alert semantics consistent.

## Provider Overlays

- AWS overlay: `docs/alerting/aws/README.md`
- GCP overlay: `docs/alerting/gcp/README.md`
- Azure overlay: `docs/alerting/azure/README.md`

Each overlay contains:
- collector auth + exporter wiring
- rule publication examples for the cloud provider
- minimal provider-specific assumptions

## Operational Notes

- Keep `honua-core.yaml` as the source of truth and regenerate provider policies from it.
- Route notifications to on-call destinations (SNS, Cloud Monitoring channels, Action Groups).
- Review thresholds after load tests and major feature releases.
