# Issue 5626 audit disposition

Re-verification was performed against the current worktree at `7c422ec`. The fix unit was
handled in severity order. The two S1 findings were completed before assessing the S2 and S3
items; remaining applicable lower-severity work is deliberately recorded as not attempted rather
than changed without its required regression test.

| Finding id | Outcome | Evidence |
|---|---|---|
| `SRV-IMP-001` | already fixed on trunk | `StreamingFileImportService.Streaming.cs` drops the replace staging table whenever `totalImported == 0` before the swap, and `StreamingFileImportService.cs` now treats every `importedCount == 0` result as a failure. Consequently an empty replacement never promotes the staging table. |
| `SRV-IMP-002` | publication reuse and attachment re-import fixed | `GeoservicesLayerPublicationServiceTests.SRV_IMP_002_ReimportingPublishedLayer_RefreshesAndReusesExistingPublication` proves that a replacement publish conflict refreshes the canonical feature snapshot and returns the existing layer. Returning that layer allows the existing attachment-copy and reconciliation stages to continue with the source-to-target object-id map instead of reporting that nothing was published. Attachment reconciliation now uses persisted source identity and retires the previous imported set only after a complete copy, as detailed below. |
| `SRV-IMP-008` | not attempted | Re-verified `StreamingFileImportService.Batch.cs`: append batches still use independent transactions (or autocommit when continue-on-error is enabled). An atomic staging/merge design and a reader-failure integration test remain necessary. |
| `SRV-IMP-011` | not attempted | Re-verified `GeoservicesImportService.ImportSteps.cs`: cancellation at `ImportFailureStage.AfterCommit` still rethrows, while rollback is intentionally a no-op after commit. The background-service terminal-state behavior requires a dedicated cancellation regression test. |
| `SRV-IMP-012` | already fixed on trunk | `StreamingFileImportService.cs` now checks `importedCount == 0` without conditioning the failure on `failedCount`, so both an empty input and an all-failed/all-skipped input return `No features found in file` rather than success. |
| `SRV-IMP-013` | not attempted | Re-verified `UniversalImportJobService.cs`: snapshot refresh and final receipt writes still use the caller cancellation token after `ImportFileAsync` can have committed. A deterministic post-commit cancellation test is required before changing this path. |
| `SRV-IMP-016` | not attempted | Re-verified `GeoservicesImportService.ImportSteps.cs` and `MigrationFidelityEvaluator.cs`: source population snapshots are now evaluated independently of publication, but unconverted geometry ids are still supplied only to published-layer reconciliation. An unpublished geometry-loss regression test is still needed. |
| `SRV-IMP-017` | not attempted | Re-verified `GeoservicesImportService.BuildCreateTableSql`: non-OID/non-geometry source fields are still sanitized without reserving `objectid` or `geom`. A single field-name mapping shared by DDL, inserts, publication metadata, and reconciliation is required; no shallow DDL-only rename was made. |
| `SRV-IMP-S3-LOCK` | not attempted | Re-verified `StreamingFileImportService.Streaming.cs`: the per-target advisory lock is still acquired only for replace mode. This low-severity concurrency item was deferred until the higher-severity findings are completed. |
| `SRV-IMP-S3-CANCEL-METRIC` | not attempted | Re-verified `StreamingFileImportService.cs`: `OperationCanceledException` is rethrown without setting the metric/log status to `cancelled`. This low-severity item was deferred. |
| `SRV-IMP-S3-PHYSICAL-NAME` | not attempted | Re-verified `ImportEndpoints.cs`: the synchronous refresh still recomputes the physical table name instead of preferring `ImportResult.PhysicalTableName`. This low-severity item was deferred. |

PR #5664 review re-verification confirmed two follow-up defects in `SRV-IMP-002`:

- Replacement conflict recovery must contain non-cancellation failures from snapshot refresh
  and catalog lookup. The publication helper now returns its normal publication warning and
  `null` on either failure, while cancellation still propagates. The publication tests cover
  both operations with ordinary exceptions, typed publishing errors, and cancellation.
- Attachment re-import was verified as real and repaired without the prior attempt's size
  restriction. Migration 125 adds source identity, source parent/attachment IDs and a run
  generation to attachment metadata, with a database uniqueness constraint. Re-import upserts
  that identity while preserving the target attachment ID and rebinding the feature ID. After
  a fully verified inventory/copy, reconciliation removes all unseen imported rows for the
  publication, including removed parents, upstream attachments and a source that no longer
  advertises attachments. A failed inventory, copy, metadata write or cancellation retains unseen
  prior attachments; a complete retry converges without duplicate rows.
- Retiring an object and queuing storage cleanup occur in the same metadata transaction. The
  durable queue survives service/process restarts, and failed deletes retry on the next import.
  Cleanup uses an independent token and checks live metadata references before deleting, so a
  lost commit acknowledgement cannot trigger deletion of a newly committed object's bytes.
  Cloud upload and PostgreSQL still do not share a transaction: a process exit immediately
  after upload, before metadata or compensation is recorded, can leave an unreferenced object
  for the existing storage orphan reconciliation policy. This does not create duplicate visible
  attachment rows.
- Legacy policy: untagged rows are never inferred to be imported from filenames or feature IDs.
  They remain untouched, including attachments created through Honua. Their presence routes
  attachment fidelity to NeedsReview on every reconciliation until their ownership is verified.
  Operators can retain confirmed Honua attachments and accept the explicit ownership review,
  or remove confirmed obsolete legacy imports through the normal attachment deletion surface.
  No automatic adoption or bulk deletion of untracked attachments is performed.
- Attachment stores without source identity reconciliation support do not receive repeated
  UploadAsync calls; the importer records the unavailable reconciliation as unverified fidelity.

Recovery repair verification: the Release `dotnet test` build compiled `Honua.Postgres`
and `Honua.Postgres.Tests` successfully; all seven publication-service tests passed with
no skips. Project-scoped `dotnet format --include`, each wrapped in `timeout 20m`, passed
for both changed C# files without changes.
The related replacement, attachment-import, reconciliation-gate, fidelity-gate,
catalog-reconciliation, and import-failure-message suites passed all 43 tests with no skips
using the Release assemblies and local PostGIS.
