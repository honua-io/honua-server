# Audit platform-20261006 — issue 5634

| Finding id | Outcome | Evidence |
|---|---|---|
| `SRV-INF-006` | fixed | `FormSubmissionServiceTests.SRV_INF_006_WhenEditOutcomeIsUnknown_PreservesClaimAsTerminalFailure` proves that once feature-edit execution begins, an unknown outcome retains and completes the idempotency claim instead of deleting it. `FormSubmissionService.PreserveOrReleaseClaimAsync` also deliberately retains the pending claim when terminal persistence fails. |
| `SRV-INF-017` | not attempted | Re-verified in `WorkflowPackageService.PublishVersionAsync`: the compiled schedule still derives `Trigger.Enabled` from the schedule declaration independently of `request.Enabled`. Deferred to keep the highest-severity finding complete. |
| `SRV-INF-022` | not attempted | Re-verified in `WorkflowPackageService.PublishVersionAsync`: `workflowDefinitionId` is still derived only from package id and version (`workflow-package:{packageId}:v{version}`), so publications share it. Deferred to keep the highest-severity finding complete. |
| `SRV-INF-023` | not attempted | Re-verified in `PostgresFieldExportStore.CreateExportAsync`: the query still uses `LIMIT @max_rows` with `MaxExportRows = 50_000`, and the returned record count is the materialized item count without a truncation signal. Deferred to keep the highest-severity finding complete. |
| Static map center/zoom world-bound clamping | not attempted | Low-severity checklist item; not reached after completing `SRV-INF-006`. |
| Saved-map operation client-supplied actor | not attempted | Low-severity checklist item; not reached after completing `SRV-INF-006`. |
| Field export invalid-body fallback | not attempted | Low-severity checklist item; not reached after completing `SRV-INF-006`. |
| Workflow package graph validation TOCTOU | not attempted | Low-severity checklist item; not reached after completing `SRV-INF-006`. |
