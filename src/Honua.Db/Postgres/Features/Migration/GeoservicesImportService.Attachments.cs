// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Db.Postgres.Features.Attachments;

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
        var importedStore = _attachmentStore as IImportedAttachmentStore;
        if (importedStore == null)
        {
            warnings.Add("The attachment store does not support source identity reconciliation; attachment copying was skipped.");
        }

        var generation = Guid.NewGuid();
        // Credentials are deliberately excluded; source identity survives token rotation.
        var serviceUri = new UriBuilder(request.ServiceUrl)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        }.Uri;
        var source = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{serviceUri.GetLeftPart(UriPartial.Path).TrimEnd('/')}:{request.LayerId}")));

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
        var targetUnverified = importedStore == null;

        // Stable batches of source ObjectIds keep attachment-group ordering deterministic for tests.
        var sourceObjectIds = objectIdMap.Keys
            .OrderBy(static value => value)
            .ToArray();

        for (var batchStart = 0; layerInfo.HasAttachments && batchStart < sourceObjectIds.Length; batchStart += AttachmentQueryBatchSize)
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
                        if (importedStore == null)
                        {
                            throw new InvalidOperationException("Source identity reconciliation is unavailable.");
                        }

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

                        await importedStore.UploadImportedAsync(
                            publishedLayerId,
                            honuaFeatureId,
                            source,
                            group.ParentObjectId,
                            attachmentInfo.Id,
                            generation,
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

        // A partial inventory or copy must never delete the prior imported set. The next
        // complete retry upserts the same source identities, then retires unseen attachments,
        // including attachments whose parent disappeared and a newly empty source inventory.
        if (importedStore != null && failedAttachments == 0 && unverifiedParents == 0)
        {
            try
            {
                if (await importedStore.CompleteImportAsync(publishedLayerId, generation, cancellationToken).ConfigureAwait(false))
                {
                    // Old imports have no provenance and cannot be distinguished from files
                    // added through Honua. Preserve them and require an ownership review.
                    targetUnverified = true;
                    warnings.Add("Untracked attachments were preserved. They may include legacy imported attachments; "
                        + "verify their ownership and remove obsolete legacy copies before accepting attachment fidelity.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                targetUnverified = true;
                Log.AttachmentQueryBatchFailed(_logger, request.LayerId, objectIdMap.Count, ex);
                warnings.Add("Imported attachment reconciliation failed; prior attachments may remain and fidelity requires review.");
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
            UnverifiedParents = unverifiedParents,
            TargetUnverified = targetUnverified
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

        /// <summary>Retained untracked rows or failed reconciliation prevent verifying the target set.</summary>
        public bool TargetUnverified { get; init; }
    }
}
