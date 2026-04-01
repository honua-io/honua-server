# Pilot Evidence Kit

Standardized scorecards, checklists, and readout templates for lighthouse migration pilots. Every pilot must use this kit as the required starting point to ensure repeatable execution and referenceable outcomes.

## Kit Contents

- [Pilot Baseline Scorecard](PILOT_BASELINE_SCORECARD.md) — captures starting state at pilot kickoff
- [Pilot Endline Scorecard](PILOT_ENDLINE_SCORECARD.md) — measures outcomes at pilot closeout for delta analysis
- [Reconciliation Report Template](RECONCILIATION_REPORT_TEMPLATE.md) — structured reporting format for reconciliation harness output
- [Migration Parity Checklist](MIGRATION_PARITY_CHECKLIST.md) — pilot-scoped parity verification workflow
- [Executive Readout Template](EXECUTIVE_READOUT_TEMPLATE.md) — executive summary for pilot closeout
- [Case Study Checklist](CASE_STUDY_CHECKLIST.md) — evidence capture checklist for referenceability

## Server-Generated Evidence Reports

Honua can now generate a durable migration evidence artifact through the admin API instead of relying on ad hoc exported notes.

- `POST /api/v1/admin/migrations/reports` starts background generation of a parity and cutover-readiness report.
- `GET /api/v1/admin/migrations/reports/jobs/{jobId}` returns job progress and the persisted `reportId` after completion.
- `GET /api/v1/admin/migrations/reports` lists immutable report summaries.
- `GET /api/v1/admin/migrations/reports/{reportId}` fetches the full JSON artifact for signoff, audit, or attachment to pilot records.

The request contract is intentionally narrow:

- `provider` currently supports only `arcgis-geoservices`.
- `sourceServiceUrl` and `targetBaseUrl` must be public HTTPS URLs without embedded credentials; loopback, private, and unresolvable hosts are rejected.
- `layers` is required and maps source layer IDs to target layer IDs; all layer IDs must be non-negative.
- `cutoverProfile` is `pilot` or `production`. The production profile escalates production-only warnings and failures into blocking readiness reasons.
- `rollbackPlanReference` is required. Optional provenance fields (`inventoryArtifactRef`, `translationManifestRef`, `importJobId`, `requestedBy`, and `summary`) are echoed into the stored artifact.
- Optional bounded probe controls are available when a pilot needs a narrower or more aggressive probe envelope: `sampleRowCount` (`1..100`, default `25`), `queryPageSize` (`1..100`, default `50`), and `probeTimeoutSeconds` (`1..60`, default `30`). These inputs keep remote parity work bounded instead of turning a pilot evidence run into an open-ended probe sweep.

The job lifecycle is asynchronous:

- The start call returns `202 Accepted` with a short `jobId` plus relative `statusUrl` and `cancelUrl`.
- Poll the job endpoint until `status` reaches `completed`, `failed`, or `cancelled`. Progress payloads expose `completedSteps`, `totalSteps`, `percentComplete`, `currentPhase`, `duration`, warnings, and any terminal `errorMessage`.
- Completed jobs include `reportId`, `readiness`, and any readiness warnings. Cancelled jobs do not create a persisted report artifact, and cancelling a terminal job returns `409 Conflict`.
- Queueing depends on distributed coordination. When Redis-backed coordination is unavailable, the start call returns `503` instead of falling back to local-only execution.
- The same progress record is also available through the unified operations endpoints at `/api/v1/admin/operations/{jobId}` and `/api/v1/admin/operations/type/MigrationEvidence`, but the dedicated migration job route remains the primary polling surface.

The report artifact captures:

- source baseline metadata and digests
- target snapshot metadata and deploy-preflight state
- split comparison sections for capability, style, data, and operational readiness
- a computed cutover-readiness checklist with blocking reasons and warnings

The persisted artifact contract is stable enough for pilot evidence packs:

- `request` preserves operator inputs and provenance references.
- `sourceBaseline` and `targetSnapshot` capture the metadata and digests used for the run, including per-layer field snapshots, extent snapshots, notes, and style digests when available.
- `targetSnapshot.operationalSnapshot` records the deploy-preflight outcome and database probe details used in the report decision, including pending migrations, executed-but-missing scripts, compatibility warnings, and probe errors.
- `comparison` is split into `capability`, `style`, `data`, and `operationalReadiness` arrays so downstream tooling can reason about each parity lane independently. Each entry carries `checkName`, `status`, `scope`, `summary`, optional `notes`, and structured `observations`.
- `cutoverReadiness` carries the final `state`, a de-duplicated `blockingReasons` list, warnings, and the full checklist that produced the decision. Checklist items expose both `requirementLevel` and `status` so pilot and production gates can be audited independently.
- `GET /api/v1/admin/migrations/reports` returns summary rows ordered by `generatedAt` descending and supports `provider`, `cutoverProfile`, and `readiness` filters for audit views. Each summary includes the immutable `reportHash` plus `warningCount` and `blockerCount` for quick triage, while the response still echoes the requested `limit` and `offset`.

## Pilot Lifecycle

| Stage | Artifacts | Timing |
|-------|-----------|--------|
| Pre-pilot | Baseline Scorecard, Parity Checklist | At kickoff |
| During pilot | Reconciliation Reports | After each import run |
| Closeout | Endline Scorecard, Executive Readout, Case Study Capture | At pilot end |

## Cookbook and Runbook Linkage

| Resource | Purpose |
|----------|---------|
| [Esri Migration Platform Plan](../ESRI_MIGRATION_PLATFORM_PLAN.md) | Migration architecture, phase gates, success metrics |
| [MVP Launch GTM Playbook](../../user/MVP_LAUNCH_GTM_PLAYBOOK.md) | GTM pilot stages, SOW structure |
| [Client Template Runbook](../../gis/CLIENT_TEMPLATE_RUNBOOK.md) | Client verification procedures |
| [Enterprise Procurement Readiness](../../user/ENTERPRISE_PROCUREMENT_READINESS.md) | Procurement packet |
| [DevOps Runbooks](../../devops/runbooks/README.md) | Deployment and infrastructure |
| [Release Checklist](../RELEASE_CHECKLIST.md) | Release gate integration |
| [`parity-scorecard-governance.yml`](../../../.github/workflows/parity-scorecard-governance.yml) | Parity scorecard CI governance |
| [`geoservices-parity-nightly.yml`](../../../.github/workflows/geoservices-parity-nightly.yml) | Nightly geoservices parity runs |
| [`parity-scorecard-baseline.json`](../../../tests/Honua.Server.Tests/Import/parity-scorecard-baseline.json) | Existing parity baseline data |
