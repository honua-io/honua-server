# Operate metric and evidence inventory

This inventory separates the signals required by the bounded 2026.1 Operate
loop from the deeper performance work tracked by #3300. This is a source
inventory, not an exact-candidate scrape receipt. Instruments that need traffic
appear only after the corresponding event occurs. Use it with the
[Operate scenario](scenario.md).

## Required evidence fields

| Field | Meaning | Actionability rule |
|---|---|---|
| `generatedAt` | Response/evaluation time | Never use as observation freshness. |
| `sourceId` | Closed source identity | Must match every finding `requiredSourceIds` entry. |
| `backendKind`, `backendId` | Implementation actually queried | Blank ids and `unverified` are non-actionable. |
| `observedAt` | Time represented by the data | Required, valid UTC; the server applies its documented one-minute future-clock tolerance. |
| `lastSuccessfulAt` | Last confirmed successful collection | Required and no older than the server validity policy. |
| `completeness` | `complete`, `partial`, `unavailable`, or `notConfigured` | Only `complete` is actionable. |
| `reasonCodes` | Closed failure/coverage vocabulary | Diagnose from these; do not parse prose. |
| `coverage` | Requested/returned window, paging, replicas, components | No truncation, missing component, or replica gap. |
| `maximumAgeSeconds`, `validUntil` | Server-owned freshness limit | Clients do not invent a freshness threshold. |

See [Ops evidence posture](evidence-posture.md) for the complete vocabularies.

## 2026.1 scenario signals

| Concern | REST/MCP evidence | Prometheus series to verify on the candidate |
|---|---|---|
| Request availability and latency | ops-health `health` and `servingLatency` | `honua_http_request_total`, `honua_http_request_duration_ms_bucket`, `honua_http_request_duration_ms_count`, `honua_http_request_duration_ms_sum`, `honua_http_active_requests` |
| Alert dispatch backlog | `alertDispatch`; `honua_alert_events` | `honua_alerts_dispatch_backlog_count`, `honua_alerts_dispatch_dead_lettered_count` |
| Alert evaluator leadership | alert health source | `honua_alerts_evaluation_no_leader` |
| Alert delivery outcomes | alert events and timeline | `honua_alerts_events_emitted_total`, `honua_alerts_dispatches_enqueued_total`, `honua_alerts_deliveries_succeeded_total`, `honua_alerts_deliveries_failed_total`, `honua_alerts_deliveries_dead_lettered_total`, `honua_alerts_deliveries_rate_capped_total`, `honua_alerts_deliveries_suppressed_total`, `honua_alerts_deliveries_circuit_deferred_total`, `honua_alerts_delivery_latency_bucket`, `honua_alerts_delivery_latency_count`, `honua_alerts_delivery_latency_sum` |
| Database/cache posture | ops-health `database` | `honua_cache_hit_ratio` plus database connection/acquisition metrics when the pool records them |
| GP queue | ops-health `geoprocessing` and findings | durable queue buckets; no Prometheus series is required to authorize the scenario |
| Deploy/release readiness | ops-health `deploy`, platform-release/deploy-operation reads | evidence envelope and typed operation receipt, not a free-form metric |

The enabled alerting qualification lane is configured to run Postgres webhook E2E with
`Alerts__Enabled=true` against an exact candidate SHA. The load/soak lane now
starts the server under Production policy and drives the real request histograms.
The existence of those lanes does not prove their latest run passed or make an
unconfigured local alert source complete. Customer alerting remains Preview in
2026.1: these qualification receipts prove behavior but do not promote the
capability to GA or create an availability/performance commitment.

## Not required for the 2026.1 claim

For this deployment/readiness scenario, the required sources are exactly the
selected finding's `requiredSourceIds`; do not enable Preview alerting merely
to make an unrelated composite health envelope complete. If the selected rule
requires an unconfigured source, stop rather than remove that source from the
denominator. Prometheus counters inform diagnosis; the server-authored envelope
and typed receipt decide actionability.

Retain `coverage.requestedFrom`/`requestedTo`, `returnedFrom`/`returnedTo`,
expected/included component IDs and replica coverage when present. A composite
observation is no fresher than its oldest component. Page cursors, truncation,
partial results and missing components must remain visible in the receipt.
Missing metrics are not numeric zero; a new `generatedAt` does not refresh
their source. Scrape timestamps do not replace source timestamps.

Pool saturation diagnosis, slow-query remediation, tile-cache/cache-seed
optimization, warehouse depth, raster/3D performance, broad autonomous tuning,
and hosted-model metrics belong to #3300 or later qualification. They may be
useful capacity signals, but they must not gate or widen this bounded scenario.
