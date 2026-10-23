# Audit platform-20261006 — issue 5634

| Finding id | Outcome | Evidence |
|---|---|---|
| `SRV-INF-006` | fixed | `FormSubmissionServiceTests.SRV_INF_006_WhenEditOutcomeIsUnknown_PreservesClaimAsTerminalFailure` proves that once feature-edit execution begins, an unknown outcome retains and completes the idempotency claim instead of deleting it. `FormSubmissionService.PreserveOrReleaseClaimAsync` also deliberately retains the pending claim when terminal persistence fails. |
| `SRV-INF-017` | fixed | `WorkflowPackageService.PublishVersionAsync` compiles `Trigger.Enabled` as `request.Enabled && schedule.Enabled`. A publication stored Disabled is absent from `ListScheduledAsync`. |
| `SRV-INF-022` | fixed | Schedule definition ids are `workflow-package:{packageId}:v{version}:{publicationId}`. The publication row is saved before `SetAsync`. The first definition write is dormant and is armed only after a re-read still shows the row active. A failed definition write deletes that row and the definition. Disable and delete re-key a legacy shared id onto each publication before syncing the stored id. |
| `SRV-INF-023` | fixed | `PostgresFieldExportStore.CreateExportAsync` reads one past `FieldExportLimits.MaxRows`, drops the probe row, and records `truncated` plus `rowLimit` on the filter. The response sets `X-Honua-Export-Truncated` and the audit details include `truncated`. |
| Static map center/zoom world-bound clamping | left | Outside the forms, workflow-schedule, and field-export change set for this fix. |
| Saved-map operation client-supplied actor | left | Outside the forms, workflow-schedule, and field-export change set for this fix. |
| Field export invalid-body fallback | fixed | `FieldExportService` returns 400 for invalid JSON and JSON null. `ContentLength == 0` is an empty filter. A missing Content-Length still reads the body. |
| Workflow package graph validation TOCTOU | fixed | `CreateVersionAsync` receives the validated graph. Under the package lock, a hash mismatch throws `WorkflowPackageGraphConflictException` (HTTP 409). The version stores that graph, not the current draft. The hash length-prefixes each field so parameter text cannot collide with a different parameter split. |
