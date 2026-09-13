---
type: reference
title: "Operate metric and evidence inventory"
description: "Source freshness, coverage, protected-update evidence and metric semantics for the bounded 2026.1 Operate scenario."
resource: "honua://capability/ops.observability"
---
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

### Deployment-source provenance

Read the source's provenance as well as its field values. The current findings
producer has these distinct sources:

| Source suffix under `honua_ops_findings` | Producer and qualification limit |
|---|---|
| `control_plane` | `configProjection`, `control-plane-options`: describes configuration evaluated now; it does not observe the serving revision at a provider. |
| `deploy_preflight` | `inProcess`, `deploy-preflight-probe`: describes the in-process preflight; it is not a provider rollback receipt. |
| `workflow_operations` | `durableStore`, `workflow-operation-store`: the producer currently stamps completeness and clocks from store registration and evaluation time. A `complete` envelope alone does not prove successful collection, backend-loss handling or full target coverage. |

For the deployment source, require the independently observed provider revision
and collection/coverage evidence described in the [scenario](scenario.md).
Do not reinterpret evaluation-time clocks as successful provider observations.
Live partial, unverified and backend-loss producer checks remain unmet in the
[qualification record](../../internal/contributor/operate-docs-precut-evidence.md).
Injected-envelope adapter tests prove suppression at that adapter boundary;
they cannot establish the producer's collection behavior.

For alert backlog evidence, `backlogObservedAt` is the successful collection
time used by the source envelope. Legacy `lastPollAt` is only a dispatcher
attempt heartbeat and may advance during a storage outage. The
[executed outage receipt](evidence/3475-windows-outage.json) preserves the
last successful observation through the failure and requires a new successful
collection after recovery; a new response or poll attempt cannot refresh it.

## 2026.1 scenario signals

| Concern | REST/MCP evidence | Prometheus series to verify on the candidate |
|---|---|---|
| Request diagnostics and latency | ops-health `health` and replica-local `servingLatency`; not platform availability | `honua_http_request_total`, `honua_http_request_duration_ms_bucket`, `honua_http_request_duration_ms_count`, `honua_http_request_duration_ms_sum`, `honua_http_active_requests` |
| Alert dispatch backlog | `alertDispatch`; `honua_alert_events` | `honua_alerts_dispatch_backlog_count`, `honua_alerts_dispatch_dead_lettered_count` |
| Alert evaluator leadership | alert health source | `honua_alerts_evaluation_no_leader` |
| Alert delivery outcomes | alert events and timeline | `honua_alerts_events_emitted_total`, `honua_alerts_dispatches_enqueued_total`, `honua_alerts_deliveries_succeeded_total`, `honua_alerts_deliveries_failed_total`, `honua_alerts_deliveries_dead_lettered_total`, `honua_alerts_deliveries_rate_capped_total`, `honua_alerts_deliveries_suppressed_total`, `honua_alerts_deliveries_circuit_deferred_total`, `honua_alerts_delivery_latency_bucket`, `honua_alerts_delivery_latency_count`, `honua_alerts_delivery_latency_sum` |
| Database/cache posture | ops-health `database` | `honua_cache_hit_ratio` plus database connection/acquisition metrics when the pool records them |
| GP queue | ops-health `geoprocessing` and findings | durable queue buckets; no Prometheus series is required to authorize the scenario |
| Deploy/release readiness | ops-health `deploy`, platform-release/deploy-operation reads | evidence envelope and typed operation receipt, not a free-form metric |

## Platform SLO and local diagnostics

The corrected `schemaVersion=1.1` contract for `GET /api/v1/operate/status`
separates `slo.nodeLocalRetainedTail` from a
platform SLI. Until a distributed, all-request, in-band-aware source exists,
`slo.configured=false` and `slo.availability=null`, even when an intended
availability target is configured. No platform burn rate or error budget is
derived from the tail.

The accepted `7ba4226` image still returns schema `1.0`, as the
[installed read observation](evidence/3302-candidate-read-observation.json)
records. A missing `nodeLocalRetainedTail` block on that image is a contract
version mismatch, not evidence of zero traffic. Its legacy advice to derive an
error budget from the in-process window must not be used for a platform SLO.
The corrected contract remains required when qualifying a replacement pin.

The diagnostic reports `scope=replica-local`, `isPlatformSli=false`, retained
population/capacity, overwritten samples and oldest/newest retained ages. Its
HTTP-5xx-only success ratio excludes HTTP-2xx protocol error envelopes. Unequal
replica traffic, overflow and replica replacement change that population;
averaging these ratios does not produce platform availability. It cannot
replace the selected update policy's candidate-scoped telemetry or functional
checks. See the [distributed comparison contract](../deploy/monitoring.md#distributed-comparison-evidence-status)
for the separate request-ledger/query proof and its qualification limits.

## Protected-update evidence

Keep these fields with the [scenario transcript](scenario.md); observation
envelopes and operation records have different jobs and clocks.

| Evidence | What to retain and assert |
|---|---|
| Identity and authority | Release/manifest digest, server image/revision, target/backend, finding, proposal, canonical operation, separate proposer/approver and audit/correlation IDs. Never substitute a client-generated success message for the typed actuator receipt. |
| Source clocks and coverage | Each required source's observation/last-success time, completeness, backend identity, validity, requested/returned window, components and replicas. Response generation and scrape time do not refresh a failed collection. |
| Protection policy | `protection.policyDigest`, `previousRevision`, `candidateRevision`, `phase`, `reasonCode`, `firstExposureAt`, `observationDeadline` and `recoveryDeadline` where reported. Preserve the bound policy and declared sample cadence with the receipt. |
| Completion | Expected/observed revision and configuration, readiness samples across the full observation window, functional result values, authorization denials and committed-data preservation. An activation timestamp is not a successful completion timestamp. |
| Recovery | Recovery trigger, previous-revision identity, provider observation, functional verification results and measured detection/recovery duration. A timeout, failed verification or absent proof must remain visible as needing attention. |

Missing evidence forbids new changes. An already-approved operation's bound
policy may require deterministic recovery after missing telemetry exceeds its
grace period; retain that trigger and the original approval instead of creating
a new change. Unknown recovery health never establishes successful restoration.

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
