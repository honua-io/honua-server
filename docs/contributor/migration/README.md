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

The report artifact captures:

- source baseline metadata and digests
- target snapshot metadata and deploy-preflight state
- split comparison sections for capability, style, data, and operational readiness
- a computed cutover-readiness checklist with blocking reasons and warnings

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
