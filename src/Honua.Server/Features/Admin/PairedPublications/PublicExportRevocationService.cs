// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.SharedCaching;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Features.Admin.PairedPublications;

// Durable revocation state machine for public Exports:
// Requested -> ObjectsDeleted -> CatalogRewritten -> InvalidationCreated(id) -> Completed.
// Persisted before every step so a crash between any two steps resumes cleanly on restart.
internal sealed partial class PublicExportRevocationService(
    IPublicExportStateStore stateStore,
    PublicExportEligibility eligibility,
    IMetadataV2GraphProvider graphProvider,
    CdnInvalidationService cdnInvalidation,
    IPublicExportOpsNotifier opsNotifier,
    IConfiguration configuration,
    ILogger<PublicExportRevocationService> logger,
    IPublicPublicationDestination? destination = null,
    ICdnInvalidationStatusReader? statusReader = null,
    IHttpContextAccessor? httpContextAccessor = null,
    TimeProvider? timeProvider = null) : IPublicExportAccessChangeListener
{
    internal const string ReasonAccessRestricted = "access-restricted";
    internal const string ReasonUnpublished = "unpublished";
    internal const string DistributionIdSetting = "SharedCache:CloudFront:DistributionId";

    internal static readonly TimeSpan PollTimeout = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan[] RetryBackoffs =
    [
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15)
    ];

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task OnAdministrativeAccessChangeAsync(
        string? serviceId,
        IReadOnlyCollection<int>? layerIds,
        CancellationToken cancellationToken = default)
    {
        if (destination is null)
        {
            return;
        }

        var promoted = await stateStore.ListPromotedAsync(cancellationToken).ConfigureAwait(false);
        if (promoted.Count == 0)
        {
            return;
        }

        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var changeId = ResolveCurrentChangeId();

        foreach (var export in promoted)
        {
            if (!MatchesScope(export, serviceId, layerIds))
            {
                continue;
            }

            if (await StillEligibleAsync(export, snapshot, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            await RequestRevocationAsync(export.PublicationId, ReasonAccessRestricted, changeId, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public Task<PublicExportRevocationRecord> RevokeUnpublishedAsync(
        string publicationId,
        string? changeId,
        CancellationToken cancellationToken)
        => RequestRevocationAsync(
            publicationId,
            ReasonUnpublished,
            string.IsNullOrWhiteSpace(changeId) ? ResolveCurrentChangeId() : changeId,
            cancellationToken);

    public async Task<PublicExportRevocationRecord> RequestRevocationAsync(
        string publicationId,
        string reason,
        string changeId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var pending = await stateStore.ListPendingRevocationsAsync(cancellationToken).ConfigureAwait(false);
        var existing = pending.FirstOrDefault(item =>
            string.Equals(item.PublicationId, publicationId, StringComparison.Ordinal));
        if (existing is not null)
        {
            return await AdvanceAsync(existing, cancellationToken).ConfigureAwait(false);
        }

        var now = _timeProvider.GetUtcNow();
        var record = new PublicExportRevocationRecord
        {
            RevocationId = $"rev-{Guid.NewGuid():N}",
            PublicationId = publicationId,
            Reason = reason,
            ChangeId = string.IsNullOrWhiteSpace(changeId) ? CdnPurgeCause.NoRequest : changeId,
            State = PublicExportRevocationState.Requested,
            CreatedAt = now,
            UpdatedAt = now
        };
        await stateStore.SaveRevocationAsync(record, cancellationToken).ConfigureAwait(false);
        return await AdvanceAsync(record, cancellationToken).ConfigureAwait(false);
    }

    public async Task ResumePendingAsync(CancellationToken cancellationToken)
    {
        if (destination is null)
        {
            return;
        }

        var pending = await stateStore.ListPendingRevocationsAsync(cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        foreach (var record in pending)
        {
            if (record.NextAttemptAt is { } next && next > now)
            {
                continue;
            }

            await AdvanceAsync(record, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<PublicExportRevocationRecord> AdvanceAsync(
        PublicExportRevocationRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (destination is null || record.State == PublicExportRevocationState.Completed)
        {
            return record;
        }

        var current = record;
        try
        {
            if (current.State == PublicExportRevocationState.Requested)
            {
                current = await DeleteObjectsAsync(current, cancellationToken).ConfigureAwait(false);
            }

            if (current.State == PublicExportRevocationState.ObjectsDeleted)
            {
                current = await RewriteCatalogAsync(current, cancellationToken).ConfigureAwait(false);
            }

            if (current.State == PublicExportRevocationState.CatalogRewritten)
            {
                current = await CreateInvalidationAsync(current, cancellationToken).ConfigureAwait(false);
            }

            if (current.State == PublicExportRevocationState.InvalidationCreated)
            {
                current = await PollInvalidationAsync(current, cancellationToken).ConfigureAwait(false);
            }

            return current;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogRevocationStepFailed(logger, current.RevocationId, current.PublicationId, current.State, exception);
            return await RecordFailureAsync(current, exception.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<PublicExportRevocationRecord> DeleteObjectsAsync(
        PublicExportRevocationRecord record,
        CancellationToken cancellationToken)
    {
        await destination!.DeletePublicationAsync(record.PublicationId, cancellationToken).ConfigureAwait(false);
        await stateStore.DeletePromotedAsync(record.PublicationId, cancellationToken).ConfigureAwait(false);

        var updated = record with
        {
            State = PublicExportRevocationState.ObjectsDeleted,
            UpdatedAt = _timeProvider.GetUtcNow(),
            NextAttemptAt = null,
            LastError = null
        };
        await stateStore.SaveRevocationAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private async Task<PublicExportRevocationRecord> RewriteCatalogAsync(
        PublicExportRevocationRecord record,
        CancellationToken cancellationToken)
    {
        var remaining = (await stateStore.ListPromotedAsync(cancellationToken).ConfigureAwait(false))
            .Where(item => !string.Equals(item.PublicationId, record.PublicationId, StringComparison.Ordinal))
            .ToArray();
        var bytes = PublicExportCatalogDocument.Serialize(remaining);
        using var stream = new MemoryStream(bytes, writable: false);
        await destination!.PutRootCatalogAsync(stream, cancellationToken).ConfigureAwait(false);

        var updated = record with
        {
            State = PublicExportRevocationState.CatalogRewritten,
            UpdatedAt = _timeProvider.GetUtcNow(),
            NextAttemptAt = null,
            LastError = null
        };
        await stateStore.SaveRevocationAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private async Task<PublicExportRevocationRecord> CreateInvalidationAsync(
        PublicExportRevocationRecord record,
        CancellationToken cancellationToken)
    {
        string[] paths =
        [
            $"/{PublicPublicationDestination.PublicationPrefix(record.PublicationId)}*",
            $"/{PublicPublicationDestination.RootCatalogKey}"
        ];
        var result = await cdnInvalidation.InvalidateNowAsync(
            paths,
            cancellationToken,
            CdnPurgeCause.Admin(record.ChangeId)).ConfigureAwait(false);

        if (result.Outcome == CdnInvalidationOutcome.NotConfigured)
        {
            return await MarkCompletedAsync(record, cancellationToken).ConfigureAwait(false);
        }

        if (result.Outcome != CdnInvalidationOutcome.Submitted || string.IsNullOrWhiteSpace(result.Detail))
        {
            var detail = $"CDN invalidation returned {result.Outcome}" +
                (string.IsNullOrWhiteSpace(result.Detail) ? string.Empty : $" ({result.Detail})");
            return await RecordFailureAsync(record, detail, cancellationToken).ConfigureAwait(false);
        }

        var distributionId = configuration[DistributionIdSetting]?.Trim();
        var updated = record with
        {
            State = PublicExportRevocationState.InvalidationCreated,
            InvalidationId = result.Detail,
            DistributionId = string.IsNullOrWhiteSpace(distributionId) ? null : distributionId,
            UpdatedAt = _timeProvider.GetUtcNow(),
            NextAttemptAt = null,
            LastError = null
        };
        await stateStore.SaveRevocationAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private async Task<PublicExportRevocationRecord> PollInvalidationAsync(
        PublicExportRevocationRecord record,
        CancellationToken cancellationToken)
    {
        if (statusReader is null || string.IsNullOrWhiteSpace(record.InvalidationId) || string.IsNullOrWhiteSpace(record.DistributionId))
        {
            return await MarkCompletedAsync(record, cancellationToken).ConfigureAwait(false);
        }

        var status = await statusReader.ReadAsync(
            new CdnInvalidationReceipt(record.DistributionId, record.InvalidationId),
            cancellationToken).ConfigureAwait(false);

        if (status.Status is CdnInvalidationStatus.Completed or CdnInvalidationStatus.NotConfigured)
        {
            return await MarkCompletedAsync(record, cancellationToken).ConfigureAwait(false);
        }

        if (status.Status == CdnInvalidationStatus.Pending)
        {
            var now = _timeProvider.GetUtcNow();
            if (now - record.CreatedAt >= PollTimeout)
            {
                return await RecordFailureAsync(record, "CDN invalidation did not complete within the expected window.", cancellationToken)
                    .ConfigureAwait(false);
            }

            var deferred = record with
            {
                AttemptCount = record.AttemptCount + 1,
                UpdatedAt = now,
                NextAttemptAt = now + NextBackoff(record.AttemptCount)
            };
            await stateStore.SaveRevocationAsync(deferred, cancellationToken).ConfigureAwait(false);
            return deferred;
        }

        var detail = string.IsNullOrWhiteSpace(status.Detail)
            ? "CDN invalidation status check failed."
            : $"CDN invalidation status check failed ({status.Detail}).";
        return await RecordFailureAsync(record, detail, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PublicExportRevocationRecord> MarkCompletedAsync(
        PublicExportRevocationRecord record,
        CancellationToken cancellationToken)
    {
        var completed = record with
        {
            State = PublicExportRevocationState.Completed,
            UpdatedAt = _timeProvider.GetUtcNow(),
            NextAttemptAt = null,
            LastError = null
        };
        await stateStore.SaveRevocationAsync(completed, cancellationToken).ConfigureAwait(false);
        return completed;
    }

    private async Task<PublicExportRevocationRecord> RecordFailureAsync(
        PublicExportRevocationRecord record,
        string detail,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var failed = record with
        {
            AttemptCount = record.AttemptCount + 1,
            UpdatedAt = now,
            NextAttemptAt = now + NextBackoff(record.AttemptCount),
            LastError = detail
        };
        await stateStore.SaveRevocationAsync(failed, cancellationToken).ConfigureAwait(false);
        await opsNotifier.NotifyRevocationFailedAsync(failed, detail, cancellationToken).ConfigureAwait(false);
        return failed;
    }

    private async Task<bool> StillEligibleAsync(
        PromotedExportRecord export,
        Honua.Core.Features.Metadata.Domain.V2.MetadataV2GraphSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (!snapshot.Index.ServicesById.TryGetValue(export.ServiceId, out var service) ||
            !snapshot.Index.ResourcesById.TryGetValue(export.ResourceId, out var resource))
        {
            return false;
        }

        var result = await eligibility.EvaluateAsync(resource, service, cancellationToken).ConfigureAwait(false);
        return result.IsEligible;
    }

    private static bool MatchesScope(
        PromotedExportRecord export,
        string? serviceId,
        IReadOnlyCollection<int>? layerIds)
    {
        if (string.IsNullOrWhiteSpace(serviceId) && (layerIds is null || layerIds.Count == 0))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(serviceId) &&
            string.Equals(export.ServiceId, serviceId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return layerIds is { Count: > 0 } && layerIds.Contains(export.StorageLayerId);
    }

    private string ResolveCurrentChangeId()
        => httpContextAccessor?.HttpContext is { } context
            ? AuditContextResolver.ResolveCorrelationId(context)
            : CdnPurgeCause.NoRequest;

    internal static TimeSpan NextBackoff(int attemptCount)
        => RetryBackoffs[Math.Clamp(attemptCount, 0, RetryBackoffs.Length - 1)];

    [LoggerMessage(EventId = 10340, Level = LogLevel.Warning,
        Message = "Public export revocation {RevocationId} for {PublicationId} failed at step {State}; retrying.")]
    private static partial void LogRevocationStepFailed(
        ILogger logger, string revocationId, string publicationId, PublicExportRevocationState state, Exception exception);
}
