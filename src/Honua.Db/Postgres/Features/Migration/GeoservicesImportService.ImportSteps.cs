// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Shared.Models;
using Npgsql;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Db.Postgres.Features.Migration;
using Honua.Db.Postgres.Features.FileImport;

using Honua.Db.Postgres.Features.Infrastructure;
namespace Honua.Db.Postgres.Features.Migration;

internal sealed partial class GeoservicesImportService
{
    /// <inheritdoc />
    public async Task<GeoservicesImportResult> ImportLayerAsync(
        GeoservicesImportRequest request,
        IProgress<GeoservicesImportProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ValidateTableName(request.TableName);

        var stopwatch = Stopwatch.StartNew();
        var warnings = new List<string>();
        var jobId = string.IsNullOrWhiteSpace(request.JobId)
            ? Guid.NewGuid().ToString("N")[..8]
            : request.JobId;
        var startedAt = DateTimeOffset.UtcNow;
        var targetSchema = ResolveTargetSchema(request.TargetSchema);

        Log.ImportStarting(_logger, request.ServiceUrl, request.LayerId, request.TableName);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // #4600: one import per target table, for the whole job. A retry submitted while the original
        // job is still running, or two imports aimed at the same table, would otherwise race: the loser
        // blocks on the winner's uncommitted catalog rows and then fails opaquely, or replaces the table
        // the winner just committed, or is still publishing, copying attachments into, or reconciling.
        // The lease is session-level so it survives the data commit; the finally below releases it, and a
        // dropped connection (a crashed worker) releases it too.
        if (!await TryAcquireTargetImportLockAsync(connection, targetSchema, request.TableName, cancellationToken))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            stopwatch.Stop();
            Log.TargetImportInProgress(_logger, request.TableName);
            return GeoservicesImportResult.CreateFailure(
                request.TableName,
                request.ServiceUrl,
                request.LayerId,
                $"Another import is already writing target table '{request.TableName}'; this job did not modify it. "
                + "Wait for that import to finish, then retry.",
                stopwatch.Elapsed) with
            {
                ServiceName = request.ServiceName
            };
        }

        try
        {
            // Phase 1: Discover layer metadata
            ReportProgress(progress, jobId, startedAt, GeoservicesImportStatus.Discovering, request,
                "Discovering layer metadata", 0, null);

            var layerInfo = await _restClient.GetLayerInfoAsync(
                request.ServiceUrl,
                request.LayerId,
                request.RequestTimeoutSeconds,
                request.MaxRetries,
                cancellationToken,
                request.Credentials);

            Log.LayerDiscovered(_logger, layerInfo.Name, layerInfo.Fields.Length, layerInfo.FeatureCount);

            var totalFeatures = layerInfo.FeatureCount;
            var batchSize = request.BatchSize ?? layerInfo.MaxRecordCount ?? 1000;

            // #4600: an import that is not a replacement says so instead of failing inside CREATE TABLE.
            // A job restarted after its data committed lands here too, so the message names that case.
            var targetExists = await RelationExistsAsync(connection, targetSchema, request.TableName, cancellationToken);
            if (targetExists && !request.OverwriteExisting)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                stopwatch.Stop();
                return BuildTargetUnavailableResult(
                    request,
                    layerInfo,
                    $"Target table '{request.TableName}' already exists and overwriteExisting is false, so it was not modified. "
                    + "Set overwriteExisting to replace it. A job restarted after its data was committed also reports this; "
                    + "the existing table holds that earlier run's result.",
                    stopwatch.Elapsed);
            }

            // #4600: a replacement must never trade a complete prior target for a weaker one. It loads
            // into a staging table and swaps it in just before commit, so the live target stays readable
            // and unlocked for the whole transfer. The staging table is created inside the import
            // transaction: a failure, a refused replacement, a cancellation or a crashed worker all remove
            // it with the rollback, and the prior target is never touched.
            var replacingExistingTarget = request.OverwriteExisting && targetExists;
            var loadTable = replacingExistingTarget
                ? BuildStagingTableName(request.TableName, jobId)
                : request.TableName;
            if (replacingExistingTarget)
            {
                // Readers keep the prior rows during the transfer; writers wait for the swap rather than
                // committing edits the swap would silently discard.
                await FenceTargetWritesAsync(connection, targetSchema, request.TableName, cancellationToken);
            }

