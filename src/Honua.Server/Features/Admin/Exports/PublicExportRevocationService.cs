// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.SharedCaching;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Features.Admin.Exports;

// Durable revocation state machine for public Exports:
// Requested -> ObjectsDeleted -> CatalogRewritten -> InvalidationCreated(id) -> Completed.
// Persisted before every step so a crash between any two steps resumes cleanly on restart.
internal sealed partial class PublicExportRevocationService(
    PublicExportRevocationDependencies deps,
    ILogger<PublicExportRevocationService> logger,
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
        if (deps.Destination is null)
        {
            return;
        }

        var promoted = await deps.StateStore.ListPromotedAsync(cancellationToken).ConfigureAwait(false);
        if (promoted.Count == 0)
        {
            return;
        }

        var snapshot = await deps.GraphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var changeId = ResolveCurrentChangeId();

        foreach (var export in promoted)
        {
            if (await StillEligibleAsync(export, snapshot, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            await RequestRevocationAsync(
                new PublicExportRevocationRequest(export.PublicationId, ReasonAccessRestricted, changeId),
                cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<PublicExportRevocationRecord> RevokeUnpublishedAsync(
        string publicationId,
        string? changeId,
        CancellationToken cancellationToken)
        => RequestRevocationAsync(
            new PublicExportRevocationRequest(
                publicationId,
                ReasonUnpublished,
                string.IsNullOrWhiteSpace(changeId) ? ResolveCurrentChangeId() : changeId),
            cancellationToken);

    public async Task<PublicExportRevocationRecord> RequestRevocationAsync(
        PublicExportRevocationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PublicationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Reason);

        var pending = await deps.StateStore.ListPendingRevocationsAsync(cancellationToken).ConfigureAwait(false);
        var existing = pending.FirstOrDefault(item =>
            string.Equals(item.PublicationId, request.PublicationId, StringComparison.Ordinal));
        if (existing is not null)
        {
            return await AdvanceAsync(existing, cancellationToken).ConfigureAwait(false);
        }

        var now = _timeProvider.GetUtcNow();
        var record = new PublicExportRevocationRecord
        {
            RevocationId = $"rev-{Guid.NewGuid():N}",
            PublicationId = request.PublicationId,
            Reason = request.Reason,
            ChangeId = string.IsNullOrWhiteSpace(request.ChangeId) ? CdnPurgeCause.NoRequest : request.ChangeId,
            State = PublicExportRevocationState.Requested,
            CreatedAt = now,
            UpdatedAt = now
        };
        await deps.StateStore.SaveRevocationAsync(record, cancellationToken).ConfigureAwait(false);
        return await AdvanceAsync(record, cancellationToken).ConfigureAwait(false);
    }

    public async Task ResumePendingAsync(CancellationToken cancellationToken)
    {
        if (deps.Destination is null)
        {
            return;
        }

        var pending = await deps.StateStore.ListPendingRevocationsAsync(cancellationToken).ConfigureAwait(false);
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
        if (deps.Destination is null || record.State == PublicExportRevocationState.Completed)
        {
            return record;
        }

        var current = record;
        try
        {
            while (current.State != PublicExportRevocationState.Completed)
            {
                var next = await ExecuteNextStepAsync(current, cancellationToken).ConfigureAwait(false);
                if (next.State == current.State)
                {
                    return next;
                }

                current = next;
            }

            return current;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogRevocationStepFailed(logger, current.RevocationId, current.PublicationId, current.State, exception);
            return await RecordFailureAsync(current, exception.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task<PublicExportRevocationRecord> ExecuteNextStepAsync(
        PublicExportRevocationRecord current,
        CancellationToken cancellationToken)
        => current.State switch
        {
            PublicExportRevocationState.Requested => DeleteObjectsAsync(current, cancellationToken),
            PublicExportRevocationState.ObjectsDeleted => RewriteCatalogAsync(current, cancellationToken),
            PublicExportRevocationState.CatalogRewritten => CreateInvalidationAsync(current, cancellationToken),
            PublicExportRevocationState.InvalidationCreated => PollInvalidationAsync(current, cancellationToken),
            _ => Task.FromResult(current)
        };

    private async Task<PublicExportRevocationRecord> DeleteObjectsAsync(
        PublicExportRevocationRecord record,
        CancellationToken cancellationToken)
    {
        await deps.Destination!.DeletePublicationAsync(record.PublicationId, cancellationToken).ConfigureAwait(false);
        await deps.StateStore.DeletePromotedAsync(record.PublicationId, cancellationToken).ConfigureAwait(false);

        var updated = record with
        {
            State = PublicExportRevocationState.ObjectsDeleted,
            UpdatedAt = _timeProvider.GetUtcNow(),
            NextAttemptAt = null,
            LastError = null
        };
        await deps.StateStore.SaveRevocationAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private async Task<PublicExportRevocationRecord> RewriteCatalogAsync(
        PublicExportRevocationRecord record,
        CancellationToken cancellationToken)
    {
        var remaining = (await deps.StateStore.ListPromotedAsync(cancellationToken).ConfigureAwait(false))
            .Where(item => !string.Equals(item.PublicationId, record.PublicationId, StringComparison.Ordinal))
            .ToArray();
        var bytes = PublicExportCatalogDocument.Serialize(remaining);
        using var stream = new MemoryStream(bytes, writable: false);
        await deps.Destination!.PutRootCatalogAsync(stream, cancellationToken).ConfigureAwait(false);

        var updated = record with
        {
            State = PublicExportRevocationState.CatalogRewritten,
            UpdatedAt = _timeProvider.GetUtcNow(),
            NextAttemptAt = null,
            LastError = null
        };
        await deps.StateStore.SaveRevocationAsync(updated, cancellationToken).ConfigureAwait(false);
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
        var result = await deps.CdnInvalidation.InvalidateNowAsync(
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

        var distributionId = deps.Configuration[DistributionIdSetting]?.Trim();
        var updated = record with
        {
            State = PublicExportRevocationState.InvalidationCreated,
            InvalidationId = result.Detail,
            DistributionId = string.IsNullOrWhiteSpace(distributionId) ? null : distributionId,
            UpdatedAt = _timeProvider.GetUtcNow(),
            NextAttemptAt = null,
            LastError = null
        };
        await deps.StateStore.SaveRevocationAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private async Task<PublicExportRevocationRecord> PollInvalidationAsync(
        PublicExportRevocationRecord record,
        CancellationToken cancellationToken)
    {
        if (deps.StatusReader is null || string.IsNullOrWhiteSpace(record.InvalidationId) || string.IsNullOrWhiteSpace(record.DistributionId))
        {
            return await MarkCompletedAsync(record, cancellationToken).ConfigureAwait(false);
        }

        var status = await deps.StatusReader.ReadAsync(
            new CdnInvalidationReceipt(record.DistributionId, record.InvalidationId),
            cancellationToken).ConfigureAwait(false);

        if (status.Status is CdnInvalidationStatus.Completed or CdnInvalidationStatus.NotConfigured)
        {
            return await MarkCompletedAsync(record, cancellationToken).ConfigureAwait(false);
        }

        if (status.Status == CdnInvalidationStatus.Pending)
        {
            return await HandlePendingPollAsync(record, cancellationToken).ConfigureAwait(false);
        }

        var detail = string.IsNullOrWhiteSpace(status.Detail)
            ? "CDN invalidation status check failed."
            : $"CDN invalidation status check failed ({status.Detail}).";
        return await RecordFailureAsync(record, detail, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PublicExportRevocationRecord> HandlePendingPollAsync(
        PublicExportRevocationRecord record,
        CancellationToken cancellationToken)
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
        await deps.StateStore.SaveRevocationAsync(deferred, cancellationToken).ConfigureAwait(false);
        return deferred;
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
        await deps.StateStore.SaveRevocationAsync(completed, cancellationToken).ConfigureAwait(false);
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
        await deps.StateStore.SaveRevocationAsync(failed, cancellationToken).ConfigureAwait(false);
        await deps.OpsNotifier.NotifyRevocationFailedAsync(failed, detail, cancellationToken).ConfigureAwait(false);
        return failed;
    }

    private async Task<bool> StillEligibleAsync(
        PromotedExportRecord export,
        MetadataV2GraphSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var target = new PublicExportLayerTarget(export.ServiceId, export.ResourceId);
        var result = await deps.Eligibility.EvaluateSnapshotAsync(
            snapshot, target, cancellationToken).ConfigureAwait(false);
        return result.IsEligible;
    }

    private string ResolveCurrentChangeId()
        => deps.HttpContextAccessor?.HttpContext is { } context
            ? AuditContextResolver.ResolveCorrelationId(context)
            : CdnPurgeCause.NoRequest;

    internal static TimeSpan NextBackoff(int attemptCount)
        => RetryBackoffs[Math.Clamp(attemptCount, 0, RetryBackoffs.Length - 1)];

    [LoggerMessage(EventId = 10340, Level = LogLevel.Warning,
        Message = "Public export revocation {RevocationId} for {PublicationId} failed at step {State}; retrying.")]
    private static partial void LogRevocationStepFailed(
        ILogger logger, string revocationId, string publicationId, PublicExportRevocationState state, Exception exception);
}
