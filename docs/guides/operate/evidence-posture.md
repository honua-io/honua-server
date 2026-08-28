# Ops evidence posture

The 2026.1 operational read surfaces add an `evidencePosture` object to their existing JSON responses. This is an additive contract: existing fields remain present and retain their meanings. In particular, `generatedAt` is response/evaluation time and must not be interpreted as source observation time.

`evidencePosture.schemaVersion` is `1.0`. Minor additions may add optional fields; an incompatible shape or vocabulary requires a new major schema version. REST and MCP serialize the same server DTOs and therefore carry equivalent values.

Closed vocabularies:

- Completeness: `complete`, `partial`, `unavailable`, `notConfigured`.
- Backend kinds: `inProcess`, `durableStore`, `configProjection`, `composite`, `unverified`.
- Top-level source ids: `honua_ops_health`, `honua_ops_findings`, `honua_alert_events`, `honua_operate_events`, `honua_platform_release_status`, `honua_deploy_operations`. Ops health additionally publishes the documented section ids `honua_ops_health.health_checks`, `honua_ops_health.serving_latency`, `honua_ops_health.gp_queue`, `honua_ops_health.alert_dispatch`, `honua_ops_health.deploy_release`, and `honua_ops_health.database_cache`.
- Reason codes: `sourceUnavailable`, `neverSucceeded`, `stale`, `missingObservationTime`, `malformedObservationTime`, `futureObservationTime`, `partialResult`, `incompleteCoverage`, `truncated`, `backendUnverified`, `notConfigured`.

Clients must fail closed unless every required source is `complete`, identifies a verified backend, has valid UTC `observedAt` and `lastSuccessfulAt` values, is inside `validUntil`/`maximumAgeSeconds`, and has complete requested coverage. `notConfigured` means no backend was configured; `unavailable` means a configured backend could not provide valid evidence. Neither is complete.

Finding proposal re-evaluation applies the same check before the operation gateway is called. Rejection uses the existing `Blocked` proposal status and stable `evidencePostureNotActionable` reason. Durable proposal evidence stores bounded `sourceId`, observation timestamp, and completeness references; it never stores provider exceptions, endpoints, credentials, tenant data, or query text.