            // Phase 2: Create table
            ReportProgress(progress, jobId, startedAt, GeoservicesImportStatus.CreatingTable, request,
                "Creating PostGIS table", 0, totalFeatures, layerInfo.Name);

            await CreateTableAsync(connection, targetSchema, loadTable, layerInfo, request.TargetSrid, cancellationToken);

            // Phase 3: Retrieve and insert features
            var featuresProcessed = 0;
            var failedFeatures = 0;
            var offset = 0;
            var batchNumber = 0;
            var hasMore = true;
            var objectIdMap = new Dictionary<long, long>();
            var seenSourceObjectIds = new HashSet<long>();
            var sourceObjectIds = layerInfo.SupportsPagination == false
                ? await _restClient.QueryObjectIdsAsync(
                    request.ServiceUrl,
                    request.LayerId,
                    request.WhereClause,
                    request.RequestTimeoutSeconds,
                    request.MaxRetries,
                    cancellationToken,
                    request.Credentials)
                : null;
            var objectIdWindowSize = sourceObjectIds is null
                ? batchSize
                : Math.Min(Math.Max(1, batchSize), layerInfo.MaxRecordCount ?? int.MaxValue);
            var objectIdWindows = sourceObjectIds is null
                ? null
                : sourceObjectIds.Chunk(Math.Max(1, objectIdWindowSize)).ToArray();
            const int maxImportPages = 100_000;

            // #4600: the source population the transfer starts from, with the import's filter applied. It
            // is compared with the population after the last page, so a source edited mid-transfer is
            // reported rather than implied to be a snapshot, and it is the baseline a filtered import is
            // reconciled against (the discovery count is always unfiltered).
            long? sourceCountBeforeTransfer = sourceObjectIds is not null
                ? sourceObjectIds.Length
                : string.IsNullOrWhiteSpace(request.WhereClause)
                    ? layerInfo.FeatureCount
                    : await TryCountSourceFeaturesAsync(request, cancellationToken);

