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

## Live outage/recovery proof

`EvidencePostureLiveTests` is the opt-in deployed-environment contract. It reads a known actionable finding through the real MCP HTTP transport, asks an environment-owned harness to interrupt one telemetry backend, waits for that exact source to report `unavailable`, and verifies the finding proposal is blocked with `evidencePostureNotActionable`. It then restores the backend and waits for the MCP posture to return to complete and fresh. The recovery control is also invoked from test cleanup so a failed assertion does not intentionally leave the backend offline.

The harness supplies these variables only in an isolated live-test environment:

- `HONUA_LIVE_EVIDENCE_BASE_URL`: deployed Honua base URL.
- `HONUA_LIVE_EVIDENCE_API_KEY`: admin API key, sent only as `X-API-Key` and never logged by the test.
- `HONUA_LIVE_EVIDENCE_SOURCE_ID`: source envelope whose backend the harness controls.
- `HONUA_LIVE_EVIDENCE_FINDING_ID`: stable active finding that requires that source.
- `HONUA_LIVE_EVIDENCE_OUTAGE_URL`: idempotent harness-owned POST control that returns success after the backend is unavailable.
- `HONUA_LIVE_EVIDENCE_RECOVERY_URL`: idempotent harness-owned POST control that returns success after the backend is restored.

The outage and recovery controls are external test-harness endpoints, not Honua server routes. They must target only the isolated telemetry backend represented by `HONUA_LIVE_EVIDENCE_SOURCE_ID`; the Honua process and MCP transport remain online throughout the run.

### Native Windows receipt

`scripts/qualification/start_evidence_windows.ps1` starts an isolated native .NET server and disposable Docker Desktop Postgres/Redis containers on loopback ports 18475, 55475 and 56375. It explicitly opts into Preview alerting for this test only. The credentials in that launcher are public disposable fixture values. Run it from a clean checkout with the Windows .NET 10 SDK and Docker Desktop available. Existing containers with the harness names cause startup to fail; the launcher never replaces them.

```powershell
./scripts/qualification/start_evidence_windows.ps1
$env:HONUA_LIVE_EVIDENCE_API_KEY = 'local-evidence-admin-only'
python scripts/qualification/evidence_outage_windows.py --source-sha (git rev-parse HEAD) --server-assembly src/Honua.Server/bin/Release/net10.0/Honua.Server.dll --receipt TestResults/evidence-live/receipt.json --allow-isolated-outage
```

Wait for server startup before invoking the proof. The runner seeds exactly one dead-letter row, reads the real MCP finding, interrupts only `honua.alert_dispatch`, and checks the unavailable envelope, blocked proposal, unchanged Redis proposal set and unchanged dispatch rows. It restores the relation in `finally` and verifies recovery to complete/fresh. The receipt records the source revision and server-assembly SHA-256; it explicitly does not claim exact-candidate qualification. After the run, stop the PID recorded in `TestResults/evidence-live/server.pid` and remove only `honua-3475-postgres` and `honua-3475-redis`.

Alert evidence uses `backlogObservedAt`, the successful collection time of the represented backlog. The legacy `lastPollAt` field is the dispatcher attempt heartbeat and may advance during a storage outage; neither `observedAt` nor `lastSuccessfulAt` uses it. Failed reads retain the last successfully collected observation, and both findings and ops-health mark the source unavailable. Recovery requires a successful backlog collection.
