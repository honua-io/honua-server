---
type: guide
title: "Ops evidence posture"
description: "The 2026.1 operational read surfaces add an evidencePosture object to their existing JSON responses."
resource: "honua://capability/ops.health"
---
# Ops evidence posture

The 2026.1 operational read surfaces add an `evidencePosture` object to their existing JSON responses. This is an additive contract: existing fields remain present and retain their meanings. In particular, `generatedAt` is response/evaluation time and must not be interpreted as source observation time.

`evidencePosture.schemaVersion` is `1.0`. Minor additions may add optional fields; an incompatible shape or vocabulary requires a new major schema version. REST and MCP serialize the same server DTOs and therefore carry equivalent values.

## Closed vocabularies

- Completeness: `complete`, `partial`, `unavailable`, `notConfigured`.
- Backend kinds: `inProcess`, `durableStore`, `configProjection`, `composite`, `unverified`.
- Reason codes: `sourceUnavailable`, `neverSucceeded`, `stale`, `missingObservationTime`, `malformedObservationTime`, `futureObservationTime`, `partialResult`, `incompleteCoverage`, `truncated`, `backendUnverified`, `notConfigured`.
- Top-level source ids: `honua_ops_health`, `honua_ops_findings`, `honua_alert_events`, `honua_operate_events`, `honua_platform_release_status`, `honua_deploy_operations`.

Two composite reads also publish the section sources they are built from, alongside the top-level id:

- `honua_ops_health` covers `honua_ops_health.health_checks`, `honua_ops_health.serving_latency`, `honua_ops_health.gp_queue`, `honua_ops_health.alert_dispatch`, `honua_ops_health.deploy_release`, and `honua_ops_health.database_cache`.
- `honua_ops_findings` covers `honua_ops_findings.alert_dispatch`, `honua_ops_findings.control_plane`, `honua_ops_findings.deploy_preflight`, `honua_ops_findings.gp_queue`, `honua_ops_findings.workflow_operations`, `honua_ops_findings.serving_latency_rollup`, `honua_ops_findings.database_pressure`, and `honua_ops_findings.batch_backends`.

A composite source is `complete` only when every section it declares is individually actionable. Its `coverage.expectedComponentIds`/`coverage.includedComponentIds` name the sections it declared and the sections it actually covered, and its `observedAt` is no fresher than the oldest section it summarizes.

## Timestamps and completeness

- `observedAt` describes the data, `lastSuccessfulAt` describes collection health, and `generatedAt` describes response/evaluation time. A missing timestamp stays missing — it is never replaced with the current time — and produces `missingObservationTime`/`neverSucceeded` posture.
- All timestamps are UTC. An observation or last-success time beyond the evaluation instant (outside a one-minute clock-skew tolerance), an observation newer than its own last success, or an inverted requested/returned window fails closed to `futureObservationTime`/`malformedObservationTime`.
- A source carrying only coverage reasons (`partialResult`, `incompleteCoverage`, `truncated`) is `partial`: it returned valid data with known-incomplete coverage. Any other reason means the configured backend produced no trustworthy evidence and the source is `unavailable`.
- `notConfigured` means no backend was wired at all and stays distinct from `unavailable`. Neither is complete.
- `maximumAgeSeconds`/`validUntil` carry the server-owned validity window so clients never infer freshness from response time.
- The top-level `evidencePosture.status` is the weakest individual state (`complete` only when every source is actionable, `unavailable` when any source is `unavailable` or `notConfigured`, otherwise `partial`). It summarizes without hiding: every source is still listed with its own state.

## Backend identity

`backendKind` plus a stable `backendId` identify the configured implementation that was actually queried. Backend ids are fixed server-side labels (for example `alert-dispatch-store`, `control-plane-options`); hostnames, URLs, account ids, credentials, tenant identifiers, query text and raw provider errors are never exposed. A source with `backendKind: "unverified"` or a blank `backendId` is `backendUnverified` and can never be actionable.