            while (hasMore && !cancellationToken.IsCancellationRequested &&
                   (objectIdWindows is null || batchNumber < objectIdWindows.Length))
            {
                batchNumber++;
                if (batchNumber > maxImportPages)
                {
                    throw new InvalidOperationException(
                        $"ArcGIS import stopped after {maxImportPages} pages because the source did not make pagination progress.");
                }
                ReportProgress(progress, jobId, startedAt, GeoservicesImportStatus.RetrievingFeatures, request,
                    $"Retrieving batch {batchNumber}", featuresProcessed, totalFeatures, layerInfo.Name);

                // Query features from remote service
                var queryResult = await _restClient.QueryFeaturesAsync(
                    request.ServiceUrl,
                    request.LayerId,
                    offset,
                    batchSize,
                    request.WhereClause,
                    request.OutputFields,
                    request.TargetSrid,
                    request.RequestTimeoutSeconds,
                    request.MaxRetries,
                    cancellationToken,
                    request.Credentials,
                    objectIdWindows is null ? null : objectIdWindows[batchNumber - 1]);

                if (queryResult.Features.Length == 0)
                {
                    break;
                }

                // Insert features into PostGIS
                ReportProgress(progress, jobId, startedAt, GeoservicesImportStatus.InsertingFeatures, request,
                    $"Inserting batch {batchNumber} ({queryResult.Features.Length} features)",
                    featuresProcessed, totalFeatures, layerInfo.Name);

                if (objectIdWindows is not null)
                {
                    if (queryResult.ExceededTransferLimit)
                    {
                        throw new InvalidOperationException(
                            $"ArcGIS object-id window {batchNumber} exceeded the source transfer limit; the import was not completed.");
                    }

                    var objectIdField = layerInfo.Fields.FirstOrDefault(static field => field.IsObjectId)?.Name;
                    var returnedObjectIds = new HashSet<long>();
                    if (objectIdField is null || queryResult.Features.Any(feature =>
                            !TryReadSourceObjectId(feature, objectIdField, out var returnedObjectId)
                            || !returnedObjectIds.Add(returnedObjectId)))
                    {
                        throw new InvalidOperationException(
                            $"ArcGIS object-id window {batchNumber} did not return identifiable source object IDs.");
                    }

                    if (objectIdWindows[batchNumber - 1].Any(objectId => !returnedObjectIds.Contains(objectId)))
                    {
                        throw new InvalidOperationException(
                            $"ArcGIS object-id window {batchNumber} did not return every requested source object ID.");
                    }
                }

                var newFeatures = FilterUnseenFeatures(
                    queryResult.Features,
                    layerInfo,
                    seenSourceObjectIds,
                    objectIdWindows is null ? null : objectIdWindows[batchNumber - 1]);
                if (newFeatures.Length == 0)
                {
                    if (objectIdWindows is null)
                    {
                        throw new InvalidOperationException(
                            "ArcGIS import stopped because the source returned no pagination progress.");
                    }

                    throw new InvalidOperationException(
                        $"ArcGIS object-id window {batchNumber} returned no requested features.");
                }

                var batchInsert = await InsertFeaturesAsync(
                    connection,
                    transaction,
                    targetSchema,
                    loadTable,
                    layerInfo,
                    newFeatures,
                    request.TargetSrid,
                    cancellationToken);

                featuresProcessed += batchInsert.Inserted;
                failedFeatures += batchInsert.Failed;

                foreach (var entry in batchInsert.ObjectIdMap)
                {
                    objectIdMap[entry.Key] = entry.Value;
                }

                if (batchInsert.Failed > 0)
                {
                    warnings.Add($"Batch {batchNumber}: {batchInsert.Failed} features failed to insert");
                }

                Log.BatchCompleted(_logger, batchNumber, batchInsert.Inserted, batchInsert.Failed, featuresProcessed);

                offset += queryResult.Features.Length;
                hasMore = objectIdWindows is not null
                    ? batchNumber < objectIdWindows.Length
                    : queryResult.ExceededTransferLimit || queryResult.Features.Length == batchSize;
            }

            // #4600: the loop stops polling the source once the token fires. Surface that as a
            // cancellation (rolled back below, so a replaced target is retained) rather than letting it
            // read as a generic incomplete transfer.
            cancellationToken.ThrowIfCancellationRequested();

            if (objectIdWindows is { } && batchNumber < objectIdWindows.Length)
            {
                throw new InvalidOperationException("ArcGIS object-id window import did not process all source object IDs.");
            }

            if (replacingExistingTarget && failedFeatures > 0)
            {
                // #4600: refuse the swap. Committing here would replace a complete prior target with
                // one missing the records that failed to load; the rollback discards the staging table
                // and the prior target was never touched.
                await transaction.RollbackAsync(CancellationToken.None);
                stopwatch.Stop();
                Log.ReplacementRefused(_logger, request.TableName, failedFeatures);
                return BuildReplacementRefusedResult(
                    request, layerInfo, featuresProcessed, failedFeatures, warnings, stopwatch.Elapsed);
            }

            // The per-window buffers are finished with. Release them before the source is enumerated
            // again, so the comparison adds at most one object-ID array to the import's footprint.
            seenSourceObjectIds.Clear();
            seenSourceObjectIds.TrimExcess();
            objectIdWindows = null;

            // #4600: close the bracket around the transfer before committing, so a source that changed
            // while it was being read is recorded against exactly the data being committed.
            var sourceSnapshot = await CaptureSourceSnapshotAsync(
                request,
                sourceObjectIds,
                sourceCountBeforeTransfer,
                cancellationToken);

            if (replacingExistingTarget)
            {
                await SwapStagingIntoTargetAsync(connection, targetSchema, loadTable, request.TableName, cancellationToken);
            }

            // Phase 4: Create spatial index
            ReportProgress(progress, jobId, startedAt, GeoservicesImportStatus.Publishing, request,
                "Creating spatial index", featuresProcessed, totalFeatures, layerInfo.Name);

