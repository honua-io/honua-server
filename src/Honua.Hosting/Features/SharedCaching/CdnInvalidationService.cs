// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Infrastructure.Middleware;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.SharedCaching;

internal sealed partial class CdnInvalidationService(
    ICdnInvalidator invalidator,
    TimeProvider timeProvider,
    ILogger<CdnInvalidationService> logger,
    IHttpContextAccessor? httpContextAccessor = null,
    IOptionsMonitor<SharedCacheOptions>? options = null,
    ILeaseStore? leaseStore = null)
{
    internal const string ResultHeader = "Honua-Cdn-Invalidation";

    internal const string LeaseKeyPrefix = "cdn-purge:";

    // A bulk load can leave hundreds of addresses pending; claiming them must not take the connection pool.
    internal const int MaxConcurrentLeaseClaims = 4;

    // Edits cost at most one invalidation of an address per window across every task, whatever the number of edits; the lifetime is the backstop.
    internal static readonly TimeSpan CoalescingWindow = TimeSpan.FromSeconds(30);

    // While the emergency setting is on, edits reach the CDN sooner.
    internal static readonly TimeSpan EmergencyCoalescingWindow = TimeSpan.FromSeconds(5);

    // Above this many wildcard paths one invalidation of everything is cheaper for the CDN than naming them.
    internal const int MaxWildcardPathsPerInvalidation = 6;

    // Tags are not wildcards, but a request names at most 3000 items; well before that, everything is cheaper.
    internal const int MaxTagsPerInvalidation = 1000;

    // A lease ends this much before the window does, so the task's own timer, started a database round trip earlier, finds it expired.
    internal static readonly TimeSpan LeaseMargin = TimeSpan.FromSeconds(1);

    internal static readonly TimeSpan[] BusyRetryDelays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    private readonly Lock _gate = new();
    private readonly HashSet<string> _pendingEditPaths = new(StringComparer.Ordinal);
    private DateTimeOffset _lastEditInvalidation = DateTimeOffset.MinValue;
    private bool _windowCloseScheduled;

    // The same owner cannot take a lease it still holds, so a second purge of an address from this task also waits for the window.
    private readonly string _leaseOwner = $"{Environment.MachineName}/{Guid.NewGuid():N}";

    internal bool TagsEnabled => options?.CurrentValue.CacheTagsEnabled == true;

    // Without a CDN an edit has nothing to coalesce, and must not cost a lease row.
    private bool CdnConfigured => invalidator is not NoCdnInvalidator;

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
        if (!CdnConfigured)
        {
            return Task.CompletedTask;
        }

        var window = CurrentCoalescingWindow();
        TimeSpan untilWindowCloses;
        lock (_gate)
        {
            untilWindowCloses = _lastEditInvalidation + window - timeProvider.GetUtcNow();
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

        return SendAfterEditAsync([.. paths], CdnPurgeCause.Edit(CauseOfCurrentRequest().ChangeId), reportOnResponse: true, cancellationToken);
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

        await SendAfterEditAsync(paths, CdnPurgeCause.Edit(CdnPurgeCause.CoalescedEdits), reportOnResponse: false, CancellationToken.None)
            .ConfigureAwait(false);
    }

    // An address another task invalidated within the window is retried shortly: that purge may have preceded this edit.
    private async Task SendAfterEditAsync(string[] paths, CdnPurgeCause cause, bool reportOnResponse, CancellationToken cancellationToken)
    {
        string[] claimed;
        CdnInvalidationResult result;
        try
        {
            claimed = await ClaimAcrossTasksAsync(paths, cancellationToken).ConfigureAwait(false);
            if (claimed.Length < paths.Length)
            {
                DeferToNextWindow(paths.Except(claimed, StringComparer.Ordinal), HeldLeaseRetryDelay());
            }

            if (claimed.Length == 0)
            {
                return;
            }

            result = await SendWithinLimitsAsync(claimed, cause, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The edit request gave up, not the edit: its purge goes out with the next window.
            DeferToNextWindow(paths, CurrentCoalescingWindow());
            throw;
        }

        if (reportOnResponse)
        {
            ReportOnCurrentResponse(result);
        }
        else if (result.Failed)
        {
            Log.EditInvalidationFailed(logger, result.Detail ?? "unknown");
        }
    }

    internal void InvalidateWithNextWindow(IEnumerable<string> paths)
    {
        if (CdnConfigured)
        {
            DeferToNextWindow(paths, CurrentCoalescingWindow());
        }
    }

    private void DeferToNextWindow(IEnumerable<string> paths, TimeSpan delay)
    {
        lock (_gate)
        {
            _pendingEditPaths.UnionWith(paths);
            if (_windowCloseScheduled)
            {
                return;
            }

            _windowCloseScheduled = true;
            _lastEditInvalidation = timeProvider.GetUtcNow();
        }

        _ = InvalidatePendingWhenWindowClosesAsync(delay);
    }

    // One lease per address in the shared database: whichever task takes it sends the purge, and nobody else does until it expires.
    // A host without that database is one task, and its own window is enough.
    private async Task<string[]> ClaimAcrossTasksAsync(string[] paths, CancellationToken cancellationToken)
    {
        if (leaseStore is null || paths.Length == 0)
        {
            return paths;
        }

        var lease = CurrentCoalescingWindow() - LeaseMargin;
        var claims = new bool[paths.Length];
        await Parallel.ForAsync(
            0,
            paths.Length,
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentLeaseClaims, CancellationToken = cancellationToken },
            async (index, token) => claims[index] = await TryClaimAsync(paths[index], lease, token).ConfigureAwait(false))
            .ConfigureAwait(false);
        return [.. paths.Where((_, index) => claims[index])];
    }

    // An unreachable lease store must not leave content stale: purging twice is cheaper than serving the old version.
    private async Task<bool> TryClaimAsync(string path, TimeSpan lease, CancellationToken cancellationToken)
    {
        try
        {
            return await leaseStore!.TryAcquireAsync(new LeaseClaim(LeaseKeyPrefix + path, _leaseOwner), lease, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log.PurgeLeaseUnavailable(logger, exception, path);
            return true;
        }
    }

    // The store does not say when another task's lease ends, so a held address is retried several times per window.
    private TimeSpan HeldLeaseRetryDelay() => CurrentCoalescingWindow() / 6;

    private TimeSpan CurrentCoalescingWindow()
        => options?.CurrentValue.Emergency is { } emergency && (emergency.AllPublicLayers || emergency.Layers.Count > 0)
            ? EmergencyCoalescingWindow
            : CoalescingWindow;

    private async Task<CdnInvalidationResult> SendWithinLimitsAsync(
        string[] paths,
        CdnPurgeCause cause,
        CancellationToken cancellationToken)
    {
        var distinctPaths = paths.Distinct(StringComparer.Ordinal).ToArray();
        // A tag is not a wildcard: it does not count toward the wildcard paths the CDN allows in progress.
        var tags = distinctPaths.Count(CdnCacheTags.IsTagInvalidation);
        var withinBudget = distinctPaths.Length - tags > MaxWildcardPathsPerInvalidation || tags > MaxTagsPerInvalidation
            ? CdnPaths.Everything
            : distinctPaths;
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

        [LoggerMessage(
            EventId = 10312,
            Level = LogLevel.Warning,
            Message = "The purge lease store could not be reached; invalidating without coalescing across tasks. path={Path}")]
        public static partial void PurgeLeaseUnavailable(ILogger logger, Exception exception, string path);

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
