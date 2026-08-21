# Operate metrics and traces

Honua exports OpenTelemetry metrics at `/metrics` when the Prometheus exporter
is enabled. This inventory covers every literal instrument registered by
`src/**/Monitoring/*Metrics.cs`; CI checks it with
`scripts/ci/check-operate-metrics-doc.py`.

## Instrument inventory

| Instrument | Type | Meaning / principal tags |
|---|---|---|
| `honua_http_request_total` | Counter | HTTP requests; operation/status tags are added by the caller. |
| `honua_http_request_duration_ms` | Histogram | HTTP request latency in milliseconds. |
| `honua_http_active_requests` | Up/down counter | Requests currently executing. |
| `honua_operation_total` | Counter | Generic operation executions. |
| `honua_operation_duration_ms` | Histogram | Generic operation latency in milliseconds. |
| `honua_application_errors_total` | Counter | Application errors by type/operation. |
| `honua_error_recovery_duration_ms` | Histogram | Recovery latency in milliseconds. |
| `honua_database_query_total` | Counter | Database query executions. |
| `honua_database_query_duration_ms` | Histogram | Database query latency in milliseconds. |
| `honua_database_query_records` | Histogram | Rows returned or affected. |
| `honua_transaction_total` | Counter | Database transactions. |
| `honua_transaction_duration_ms` | Histogram | Transaction latency in milliseconds. |
| `honua_db_connection_acquisition_duration_ms` | Histogram | Pool acquisition latency; caller-supplied tags. |
| `honua_db_active_connections` | Gauge | Active tracked database connections. |
| `honua_db_pool_size` | Gauge | Configured/observed pool size. |
| `honua_db_pool_utilization_ratio` | Gauge | Active connections divided by known pool size. |
| `honua_db_connection_acquisition_failures_total` | Counter | Acquisition failures; `reason`. |
| `honua_db_connection_timeouts_total` | Counter | Connection acquisition timeouts. |
| `honua_cache_hits_total` | Counter | Cache hits; `cache_name`. |
| `honua_cache_misses_total` | Counter | Cache misses; `cache_name`. |
| `honua_cache_evictions_total` | Counter | Cache evictions; `cache_name`. |
| `honua_cache_operation_total` | Counter | Cache operations by the caller's cache/operation tags. |
| `honua_cache_operation_duration_ms` | Histogram | Cache operation latency. |
| `honua_cache_hit_ratio` | Histogram | Cache hit-ratio samples. |
| `honua_cache_hit_ratio_detailed` | Histogram | Windowed hit ratio by cache type. |
| `honua_cache_errors_total` | Counter | Cache operation errors. |
| `honua_geometry_operation_total` | Counter | Geometry operations. |
| `honua_geometry_operation_duration_ms` | Histogram | Geometry-operation latency. |
| `honua_geometry_complexity_coordinates` | Histogram | Coordinate count of processed geometries. |
| `honua_geometry_transformation_total` | Counter | Geometry coordinate transformations. |
| `honua_geometry_transformation_duration_ms` | Histogram | Geometry transformation latency. |
| `honua_coordinate_transform_total` | Counter | Coordinate transform operations. |
| `honua_coordinate_transform_duration_ms` | Histogram | Coordinate transform latency. |
| `honua_spatial_query_total` | Counter | Spatial query operations. |
| `honua_spatial_query_duration_ms` | Histogram | Spatial query latency. |
| `honua_spatial_filter_total` | Counter | Spatial filter operations. |
| `honua_spatial_filter_duration_ms` | Histogram | Spatial filter latency. |
| `honua_memory_allocated_bytes` | Gauge | Currently allocated managed bytes. |
| `honua_memory_allocated_mb` | Histogram | Allocated-memory samples in MiB. |
| `honua_memory_pressure_percent` | Gauge | Current process memory pressure percentage. |
| `honua_memory_pressure_alerts_total` | Counter | High-memory-pressure alerts. |
| `honua_gc_collection_total` | Counter | Garbage collections by generation. |

The Prometheus exporter may add conventional histogram suffixes at exposition
time. The table names the source instruments; units and labels do not rename
the contract.

## Prometheus scrape

```yaml
scrape_configs:
  - job_name: honua-server
    metrics_path: /metrics
    scheme: http
    static_configs:
      - targets: ["honua-server:8080"]
```

Protect the endpoint at the network/ingress layer in production. For local
evaluation, the monitoring bundle under `docker/monitoring/` supplies
Prometheus and Grafana. The sample dashboard is
[`honua-serving-overview.json`](../../../docker/monitoring/grafana/dashboards/honua-serving-overview.json).

## Trace taxonomy

OTLP traces use the same request/operation correlation IDs as logs and Operate
events. Start with these span families:

| Span family | What it covers |
|---|---|
| `honua.http.request` | Inbound protocol request. |
| `honua.db.query` / `honua.db.transaction` | Database work. |
| `honua.feature.query` / `honua.feature.edit` | Canonical feature pipeline. |
| `honua.controlplane.*` | Deploy/execution plan, start, observe, promote, rollback, and reconcile work. |
| `honua.tile.*` / `honua.map.render` | Tile and map rendering. |
| `honua.import.*` | File/staged import work. |

Use `trace_id`/correlation IDs to move from a finding or Operate event to its
request, backend call, and logs. Do not enable database statement text in
traces unless its data-handling implications are reviewed.