            // #4600: a nonspatial ArcGIS table is created without a geom column (BuildCreateTableSql), so
            // indexing it unconditionally failed every table import with 42703.
            if (!string.IsNullOrEmpty(layerInfo.GeometryType))
            {
                await CreateSpatialIndexAsync(connection, targetSchema, request.TableName, cancellationToken);
            }

            await AnalyzeTableAsync(connection, targetSchema, request.TableName, cancellationToken);

            await transaction.CommitSafelyAsync(cancellationToken);

            PublishedLayerSummary? publishedLayer = null;
            if (request.AutoPublish)
            {
                publishedLayer = await _layerPublicationService.TryPublishImportedLayerAsync(
                    request,
                    targetSchema,
                    layerInfo,
                    warnings,
                    progress,
                    jobId,
                    startedAt,
                    featuresProcessed,
                    _connectionProvider.GetConnectionString(),
                    cancellationToken).ConfigureAwait(false);
            }

            // #4600: attachment accounting is kept on its own evidence (advertised / copied / failed /
            // unreadable-inventory) so attachment parity is decided independently of feature counts.
            MigrationFidelityAttachmentInput? attachmentFidelity = null;
            var attachmentCount = 0;
            var failedAttachments = 0;
            if (layerInfo.HasAttachments
                && request.ImportAttachments
                && publishedLayer != null
                && _attachmentStore != null
                && objectIdMap.Count > 0)
            {
                var attachmentOutcome = await CopyAttachmentsAsync(
                    request,
                    layerInfo,
                    publishedLayer.LayerId,
                    objectIdMap,
                    warnings,
                    progress,
                    jobId,
                    startedAt,
                    featuresProcessed,
                    cancellationToken).ConfigureAwait(false);

                attachmentCount = attachmentOutcome.Copied;
                failedAttachments = attachmentOutcome.Failed;
                attachmentFidelity = new MigrationFidelityAttachmentInput
                {
                    Advertised = attachmentOutcome.Advertised,
                    Copied = attachmentOutcome.Copied,
                    Failed = attachmentOutcome.Failed,
                    UnverifiedParents = attachmentOutcome.UnverifiedParents
                };
            }
            else if (layerInfo.HasAttachments && request.ImportAttachments && _attachmentStore == null)
            {
                warnings.Add(
                    "Layer advertises attachments, but no attachment store is registered; attachments were not copied.");
                attachmentFidelity = new MigrationFidelityAttachmentInput { CopySkipped = true };
            }
            else if (layerInfo.HasAttachments)
            {
                attachmentFidelity = new MigrationFidelityAttachmentInput { CopySkipped = true };
            }

            // Phase 5: Validating â€” reconcile the published layer against the apply-time source
            // snapshot before declaring the import complete. A hard finding routes the run to
            // NeedsReview (issue #1380) instead of Completed; warn findings are recorded but do
            // not block. Reconciliation runs outside the import transaction (which is already
            // committed) because it probes the published, queryable layer.
            GeoservicesReconciliationGateOutcome reconciliation = GeoservicesReconciliationGateOutcome.Skipped;
            if (publishedLayer is not null && _layerPublicationService.ReconciliationEnabled)
            {
                ReportProgress(progress, jobId, startedAt, GeoservicesImportStatus.Validating, request,
                    "Reconciling published layer against source snapshot",
                    featuresProcessed, featuresProcessed, layerInfo.Name, publishedLayer.LayerId,
                    attachmentsProcessed: attachmentCount, failedAttachments: failedAttachments);

                // #4600 AC6: a filtered import is reconciled against the filtered source. The discovery
                // count is always unfiltered, so the transfer's filtered baseline stands in for it.
                var reconciliationSource = string.IsNullOrWhiteSpace(request.WhereClause)
                    ? layerInfo
                    : layerInfo with
                    {
                        FeatureCount = sourceCountBeforeTransfer is { } filteredCount ? (int)filteredCount : null
                    };

                reconciliation = await _layerPublicationService.RunReconciliationGateAsync(
                    request,
                    jobId,
                    reconciliationSource,
                    publishedLayer,
                    cancellationToken).ConfigureAwait(false);
            }

