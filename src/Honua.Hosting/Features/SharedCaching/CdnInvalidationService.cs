// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Infrastructure.Middleware;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.SharedCaching;

internal sealed partial class CdnInvalidationService(
    ICdnInvalidator invalidator,
    ILogger<CdnInvalidationService> logger,
    CdnPurgeCoordination? coordination = null)
{
    private readonly TimeProvider _timeProvider = coordination?.TimeProvider ?? TimeProvider.System;
    private readonly IHttpContextAccessor? _httpContextAccessor = coordination?.HttpContextAccessor;
    private readonly IOptionsMonitor<SharedCacheOptions>? _options = coordination?.Options;
    private readonly ILeaseStore? _leaseStore = coordination?.LeaseStore;
    private readonly ICdnPurgeBudget? _budget = coordination?.Budget;
    private readonly CdnPurgeReliability? _reliability = coordination?.Reliability;

    internal const string ResultHeader = "Honua-Cdn-Invalidation";

    internal const string LeaseKeyPrefix = "cdn-purge:";

    // A bulk load can leave hundreds of addresses pending; claiming them must not take the connection pool.
    internal const int MaxConcurrentLeaseClaims = 4;

    // Edits cost at most one invalidation of an address per window across every task, whatever the number of edits; the lifetime is the backstop.
    internal static readonly TimeSpan CoalescingWindow = TimeSpan.FromSeconds(30);

    // While the emergency setting is on, edits reach the CDN sooner.
    internal static readonly TimeSpan EmergencyCoalescingWindow = TimeSpan.FromSeconds(5);

    // Above this many wildcard paths one invalidation of everything is cheaper for the CDN than naming them. Two edits of
    // different layers of one service fit, and two such requests stay within the CDN's 15 wildcard paths in progress.
    internal const int MaxWildcardPathsPerInvalidation = 7;

    // Bound tag requests below the provider limit without widening a scoped purge to every object.
    internal const int MaxTagsPerInvalidation = 1000;

    // A lease ends this much before the window does, so the task's own timer, started a database round trip earlier, finds it expired.
    internal static readonly TimeSpan LeaseMargin = TimeSpan.FromSeconds(1);

    internal static readonly TimeSpan[] BusyRetryDelays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    private readonly Lock _gate = new();
    private readonly HashSet<string> _pendingEditPaths = new(StringComparer.Ordinal);
    private DateTimeOffset _lastEditInvalidation = DateTimeOffset.MinValue;
    private DateTimeOffset? _windowClosesAt;

    // The same owner cannot take a lease it still holds, so a second purge of an address from this task also waits for the window.
    private readonly string _leaseOwner = $"{Environment.MachineName}/{Guid.NewGuid():N}";

    internal bool TagsEnabled => _options?.CurrentValue.CacheTagsEnabled == true;

    // Without a CDN an edit has nothing to coalesce, and must not cost a lease row.
    private bool CdnConfigured => invalidator is not NoCdnInvalidator;

    // Awaited by operations that may reduce access: the caller does not report complete until the CDN was told.
    // Without a stated cause the purge belongs to the admin request being handled, whose correlation id it records.
    internal async Task<CdnInvalidationResult> InvalidateNowAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken,
        CdnPurgeCause? cause = null)
    {
        string[] sent = [.. paths];
        var stated = cause ?? CauseOfCurrentRequest();
        var result = await SendWithinLimitsAsync(sent, stated, cancellationToken).ConfigureAwait(false);
        ReportOnCurrentResponse(result.Result);
        if (result.Result.Outcome is CdnInvalidationOutcome.Submitted)
        {
            AfterSubmitted(sent, stated);
        }

        return result.Result;
    }

    internal Task InvalidateAfterEditAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
    {
        if (!CdnConfigured)
        {
            return Task.CompletedTask;
        }

        NoteEditQueued(paths);
        DateTimeOffset? schedule = null;
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            var due = _lastEditInvalidation + CurrentCoalescingWindow();
            // Once the task is stopping no window timer will fire in time, so an edit is sent at once.
            if (!_stopping && (_windowClosesAt.HasValue || due > now))
            {
                _pendingEditPaths.UnionWith(paths);
                due = due > now ? due : now;
                if (_windowClosesAt is { } scheduled && scheduled <= due)
                {
                    return Task.CompletedTask;
                }

                _windowClosesAt = schedule = due;
            }
            else
            {
                _lastEditInvalidation = now;
            }
        }

        if (schedule is { } windowClosesAt)
        {
            _ = InvalidatePendingWhenWindowClosesAsync(windowClosesAt);
            return Task.CompletedTask;
        }

        return SendAndReportAfterEditAsync([.. paths], CdnPurgeCause.Edit(CauseOfCurrentRequest().ChangeId), cancellationToken);
    }

    private async Task SendAndReportAfterEditAsync(string[] paths, CdnPurgeCause cause, CancellationToken cancellationToken)
    {
        var result = await SendAfterEditAsync(paths, cause, cancellationToken).ConfigureAwait(false);
        if (result is not null)
        {
            ReportOnCurrentResponse(result);
        }
    }

    private TimeSpan CurrentCoalescingWindow()
        => _options?.CurrentValue.Emergency is { } emergency && (emergency.AllPublicLayers || emergency.Layers.Count > 0)
            ? EmergencyCoalescingWindow
            : CoalescingWindow;

    private sealed record BatchInvalidationResult(CdnInvalidationResult Result, IReadOnlyCollection<string> BusyPaths);

    private async Task<BatchInvalidationResult> SendWithinLimitsAsync(
        string[] paths,
        CdnPurgeCause cause,
        CancellationToken cancellationToken)
    {
        var busyPaths = new List<string>();
        var distinctPaths = paths.Distinct(StringComparer.Ordinal).ToArray();
        var tags = distinctPaths.Where(CdnCacheTags.IsTagInvalidation).ToArray();
        var wildcardPaths = distinctPaths.Where(path => !CdnCacheTags.IsTagInvalidation(path)).ToArray();
        if (wildcardPaths.Length > MaxWildcardPathsPerInvalidation)
        {
            return new(await SendBatchAsync(CdnPaths.Everything).ConfigureAwait(false), busyPaths);
        }

        if (tags.Length <= MaxTagsPerInvalidation)
        {
            return new(await SendBatchAsync(distinctPaths).ConfigureAwait(false), busyPaths);
        }

        var combined = CdnInvalidationResult.NotConfigured;
        foreach (var batch in tags.Chunk(MaxTagsPerInvalidation))
        {
            var result = await SendBatchAsync(batch).ConfigureAwait(false);
            combined = CombineBatchResults(combined, result);
        }

        if (wildcardPaths.Length > 0)
        {
            var result = await SendBatchAsync(wildcardPaths).ConfigureAwait(false);
            combined = CombineBatchResults(combined, result);
        }

        return new(combined, busyPaths);

        async Task<CdnInvalidationResult> SendBatchAsync(IReadOnlyCollection<string> batch)
        {
            if (await RefuseWhenEditBudgetSpentAsync(batch, cause, cancellationToken).ConfigureAwait(false) is { } refused)
            {
                return refused;
            }

            var result = await SendAndRecordAsync(batch, cause, cancellationToken).ConfigureAwait(false);
            if (result.Outcome is CdnInvalidationOutcome.Busy)
            {
                busyPaths.AddRange(batch);
            }

            return result;
        }
    }

    private static CdnInvalidationResult CombineBatchResults(CdnInvalidationResult combined, CdnInvalidationResult next)
        => combined.Outcome is CdnInvalidationOutcome.Failed ? combined
            : next.Outcome is CdnInvalidationOutcome.Failed ? next
            : combined.Failed ? combined : next;

    private async Task<CdnInvalidationResult> SendAndRecordAsync(
        IReadOnlyCollection<string> paths,
        CdnPurgeCause cause,
        CancellationToken cancellationToken)
    {
        var result = await SendAndMeasureAsync(paths, cause, cancellationToken).ConfigureAwait(false);
        if (result.Outcome is CdnInvalidationOutcome.NotConfigured)
        {
            return result;
        }

        // A result that is still busy has already used its retries: an edit made now would not reach the CDN either. A
        // stopping task's send skips those retries, so its busy answer says too little to hold off the fleet; an outright
        // refusal still does.
        if (result.Failed && !(cause.Kind is CdnPurgeKind.Shutdown && result.Outcome is CdnInvalidationOutcome.Busy))
        {
            _reliability?.RecordFailedPurge();
        }
        else if (result.Outcome is CdnInvalidationOutcome.Submitted)
        {
            _reliability?.RecordDeliveredPurge();
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            var outcome = result.Outcome.ToString();
            var recordedPaths = string.Join(' ', paths);
            Log.PurgeRecorded(
                logger, cause.Reason, cause.ChangeId, Environment.MachineName, outcome,
                result.Detail ?? string.Empty, recordedPaths);
        }

        return result;
    }

    private async Task<CdnInvalidationResult> SendAndMeasureAsync(
        IReadOnlyCollection<string> paths, CdnPurgeCause cause, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var outcome = "failed";
        try
        {
            var result = await SendWithRetriesAsync(paths, cause, cancellationToken).ConfigureAwait(false);
            outcome = result.Outcome.ToString().ToLowerInvariant();
            return result;
        }
        catch (OperationCanceledException)
        {
            outcome = "canceled";
            throw;
        }
        finally
        {
            if (outcome != "notconfigured")
            {
                SharedCacheMetrics.RecordPurge(cause, new CdnPurgeSample(paths, outcome, Stopwatch.GetElapsedTime(started)));
            }
        }
    }

    // A stopping task does not wait out a busy CDN.
    private async Task<CdnInvalidationResult> SendWithRetriesAsync(
        IReadOnlyCollection<string> withinBudget, CdnPurgeCause cause, CancellationToken cancellationToken)
    {
        var result = await invalidator.InvalidateAsync(withinBudget, cancellationToken).ConfigureAwait(false);
        foreach (var delay in cause.Kind is CdnPurgeKind.Shutdown ? Array.Empty<TimeSpan>() : BusyRetryDelays)
        {
            if (result.Outcome is not CdnInvalidationOutcome.Busy)
            {
                break;
            }

            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            result = await invalidator.InvalidateAsync(withinBudget, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private CdnPurgeCause CauseOfCurrentRequest()
        => _httpContextAccessor?.HttpContext is { } context
            ? CdnPurgeCause.Admin(AuditContextResolver.ResolveCorrelationId(context))
            : CdnPurgeCause.Admin(CdnPurgeCause.NoRequest);

    private void ReportOnCurrentResponse(CdnInvalidationResult result)
    {
        if (result.Outcome is CdnInvalidationOutcome.NotConfigured
            || _httpContextAccessor?.HttpContext?.Response is not { HasStarted: false } response)
        {
            return;
        }

        response.Headers[ResultHeader] = result.Outcome switch
        {
            CdnInvalidationOutcome.Submitted => "submitted",
            CdnInvalidationOutcome.BudgetExceeded => "budget",
            _ => "failed",
        };
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

        [LoggerMessage(
            EventId = 10313,
            Level = LogLevel.Warning,
            Message = "CDN edit purges are paused for this month; cached edits expire with their lifetime. submitted={Submitted} estimatedSpend={EstimatedSpend} paths={Paths}")]
        public static partial void PurgeBudgetSpent(ILogger logger, int submitted, string estimatedSpend, string paths);

        [LoggerMessage(
            EventId = 10314,
            Level = LogLevel.Warning,
            Message = "The CDN purge budget could not be read; the edit purge was sent anyway.")]
        public static partial void PurgeBudgetUnreadable(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 10318,
            Level = LogLevel.Warning,
            Message = "Stopped retrying failed CDN edit purges: every purge-backed copy they would remove has expired. paths={Count}")]
        public static partial void FailedEditPurgeAbandoned(ILogger logger, int count);

        [LoggerMessage(
            EventId = 10319,
            Level = LogLevel.Error,
            Message = "A stopping task could not send its waiting CDN purges; invalidate these before relying on purge-backed lifetimes. count={Count} paths={Paths}")]
        public static partial void ShutdownPurgesNotSent(ILogger logger, int count, string paths);

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

    // Set only by the service itself: the change id can come from a client's correlation id, so it never decides how
    // a purge is handled.
    internal CdnPurgeKind Kind { get; private init; } = CdnPurgeKind.Change;

    // The repeat of a purge after the settle delay; it is not repeated again.
    internal static CdnPurgeCause Settle() => new("edit", "settle") { Kind = CdnPurgeKind.Settle };

    // Purges a stopping task sends instead of leaving them to its windows.
    internal static CdnPurgeCause Shutdown() => new("edit", "shutdown") { Kind = CdnPurgeKind.Shutdown };

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

internal enum CdnPurgeKind
{
    Change,
    Settle,
    Shutdown,
}
