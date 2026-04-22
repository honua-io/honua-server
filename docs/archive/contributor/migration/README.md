# Pilot Evidence Kit

Standardized scorecards, checklists, and readout templates for lighthouse migration pilots. Every pilot must use this kit as the required starting point to ensure repeatable execution and referenceable outcomes.

## Kit Contents

- [Pilot Baseline Scorecard](PILOT_BASELINE_SCORECARD.md) — captures starting state at pilot kickoff
- [Pilot Endline Scorecard](PILOT_ENDLINE_SCORECARD.md) — measures outcomes at pilot closeout for delta analysis
- [Reconciliation Report Template](RECONCILIATION_REPORT_TEMPLATE.md) — structured reporting format for reconciliation harness output
- [Migration Parity Checklist](MIGRATION_PARITY_CHECKLIST.md) — pilot-scoped parity verification workflow
- [Executive Readout Template](EXECUTIVE_READOUT_TEMPLATE.md) — executive summary for pilot closeout
- [Case Study Checklist](CASE_STUDY_CHECKLIST.md) — evidence capture checklist for referenceability

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
| Historical GTM pilot playbooks | Removed from the live repo; not maintained in the archive tree |
| [Client Template Runbook](../../../gis/CLIENT_TEMPLATE_RUNBOOK.md) | Current client verification procedures |
| Historical procurement readiness materials | Removed from the live repo; not maintained in the archive tree |
| [Operator Runbooks](../../../operator/runbooks/README.md) | Current deployment and infrastructure procedures |
| [Release Checklist](../../../contributor/RELEASE_CHECKLIST.md) | Current release gate integration |
| [`parity-scorecard-governance.yml`](../../../../.github/workflows/parity-scorecard-governance.yml) | Parity scorecard CI governance |
| [`geoservices-parity-nightly.yml`](../../../../.github/workflows/geoservices-parity-nightly.yml) | Nightly geoservices parity runs |
| [`parity-scorecard-baseline.json`](../../../../tests/dotnet/Honua.Server.Tests/Import/parity-scorecard-baseline.json) | Existing parity baseline data |