            stopwatch.Stop();

            // #4600: one verdict, not two. Data-movement reconciliation, catalog (schema/domain/
            // identifier/subtype) reconciliation, dropped records, lost attachments and deferred
            // relationships all fold into a single fidelity evaluation. Any blocking difference
            // routes the run to NeedsReview; a check that never executed downgrades the run to
            // 'unverified' rather than letting it be reported as a full-fidelity migration.
            var fidelity = MigrationFidelityEvaluator.Evaluate(new MigrationFidelityEvaluationInput
            {
                LayerName = string.IsNullOrWhiteSpace(layerInfo.Name) ? request.TableName : layerInfo.Name,
                DataReconciliation = reconciliation.Artifact,
                DataReconciliationExecuted = reconciliation.DataCheckExecuted,
                CatalogReconciliation = reconciliation.CatalogReport,
                CatalogReconciliationExecuted = reconciliation.CatalogCheckExecuted,
                PublishedTarget = publishedLayer is not null,
                PublishRequested = request.AutoPublish,
                FailedFeatures = failedFeatures,
                Attachments = attachmentFidelity,
                SourceSnapshot = sourceSnapshot
            });

            foreach (var difference in fidelity.Differences)
            {
                Log.FidelityDifference(_logger, request.TableName, difference.Code, difference.Severity, difference.Summary);
            }

            if (fidelity.IsBlocking)
            {
                var reviewReason = fidelity.BlockingReason
                    ?? "Post-publish reconciliation reported a blocking discrepancy.";
                Log.ReconciliationGateBlocked(_logger, request.TableName, reviewReason);

                ReportProgress(progress, jobId, startedAt, GeoservicesImportStatus.NeedsReview, request,
                    "Import published but requires operator review (fidelity gate)",
                    featuresProcessed,
                    featuresProcessed,
                    layerInfo.Name,
                    publishedLayer?.LayerId,
                    attachmentsProcessed: attachmentCount,
                    failedAttachments: failedAttachments,
                    reconciliationArtifact: reconciliation.Artifact,
                    catalogReconciliationReport: reconciliation.CatalogReport,
                    fidelityVerdict: fidelity.Verdict,
                    fidelityDifferences: fidelity.Differences);

                return GeoservicesImportResult.CreateNeedsReview(
                    request.TableName,
                    request.ServiceUrl,
                    request.LayerId,
                    featuresProcessed,
                    failedFeatures,
                    publishedLayer?.LayerId,
                    publishedLayer?.ServiceName ?? request.ServiceName,
                    layerInfo.Name,
                    stopwatch.Elapsed,
                    warnings,
                    attachmentCount,
                    failedAttachments,
                    reconciliation.Artifact,
                    reconciliation.CatalogReport,
                    reviewReason,
                    fidelity.Verdict,
                    fidelity.Differences);
            }

            Log.ImportCompleted(_logger, request.TableName, featuresProcessed, failedFeatures,
                stopwatch.Elapsed.TotalSeconds);

            // Report final progress
            ReportProgress(progress, jobId, startedAt, GeoservicesImportStatus.Completed, request,
                publishedLayer == null ? "Import completed" : "Import completed and layer published",
                featuresProcessed,
                featuresProcessed,
                layerInfo.Name,
                publishedLayer?.LayerId,
                attachmentsProcessed: attachmentCount,
                failedAttachments: failedAttachments,
                reconciliationArtifact: reconciliation.Artifact,
                catalogReconciliationReport: reconciliation.CatalogReport,
                fidelityVerdict: fidelity.Verdict,
                fidelityDifferences: fidelity.Differences);

