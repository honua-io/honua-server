// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;

namespace Honua.Postgres.Features.Migration;

internal sealed partial class GeoservicesImportService
{
    private const int AttachmentQueryBatchSize = 50;

    private async Task<(int Copied, int Failed)> CopyAttachmentsAsync(
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
            return (0, 0);
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
            catch (Exception ex)
            {
                Log.AttachmentQueryBatchFailed(_logger, request.LayerId, batch.Length, ex);
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
                    catch (Exception ex)
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

        return (attachmentsCopied, failedAttachments);
    }
}
