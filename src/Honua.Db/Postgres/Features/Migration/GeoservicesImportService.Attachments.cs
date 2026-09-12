// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;

namespace Honua.Db.Postgres.Features.Migration;

internal sealed partial class GeoservicesImportService
{
    private const int AttachmentQueryBatchSize = 50;

    /// <summary>
    /// Copies the source layer's attachments into the Honua attachment store and reports the
    /// attachment inventory independently of the feature counts (issue #4600). The advertised total
    /// is what the source said exists; <c>UnverifiedParents</c> counts imported features whose
    /// attachment inventory could not be read at all, so the fidelity gate can tell "no attachments"
    /// apart from "we never found out".
    /// </summary>
    private async Task<AttachmentCopyOutcome> CopyAttachmentsAsync(
        GeoservicesImportRequest request,
        GeoservicesLayerInfo layerInfo,
        int publishedLayerId,
        Dictionary<long, long> objectIdMap,
        List<string> warnings,
        IProgress<GeoservicesImportProgress>? progress,
        string jobId,
        DateTimeOffset startedAt,
        int featuresProcessed,
        CancellationToken cancellationToken)
    {
        if (_attachmentStore == null || objectIdMap.Count == 0)
        {
            return default;
        }

        Log.AttachmentCopyStarting(_logger, request.LayerId, objectIdMap.Count);
        ReportProgress(
            progress,
            jobId,
            startedAt,
            GeoservicesImportStatus.CopyingAttachments,
            request,
            "Copying source attachments",
            featuresProcessed,
            featuresProcessed,
            layerInfo.Name,
            publishedLayerId,
            attachmentsProcessed: 0,
            failedAttachments: 0);

        var attachmentsCopied = 0;
        var failedAttachments = 0;
        var advertisedAttachments = 0;
        var unverifiedParents = 0;

        // Stable batches of source ObjectIds keep attachment-group ordering deterministic for tests.
        var sourceObjectIds = objectIdMap.Keys
            .OrderBy(static value => value)
            .ToArray();

        for (var batchStart = 0; batchStart < sourceObjectIds.Length; batchStart += AttachmentQueryBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batchLength = Math.Min(AttachmentQueryBatchSize, sourceObjectIds.Length - batchStart);
            var batch = new long[batchLength];
            Array.Copy(sourceObjectIds, batchStart, batch, 0, batchLength);

            ArcGisAttachmentQueryResponse queryResponse;
            try
            {
                queryResponse = await _restClient.QueryAttachmentsAsync(
                    request.ServiceUrl,
                    request.LayerId,
                    batch,
                    request.RequestTimeoutSeconds,
                    request.MaxRetries,
                    cancellationToken,
                    request.Credentials).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            // Intentionally broad: per-batch failure; log, record a warning for the import result,
            // and skip just this batch of parent features rather than aborting the whole
            // attachment copy.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.AttachmentQueryBatchFailed(_logger, request.LayerId, batch.Length, ex);
                // #4600: the advertised attachment count for these parents is now unknowable, so the
                // attachment check cannot be reported as passing. Record the parents as unverified
                // rather than letting the run imply they simply had no attachments.
                unverifiedParents += batch.Length;
                warnings.Add(
                    $"Attachment metadata query failed for {batch.Length} parent features; "
                    + "this batch of attachments was skipped.");
                continue;
            }

            if (queryResponse.AttachmentGroups == null || queryResponse.AttachmentGroups.Length == 0)
            {
                continue;
            }

            foreach (var group in queryResponse.AttachmentGroups)
            {
                if (group.AttachmentInfos == null || group.AttachmentInfos.Length == 0)
                {
                    continue;
                }

                advertisedAttachments += group.AttachmentInfos.Length;

                if (!objectIdMap.TryGetValue(group.ParentObjectId, out var honuaFeatureId))
                {
                    // Parent feature was not inserted (filtered out or insert failed). Count
                    // every advertised attachment as failed so reconciliation surfaces it.
                    failedAttachments += group.AttachmentInfos.Length;
                    warnings.Add(
                        $"Source feature {group.ParentObjectId} has "
                        + $"{group.AttachmentInfos.Length} attachment(s) but no matching imported feature.");
                    continue;
                }

                foreach (var attachmentInfo in group.AttachmentInfos)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        await using var download = await _restClient.DownloadAttachmentAsync(
                            request.ServiceUrl,
                            request.LayerId,
                            group.ParentObjectId,
                            attachmentInfo.Id,
                            request.RequestTimeoutSeconds,
                            request.MaxRetries,
                            cancellationToken,
                            request.Credentials).ConfigureAwait(false);

                        var filename = string.IsNullOrWhiteSpace(attachmentInfo.Name)
                            ? $"attachment-{attachmentInfo.Id}"
                            : attachmentInfo.Name!;
                        var contentType = string.IsNullOrWhiteSpace(attachmentInfo.ContentType)
                            ? download.ContentType
                            : attachmentInfo.ContentType!;

                        await _attachmentStore!.UploadAsync(
                            publishedLayerId,
                            honuaFeatureId,
                            filename,
                            contentType,
                            download.Content,
                            attachmentInfo.Keywords,
                            cancellationToken).ConfigureAwait(false);

                        attachmentsCopied++;

                        ReportProgress(
                            progress,
                            jobId,
                            startedAt,
                            GeoservicesImportStatus.CopyingAttachments,
                            request,
                            $"Copied attachment {attachmentsCopied}",
                            featuresProcessed,
                            featuresProcessed,
                            layerInfo.Name,
                            publishedLayerId,
                            attachmentsProcessed: attachmentsCopied,
                            failedAttachments: failedAttachments);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    // Intentionally broad: per-attachment failure; log and count it as failed,
                    // letting the rest of the batch's attachments continue copying.
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        failedAttachments++;
                        Log.AttachmentCopyFailed(
                            _logger,
                            attachmentInfo.Id,
                            group.ParentObjectId,
                            request.LayerId,
                            ex);
                    }
                }
            }
        }

        Log.AttachmentCopyCompleted(_logger, request.LayerId, attachmentsCopied, failedAttachments);

        if (failedAttachments > 0)
        {
            warnings.Add(
                $"Attachment copy completed with {failedAttachments} failure(s); the import succeeded "
                + "but some attachments are missing from the target store.");
        }

        return new AttachmentCopyOutcome
        {
            Copied = attachmentsCopied,
            Failed = failedAttachments,
            Advertised = advertisedAttachments,
            UnverifiedParents = unverifiedParents
        };
    }

    /// <summary>
    /// Attachment-copy accounting for one import run. Kept separate from the feature counters so
    /// attachment parity is reconciled on its own evidence (issue #4600 acceptance criterion 6).
    /// </summary>
    internal readonly record struct AttachmentCopyOutcome
    {
        /// <summary>Attachments whose bytes reached the Honua attachment store.</summary>
        public int Copied { get; init; }

        /// <summary>Advertised attachments that could not be copied.</summary>
        public int Failed { get; init; }

        /// <summary>Attachments the source advertised across every parent feature that was probed.</summary>
        public int Advertised { get; init; }

        /// <summary>Imported features whose source attachment inventory could not be read.</summary>
        public int UnverifiedParents { get; init; }
    }
}