## Client contract

Clients must fail closed unless every required source is `complete`, identifies a verified backend, has valid UTC `observedAt` and `lastSuccessfulAt` values, is inside `validUntil`/`maximumAgeSeconds`, and has complete requested coverage.

Each finding publishes `requiredSourceIds` — the sources its rule read — and `observationWindow`, the interval those sources actually observed (`returnedFrom`/`returnedTo`), plus `requestedFrom`/`requestedTo` when every required source declared a requested window. The window is derived only from measured source timestamps; when it cannot be derived it is omitted rather than approximated from `detectedAt`. `detectedAt` alone is not evidence freshness.

## Proposal safety

Finding proposal re-evaluation applies the same check before the operation gateway is looked up or called, so an incomplete source performs zero gateway/actuator calls. The check uses the posture from the same evaluation pass that produced the finding, and it precedes the deterministic auto-safe policy — an auto-safe action cannot bypass the evidence gate. Rejection uses the existing `Blocked` proposal status and the stable `evidencePostureNotActionable` reason.

Durable proposal evidence stores bounded `evidence:<sourceId>:<observedAt>:<completeness>` references; it never stores provider exceptions, endpoints, credentials, tenant data, or query text.

## Compatibility

`evidencePosture` is additive and nullable. Existing clients that do not read it are unaffected, and every legacy top-level field (`generatedAt`, `detectedAt`, `partialResult`, `sourceErrors`, `available`, `hasMore`, `nextCursor`, `clusterReplicaCount`) keeps its previous meaning and remains truthful during the transition.

## Alert backlog evidence

Alert evidence uses `backlogObservedAt`, the successful collection time of the represented backlog. The legacy `lastPollAt` field is the dispatcher attempt heartbeat and may advance during a storage outage; neither `observedAt` nor `lastSuccessfulAt` uses it. Failed reads retain the last successfully collected observation, and both findings and ops-health mark the source unavailable. Recovery requires a successful backlog collection.

## Managed Compose stack

On an installed Compose target, `honua_ops_health.managedTargets` reports the serving
Honua readiness probe, a PostGIS query against the managed database, and configured Redis `PING`.
The existing `health` section continues to describe the manager process. A stopped
or unready managed service makes `overallStatus` unhealthy even while the manager is
healthy. An unreachable executor reports unknown health and unavailable evidence.

Both health and findings publish the `honua_ops.managed_compose` section source.
A successfully observed outage is complete evidence. The deterministic
`managed-compose-service-unhealthy` finding recommends `compose.restart_service`
for the registered target and configured service only. Use `honua_propose_finding`
with that finding and target; a different principal approves it in the normal
approval inbox. Restart is never auto-safe. The executor checks readiness after
restarting the selected service, and a fresh health/findings read confirms recovery.

`honua_platform_release_status.serving` keeps `effectiveArtifactReference` as the
desired configuration. `observedArtifactReference` and `observedImageId` come from
Docker inspection of the managed serving container; `serverVersion` comes from
that server's authenticated `/version` endpoint. `observationStatus` and `observedAt`
identify readback availability and time. A differing running image sets `skewed`
and adds the target to `skewedIds`, including after rollback. Missing observations
never claim convergence. Targets without live readback report `notObserved`. Other deployment backends retain their existing configuration-based co-versioning and evidence; their configuration is not presented as an observed runtime revision.

The executor reads the version endpoint at the origin configured by `HealthProbeUrl`.
Set `ControlPlane:ComposeExecutor:VersionProbeApiKey` to the serving bootstrap
credential; the packaged quickstart supplies it locally. This credential is used
only for readback and is never included in the observation response.

For a supported installation without Redis, set
`ControlPlane:ComposeExecutor:RedisService` to an empty value on the executor (and
keep the manager's matching executor options consistent). Only configured managed
services are observed and eligible for restart. The standard quickstart includes
Redis and keeps its observation enabled.
