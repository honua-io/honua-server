// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Infrastructure.Middleware;

namespace Honua.Infrastructure.SharedCaching;

internal sealed partial class CdnInvalidationService(
    ICdnInvalidator invalidator,
    TimeProvider timeProvider,
    ILogger<CdnInvalidationService> logger,
    IHttpContextAccessor? httpContextAccessor = null)
{
    internal const string ResultHeader = "Honua-Cdn-Invalidation";

    // Edits cost at most one invalidation per window on a node, whatever the number of edits or layers; the lifetime is the backstop.
    internal static readonly TimeSpan CoalescingWindow = TimeSpan.FromSeconds(30);

    // Above this many wildcard paths one invalidation of everything is cheaper for the CDN than naming them.
    internal const int MaxWildcardPathsPerInvalidation = 6;

    internal static readonly TimeSpan[] BusyRetryDelays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    private readonly Lock _gate = new();
    private readonly HashSet<string> _pendingEditPaths = new(StringComparer.Ordinal);
    private DateTimeOffset _lastEditInvalidation = DateTimeOffset.MinValue;
    private bool _windowCloseScheduled;

    // Awaited by operations that may reduce access: the caller does not report complete until the CDN was told.
    // Without a stated cause the purge belongs to the admin request being handled, whose correlation id it records.
    internal async Task<CdnInvalidationResult> InvalidateNowAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken,
        CdnPurgeCause? cause = null)
    {
        var result = await SendWithinLimitsAsync([.. paths], cause ?? CauseOfCurrentRequest(), cancellationToken).ConfigureAwait(false);
        ReportOnCurrentResponse(result);
        return result;
    }

    internal Task InvalidateAfterEditAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
    {
        TimeSpan untilWindowCloses;
        lock (_gate)
        {
            untilWindowCloses = _lastEditInvalidation + CoalescingWindow - timeProvider.GetUtcNow();
            if (_windowCloseScheduled || untilWindowCloses > TimeSpan.Zero)
            {
                _pendingEditPaths.UnionWith(paths);
                if (_windowCloseScheduled)
                {
                    return Task.CompletedTask;
                }

                _windowCloseScheduled = true;
            }
            else
            {
                _lastEditInvalidation = timeProvider.GetUtcNow();
            }
        }

        if (untilWindowCloses > TimeSpan.Zero)
        {
            _ = InvalidatePendingWhenWindowClosesAsync(untilWindowCloses);
            return Task.CompletedTask;
        }

        return InvalidateNowAsync(paths, cancellationToken, CdnPurgeCause.Edit(CauseOfCurrentRequest().ChangeId));
    }

    private async Task InvalidatePendingWhenWindowClosesAsync(TimeSpan untilWindowCloses)
    {
        await Task.Delay(untilWindowCloses, timeProvider).ConfigureAwait(false);
        string[] paths;
        lock (_gate)
        {
            paths = [.. _pendingEditPaths];
            _pendingEditPaths.Clear();
            _windowCloseScheduled = false;
            _lastEditInvalidation = timeProvider.GetUtcNow();
        }

        var result = await SendWithinLimitsAsync(paths, CdnPurgeCause.Edit(CdnPurgeCause.CoalescedEdits), CancellationToken.None)
            .ConfigureAwait(false);
        if (result.Failed)
        {
            Log.EditInvalidationFailed(logger, result.Detail ?? "unknown");
        }
    }

    private async Task<CdnInvalidationResult> SendWithinLimitsAsync(
        string[] paths,
        CdnPurgeCause cause,
        CancellationToken cancellationToken)
    {
        var distinctPaths = paths.Distinct(StringComparer.Ordinal).ToArray();
        var withinBudget = distinctPaths.Length > MaxWildcardPathsPerInvalidation ? CdnPaths.Everything : distinctPaths;
        var result = await SendWithRetriesAsync(withinBudget, cancellationToken).ConfigureAwait(false);
        if (result.Outcome is not CdnInvalidationOutcome.NotConfigured)
        {
            var outcome = result.Outcome.ToString();
            var purgedPaths = string.Join(' ', withinBudget);
            Log.PurgeRecorded(
                logger, cause.Reason, cause.ChangeId, Environment.MachineName, outcome, result.Detail ?? string.Empty, purgedPaths);
        }

        return result;
    }

    private async Task<CdnInvalidationResult> SendWithRetriesAsync(IReadOnlyCollection<string> withinBudget, CancellationToken cancellationToken)
    {
        var result = await invalidator.InvalidateAsync(withinBudget, cancellationToken).ConfigureAwait(false);
        foreach (var delay in BusyRetryDelays)
        {
            if (result.Outcome is not CdnInvalidationOutcome.Busy)
            {
                break;
            }

            await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
            result = await invalidator.InvalidateAsync(withinBudget, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private CdnPurgeCause CauseOfCurrentRequest()
        => httpContextAccessor?.HttpContext is { } context
            ? CdnPurgeCause.Admin(AuditContextResolver.ResolveCorrelationId(context))
            : CdnPurgeCause.Admin(CdnPurgeCause.NoRequest);

    private void ReportOnCurrentResponse(CdnInvalidationResult result)
    {
        if (result.Outcome is CdnInvalidationOutcome.NotConfigured
            || httpContextAccessor?.HttpContext?.Response is not { HasStarted: false } response)
        {
            return;
        }

        response.Headers[ResultHeader] = result.Failed ? "failed" : "submitted";
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 10310,
            Level = LogLevel.Error,
            Message = "A CDN invalidation after edits failed; cached entries expire with their lifetime. error={Error}")]
        public static partial void EditInvalidationFailed(ILogger logger, string error);

        // The purge record: which change caused the invalidation, which task sent it, and what the CDN answered.
        [LoggerMessage(
            EventId = 10311,
            Level = LogLevel.Information,
            Message = "CDN purge reason={Reason} change={ChangeId} task={Task} outcome={Outcome} invalidation={InvalidationId} paths={Paths}")]
        public static partial void PurgeRecorded(
            ILogger logger, string reason, string changeId, string task, string outcome, string invalidationId, string paths);
    }
}

// Why the CDN is told to forget responses, and the id of the change that made it necessary.
internal sealed record CdnPurgeCause(string Reason, string ChangeId)
{
    internal const string NoRequest = "none";

    internal const string CoalescedEdits = "coalesced";

    internal static CdnPurgeCause Admin(string changeId) => new("admin", changeId);

    internal static CdnPurgeCause Edit(string changeId) => new("edit", changeId);

    internal static CdnPurgeCause Release(string revision) => new("release", revision);

    internal static CdnPurgeCause Operation(string operationId) => new("operation", operationId);
}

internal sealed class NoCdnInvalidator : ICdnInvalidator
{
    public Task<CdnInvalidationResult> InvalidateAsync(
        IReadOnlyCollection<string> paths,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CdnInvalidationResult.NotConfigured);
}
