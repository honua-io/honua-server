# Migration Pilot And Cutover Checklist

Use this checklist after source discovery and manifest translation, before
moving production traffic to Honua. Keep a completed copy with the migration
inventory, manifest, parity evidence pack, and readiness attestation JSON.

## Inputs

- Source inventory artifact:
- Migration manifest artifact:
- Parity evidence pack:
- Readiness attestation JSON:
- Source owner:
- Honua operator:
- Planned pilot date:
- Planned cutover date:

## Readiness Checks

| ID | State | Evidence | Owner |
|---|---|---|---|
| `inventory-confirmed` | `unknown` | Source owner reviewed inventory scope, auth posture, completeness warnings, and missing artifacts. | |
| `manifest-reviewed` | `unknown` | Target resource actions, style actions, manual-review items, and unsupported items were reviewed. | |
| `parity-report-reviewed` | `unknown` | Capability, style, and data parity sections were reviewed after pilot migration. | |
| `known-gaps-accepted` | `unknown` | Every `fail` or `unknown` evidence item has an accepted remediation, waiver, or deferral. | |
| `rollback-plan-documented` | `unknown` | Rollback steps, data restore point, DNS/load-balancer reversion, and owner escalation path are documented. | |
| `traffic-switch-planned` | `unknown` | DNS, load-balancer, API client, tile client, and cache-warming changes are scheduled. | |

Allowed states are `pass`, `fail`, `unknown`, and `not-applicable`. Do not
mark an item `pass` until the evidence column points to a concrete artifact,
ticket, dashboard, runbook, or signed approval.

## Pilot Review

- Confirm the pilot used the same inventory and manifest artifacts under
  review.
- Confirm styles were imported, recreated, or waived according to
  `styleActions`.
- Confirm unsupported items were excluded intentionally or moved to a
  separate migration path.
- Confirm client endpoint changes were tested against Honua equivalents.
- Confirm generated parity evidence was rerun after any remediation.

## Cutover Review

- Confirm the latest parity evidence pack has no unaccepted `fail` items.
- Confirm unresolved `unknown` items are either remediated or explicitly
  accepted by the source owner.
- Confirm rollback timing, owners, and restore points are current.
- Confirm production credentials and secret references are in place.
- Confirm monitoring, alerting, and on-call coverage are active for cutover.
- Confirm the post-cutover validation window and decision owner.

## Signoff

| Role | Name | Date | Decision |
|---|---|---|---|
| Source owner | | | |
| Honua operator | | | |
| Application owner | | | |
| Incident lead | | | |