            return GeoservicesImportResult.CreateSuccess(
                request.TableName,
                request.ServiceUrl,
                request.LayerId,
                featuresProcessed,
                failedFeatures,
                publishedLayer?.LayerId,
                publishedLayer?.ServiceName ?? request.ServiceName,
                layerInfo.Name,
                duration: stopwatch.Elapsed,
                warnings: warnings,
                attachmentCount: attachmentCount,
                failedAttachments: failedAttachments,
                reconciliationArtifact: reconciliation.Artifact,
                catalogReconciliationReport: reconciliation.CatalogReport,
                fidelityVerdict: fidelity.Verdict,
                fidelityDifferences: fidelity.Differences);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            Log.ImportCancelled(_logger, request.TableName);
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Top-level import failure: roll back, log, and map to a sanitized failure result via
            // BuildImportFailureMessage rather than leaking the raw exception to the caller.
            await transaction.RollbackAsync(CancellationToken.None);
            stopwatch.Stop();
            Log.ImportFailed(_logger, request.TableName, ex);

            return GeoservicesImportResult.CreateFailure(
                request.TableName,
                request.ServiceUrl,
                request.LayerId,
                BuildImportFailureMessage(ex),
                stopwatch.Elapsed);
        }
        finally
        {
            // #4600: every path out of the job ends the target lease, after its transaction has committed
            // or rolled back. A failure here only means the connection is gone, which releases it anyway.
            try
            {
                await ReleaseTargetImportLockAsync(connection, targetSchema, request.TableName);
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
            {
                Log.TargetLeaseReleaseFailed(_logger, request.TableName, ex);
            }
        }
    }

    private static string BuildImportFailureMessage(Exception exception)
        => exception switch
        {
            ArcGisAuthenticationException auth => auth.Kind switch
            {
                ArcGisAuthenticationFailureKind.CredentialExpired =>
                    $"{ImportCompatibilityCodes.ArcGisTokenExpired}: ArcGIS credentials are expired. Provide a refreshed token or credential reference and retry.",
                ArcGisAuthenticationFailureKind.CredentialDenied =>
                    $"{ImportCompatibilityCodes.ArcGisAccessDenied}: ArcGIS rejected the supplied credentials. Verify access to the layer and retry.",
                _ =>
                    $"{ImportCompatibilityCodes.ArcGisTokenRequired}: ArcGIS service requires authentication. Provide a token or credential reference and retry."
            },
            _ => "Import from ArcGIS service failed."
        };

    private static GeoservicesImportResult BuildReplacementRefusedResult(
        GeoservicesImportRequest request,
        GeoservicesLayerInfo layerInfo,
        int loadedFeatures,
        int failedFeatures,
        List<string> warnings,
        TimeSpan duration)
    {
        // The evaluator supplies the same records-lost difference a completed run would carry, so the
        // refusal is actionable from the persisted result rather than only from the message text.
        var fidelity = MigrationFidelityEvaluator.Evaluate(new MigrationFidelityEvaluationInput
        {
            LayerName = string.IsNullOrWhiteSpace(layerInfo.Name) ? request.TableName : layerInfo.Name,
            FailedFeatures = failedFeatures
        });

        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"Replacement refused: {failedFeatures} of {loadedFeatures + failedFeatures} source record(s) failed to load, "
            + $"so the existing target table '{request.TableName}' was retained unchanged. Resolve the failing records and retry the import.");

        return GeoservicesImportResult.CreateFailure(
            request.TableName,
            request.ServiceUrl,
            request.LayerId,
            message,
            duration) with
        {
            FailedFeatures = failedFeatures,
            SourceLayerName = layerInfo.Name,
            ServiceName = request.ServiceName,
            Warnings = warnings,
            FidelityDifferences = fidelity.Differences
        };
    }

    private static GeoservicesImportResult BuildTargetUnavailableResult(
        GeoservicesImportRequest request,
        GeoservicesLayerInfo layerInfo,
        string message,
        TimeSpan duration)
        => GeoservicesImportResult.CreateFailure(
            request.TableName,
            request.ServiceUrl,
            request.LayerId,
            message,
            duration) with
        {
            SourceLayerName = layerInfo.Name,
            ServiceName = request.ServiceName
        };

    /// <summary>
    /// Counts the source records matching the import filter, or <c>null</c> when the source cannot
    /// answer. Cancellation propagates; any other failure only leaves the source snapshot unverified.
    /// </summary>
    private async Task<long?> TryCountSourceFeaturesAsync(
        GeoservicesImportRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _restClient.QueryFeatureCountAsync(
                request.ServiceUrl,
                request.LayerId,
                request.WhereClause,
                request.RequestTimeoutSeconds,
                request.MaxRetries,
                cancellationToken,
                request.Credentials).ConfigureAwait(false);
        }
        // Intentionally generic: an unreadable count is an evidence gap, not an import failure. The
        // fidelity verdict records it as unverified.
        catch (Exception ex) when (IsUnreadableSourceFailure(ex, cancellationToken))
        {
            Log.SourcePopulationUnavailable(_logger, request.TableName, ex);
            return null;
        }
    }

    /// <summary>
    /// Only the caller's own cancellation propagates. A per-request timeout also surfaces as
    /// <see cref="OperationCanceledException"/> (the REST client's linked <c>CancelAfter</c>) and, like
    /// any other source failure, only leaves the source snapshot unverified.
    /// </summary>
    private static bool IsUnreadableSourceFailure(Exception exception, CancellationToken cancellationToken)
        => exception is not OutOfMemoryException
            && !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested);

    /// <summary>
    /// Reads the source population again after the last page. The object-ID window path compares the
    /// object-ID sets it enumerated; the paged path compares filtered counts.
    /// </summary>
    private async Task<MigrationFidelitySourceSnapshotInput> CaptureSourceSnapshotAsync(
        GeoservicesImportRequest request,
        long[]? sourceObjectIds,
        long? countBeforeTransfer,
        CancellationToken cancellationToken)
    {
        if (sourceObjectIds is null)
        {
            return new MigrationFidelitySourceSnapshotInput
            {
                CountBeforeTransfer = countBeforeTransfer,
                CountAfterTransfer = await TryCountSourceFeaturesAsync(request, cancellationToken).ConfigureAwait(false)
            };
        }

        long[]? objectIdsAfterTransfer;
        try
        {
            objectIdsAfterTransfer = await _restClient.QueryObjectIdsAsync(
                request.ServiceUrl,
                request.LayerId,
                request.WhereClause,
                request.RequestTimeoutSeconds,
                request.MaxRetries,
                cancellationToken,
                request.Credentials).ConfigureAwait(false);
        }
        // Intentionally generic, as in TryCountSourceFeaturesAsync.
        catch (Exception ex) when (IsUnreadableSourceFailure(ex, cancellationToken))
        {
            Log.SourcePopulationUnavailable(_logger, request.TableName, ex);
            objectIdsAfterTransfer = null;
        }

        // Sorted in place and compared span by span, so no set is built over either list. The transfer
        // windows were cut from copies, so reordering the enumerated source list here is safe.
        var membershipChanged = false;
        if (objectIdsAfterTransfer is not null)
        {
            Array.Sort(sourceObjectIds);
            Array.Sort(objectIdsAfterTransfer);
            membershipChanged = !sourceObjectIds.AsSpan().SequenceEqual(objectIdsAfterTransfer);
        }

        return new MigrationFidelitySourceSnapshotInput
        {
            CountBeforeTransfer = sourceObjectIds.Length,
            CountAfterTransfer = objectIdsAfterTransfer?.Length,
            MembershipChanged = membershipChanged
        };
    }

    private async Task CreateTableAsync(
        NpgsqlConnection connection,
        string schemaName,
        string tableName,
        GeoservicesLayerInfo layerInfo,
        int targetSrid,
        CancellationToken cancellationToken)
    {
        var createSql = BuildCreateTableSql(schemaName, tableName, layerInfo, targetSrid);
        await using var createCmd = connection.CreateCommand();
        createCmd.CommandText = createSql;
        await createCmd.ExecuteNonQueryAsync(cancellationToken);

        Log.TableCreated(_logger, tableName);
    }

    private static string BuildCreateTableSql(string schemaName, string tableName, GeoservicesLayerInfo layerInfo, int targetSrid)
    {
        var columns = new List<string>
        {
            $"{FieldNames.ObjectId} BIGSERIAL PRIMARY KEY"
        };

        // Add attribute fields (objectid/geometry fields are handled separately above/below).
        foreach (var field in layerInfo.Fields.Where(field => !field.IsObjectId && !IsGeometryField(field)))
        {
            var pgType = MapEsriTypeToPgType(field.Type, field.Length);
            columns.Add($"\"{field.Name.SanitizeFieldName()}\" {pgType}");
        }

        // Add geometry column if the layer has geometry
        if (!string.IsNullOrEmpty(layerInfo.GeometryType))
        {
            var pgGeomType = MapEsriGeometryType(layerInfo.GeometryType);
            columns.Add($"geom geometry({pgGeomType}{GetGeometryDimensionSuffix(layerInfo.HasZ, layerInfo.HasM)}, {targetSrid})");
        }

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"CREATE SCHEMA IF NOT EXISTS {QuoteIdentifier(schemaName)};");
        sb.AppendLine(CultureInfo.InvariantCulture, $"CREATE TABLE {QuoteIdentifier(schemaName)}.{QuoteIdentifier(tableName)} (");

        for (var i = 0; i < columns.Count; i++)
        {
            var suffix = i == columns.Count - 1 ? string.Empty : ",";
            sb.AppendLine(CultureInfo.InvariantCulture, $"    {columns[i]}{suffix}");
        }

        sb.AppendLine(");");
        return sb.ToString();
    }

    private static string MapEsriTypeToPgType(string esriType, int? length)
    {
        return esriType.ToPostgresType(length);
    }

    /// <summary>
    /// Maps an Esri geometry-type token to the PostGIS geometry-column type. Shared with
    /// <see cref="GeoservicesLayerPublicationService"/> so AutoPublish and table creation agree.
    /// </summary>
    internal static string MapEsriGeometryType(string esriGeometryType, bool hasZ = false, bool hasM = false)
    {
        var geometryType = esriGeometryType.ToUpperInvariant() switch
        {
            "ESRIGEOMETRYPOINT" => "POINT",
            "ESRIGEOMETRYMULTIPOINT" => "MULTIPOINT",
            "ESRIGEOMETRYPOLYLINE" => "MULTILINESTRING",
            "ESRIGEOMETRYPOLYGON" => "MULTIPOLYGON",
            "ESRIGEOMETRYENVELOPE" => "POLYGON",
            _ => "GEOMETRY"
        };

        return geometryType + GetGeometryDimensionSuffix(hasZ, hasM);
    }

    private static string GetGeometryDimensionSuffix(bool hasZ, bool hasM)
        => hasZ && hasM ? "ZM" : hasZ ? "Z" : hasM ? "M" : string.Empty;

    private static ArcGisFeature[] FilterUnseenFeatures(
        ArcGisFeature[] features,
        GeoservicesLayerInfo layerInfo,
        HashSet<long> seenObjectIds,
        IReadOnlyCollection<long>? allowedObjectIds)
    {
        var objectIdField = layerInfo.Fields.FirstOrDefault(static field => field.IsObjectId)?.Name;
        if (objectIdField is null)
        {
            return features;
        }

        var newFeatures = new List<ArcGisFeature>(features.Length);
        var allowed = allowedObjectIds is null ? null : allowedObjectIds.ToHashSet();
        foreach (var feature in features)
        {
            if (!TryReadSourceObjectId(feature, objectIdField, out var objectId))
            {
                newFeatures.Add(feature);
                continue;
            }

            if ((allowed is null || allowed.Contains(objectId)) && seenObjectIds.Add(objectId))
            {
                newFeatures.Add(feature);
            }
        }

        return newFeatures.ToArray();
    }

    /// <summary>
    /// True when the source field is the Esri geometry field (excluded from attribute columns).
    /// Shared with <see cref="GeoservicesLayerPublicationService"/> so the reconciliation field set
    /// matches the imported column set.
    /// </summary>
    internal static bool IsGeometryField(GeoservicesFieldInfo field)
        => field.Type.Equals("esriFieldTypeGeometry", StringComparison.OrdinalIgnoreCase);
}
