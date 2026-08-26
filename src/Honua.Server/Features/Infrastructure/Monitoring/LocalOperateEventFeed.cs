// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.ControlPlane;
using Microsoft.Extensions.Logging;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Server-local fan-out implementation of <see cref="IOperateEventFeed"/> that
/// joins alert events, audit records, and job/operation progress into a single
/// time-ordered timeline (#1168).
/// </summary>
/// <remarks>
/// <para>
/// Each upstream source is queried independently with its own bounded page size.
/// If a single source throws, the remaining sources still contribute and the
/// page is returned with <see cref="OperateEventPage.PartialResult"/> set so the
/// caller can render a partial timeline rather than failing the whole request.
/// </para>
/// <para>
/// This is intentionally a server-side composite; it lives in <c>Honua.Server</c>
/// rather than <c>Honua.Core</c> because it depends on concrete stores wired in
/// DI rather than on a domain abstraction.
/// </para>
/// </remarks>
internal sealed class LocalOperateEventFeed : IOperateEventFeed
{
    internal const int MaxPageSize = 200;
    private const int MaxSourceScanPages = 4;
    private const int DurableJobScanPageSize = 50;
    private static readonly ExecutionJobStatus[] NoticeExecutionJobStatuses =
    [
        ExecutionJobStatus.Succeeded,
        ExecutionJobStatus.Failed,
        ExecutionJobStatus.Cancelled
    ];
    private static readonly ExecutionJobStatus[] WarningExecutionJobStatuses =
    [
        ExecutionJobStatus.Failed,
        ExecutionJobStatus.Cancelled
    ];
    private static readonly ExecutionJobStatus[] ErrorExecutionJobStatuses =
    [
        ExecutionJobStatus.Failed
    ];

    private readonly IAlertEventQuery? _alertQuery;
    private readonly IAuditLogReader? _auditReader;
    private readonly IUniversalProgressStore? _progressStore;
    private readonly IExecutionJobStore? _jobStore;
    private readonly ReleaseTimelineBuffer? _releaseTimeline;
    private readonly ILogger<LocalOperateEventFeed> _logger;

    public LocalOperateEventFeed(
        ILogger<LocalOperateEventFeed> logger,
        IAlertEventQuery? alertQuery = null,
        IAuditLogReader? auditReader = null,
        IUniversalProgressStore? progressStore = null,
        IExecutionJobStore? jobStore = null,
        ReleaseTimelineBuffer? releaseTimeline = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _alertQuery = alertQuery;
        _auditReader = auditReader;
        _progressStore = progressStore;
        _jobStore = jobStore;
        _releaseTimeline = releaseTimeline;
    }

    public async Task<OperateEventPage> ListAsync(OperateEventFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var pageSize = Math.Min(MaxPageSize, Math.Max(1, filter.PageSize));
        var probeSize = pageSize + 1;
        var requestedKinds = filter.Kinds is { Count: > 0 } ? new HashSet<OperateEventKind>(filter.Kinds) : null;
        bool Wanted(OperateEventKind kind) => requestedKinds is null || requestedKinds.Contains(kind);

        var collected = new List<OperateEvent>();
        var queriedSources = new List<OperateEventKind>();
        Dictionary<OperateEventKind, string>? sourceErrors = null;
        var partial = false;
        var scanTruncated = false;

        var queryAlerts = Wanted(OperateEventKind.Alert) && _alertQuery is not null;
        if (queryAlerts)
        {
            queriedSources.Add(OperateEventKind.Alert);
        }

        var alertTask = queryAlerts
            ? LoadAlertsAsync(filter, probeSize, cancellationToken)
            : Task.FromResult(SourceLoadResult.Empty);

        var queryAudit = Wanted(OperateEventKind.Audit) && _auditReader is not null;
        if (queryAudit)
        {
            queriedSources.Add(OperateEventKind.Audit);
        }

        var auditTask = queryAudit
            ? LoadAuditAsync(filter, probeSize, cancellationToken)
            : Task.FromResult(SourceLoadResult.Empty);

        var queryJobs = Wanted(OperateEventKind.Job) && (_progressStore is not null || _jobStore is not null);
        if (queryJobs)
        {
            queriedSources.Add(OperateEventKind.Job);
        }

        var jobTask = queryJobs
            ? LoadJobsAsync(filter, probeSize, cancellationToken)
            : Task.FromResult(SourceLoadResult.Empty);

        var queryReleases = Wanted(OperateEventKind.Release) &&
            _releaseTimeline is not null &&
            ReleaseSourceCanMatch(filter);
        if (queryReleases)
        {
            queriedSources.Add(OperateEventKind.Release);
        }

        var releaseTask = queryReleases
            ? LoadReleasesAsync(filter, probeSize)
            : Task.FromResult(SourceLoadResult.Empty);

        try
        {
            var result = await alertTask.ConfigureAwait(false);
            collected.AddRange(result.Items);
            scanTruncated |= result.ScanTruncated;
            if (result.PartialResult)
            {
                partial = true;
                sourceErrors ??= new();
                sourceErrors[OperateEventKind.Alert] = "alert source incomplete";
                if (result.Exception is not null)
                {
                    ObservabilityFeedLog.AlertSourceFailed(_logger, result.Exception);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            partial = true;
            sourceErrors ??= new();
            sourceErrors[OperateEventKind.Alert] = "alert source unavailable";
            ObservabilityFeedLog.AlertSourceFailed(_logger, ex);
        }

        try
        {
            var result = await auditTask.ConfigureAwait(false);
            collected.AddRange(result.Items);
            scanTruncated |= result.ScanTruncated;
            if (result.PartialResult)
            {
                partial = true;
                sourceErrors ??= new();
                sourceErrors[OperateEventKind.Audit] = "audit source incomplete";
                if (result.Exception is not null)
                {
                    ObservabilityFeedLog.AuditSourceFailed(_logger, result.Exception);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            partial = true;
            sourceErrors ??= new();
            sourceErrors[OperateEventKind.Audit] = "audit source unavailable";
            ObservabilityFeedLog.AuditSourceFailed(_logger, ex);
        }

        try
        {
            var result = await jobTask.ConfigureAwait(false);
            collected.AddRange(result.Items);
            scanTruncated |= result.ScanTruncated;
            if (result.PartialResult)
            {
                partial = true;
                sourceErrors ??= new();
                sourceErrors[OperateEventKind.Job] = "job source incomplete";
                if (result.Exception is not null)
                {
                    ObservabilityFeedLog.JobSourceFailed(_logger, result.Exception);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            partial = true;
            sourceErrors ??= new();
            sourceErrors[OperateEventKind.Job] = "job source unavailable";
            ObservabilityFeedLog.JobSourceFailed(_logger, ex);
        }

        try
        {
            var result = await releaseTask.ConfigureAwait(false);
            collected.AddRange(result.Items);
            scanTruncated |= result.ScanTruncated;
            if (result.PartialResult)
            {
                partial = true;
                sourceErrors ??= new();
                sourceErrors[OperateEventKind.Release] = "release source incomplete";
                if (result.Exception is not null)
                {
                    ObservabilityFeedLog.ReleaseSourceFailed(_logger, result.Exception);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            partial = true;
            sourceErrors ??= new();
            sourceErrors[OperateEventKind.Release] = "release source unavailable";
            ObservabilityFeedLog.ReleaseSourceFailed(_logger, ex);
        }

        collected.RemoveAll(item => !MatchesFilter(filter, item, matchResourceRef: false));

        collected.Sort(static (left, right) => right.OccurredAt.CompareTo(left.OccurredAt));
        var hasMore = collected.Count > pageSize;
        var trimmed = collected.Take(pageSize).ToArray();

        return new OperateEventPage
        {
            Items = trimmed,
            HasMore = hasMore,
            Truncated = hasMore || scanTruncated,
            QueriedSources = queriedSources,
            PartialResult = partial,
            SourceErrors = sourceErrors
        };
    }

    private async Task<SourceLoadResult> LoadAlertsAsync(
        OperateEventFilter filter,
        int pageSize,
        CancellationToken cancellationToken)
    {
        Debug.Assert(_alertQuery is not null);

        if (!AlertSourceCanMatch(filter))
        {
            return SourceLoadResult.Empty;
        }

        if (!string.IsNullOrWhiteSpace(filter.ResourceRef))
        {
            if (!TryParseAlertResourceRef(filter.ResourceRef, out var eventId))
            {
                return SourceLoadResult.Empty;
            }

            var item = await _alertQuery!.GetAsync(eventId, cancellationToken).ConfigureAwait(false);
            if (item is null)
            {
                return SourceLoadResult.Empty;
            }

            var value = MapAlertEvent(item);
            return new SourceLoadResult(
                MatchesFilter(filter, value) ? new[] { value } : Array.Empty<OperateEvent>(),
                ScanTruncated: false);
        }

        var alertFilter = new AlertEventFilter
        {
            From = filter.From,
            To = filter.To,
            ServiceId = filter.ServiceId,
            LayerId = filter.LayerId,
            Severities = ToAlertSeverities(filter.MinimumSeverity),
            PageSize = pageSize
        };

        var results = new List<OperateEvent>(pageSize);
        string? cursor = null;
        var hasRemainingPage = false;
        var cursorCycleDetected = false;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        var scannedPages = 0;
        for (var scanPage = 0; scanPage < MaxSourceScanPages && results.Count < pageSize; scanPage++)
        {
            AlertEventPage page;
            try
            {
                page = await _alertQuery!.ListAsync(alertFilter with { Cursor = cursor }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Sort(static (left, right) => right.OccurredAt.CompareTo(left.OccurredAt));
                var preserved = results.Count <= pageSize ? results : results.GetRange(0, pageSize);
                return new SourceLoadResult(
                    preserved,
                    ScanTruncated: true,
                    PartialResult: true,
                    Exception: ex);
            }

            scannedPages++;
            var itemIndex = 0;
            while (itemIndex < page.Items.Count && results.Count < pageSize)
            {
                var value = MapAlertEvent(page.Items[itemIndex]);
                if (MatchesFilter(filter, value))
                {
                    results.Add(value);
                }

                itemIndex++;
            }

            hasRemainingPage = !string.IsNullOrEmpty(page.NextCursor);
            if (!hasRemainingPage)
            {
                break;
            }

            if (!seenCursors.Add(page.NextCursor!))
            {
                cursorCycleDetected = true;
                break;
            }

            cursor = page.NextCursor;
        }

        var scanTruncated = cursorCycleDetected || (scannedPages == MaxSourceScanPages && hasRemainingPage);
        return new SourceLoadResult(results, scanTruncated, PartialResult: scanTruncated);
    }

    private async Task<SourceLoadResult> LoadAuditAsync(
        OperateEventFilter filter,
        int pageSize,
        CancellationToken cancellationToken)
    {
        Debug.Assert(_auditReader is not null);

        if (!AuditSourceCanMatch(filter))
        {
            return SourceLoadResult.Empty;
        }

        var outcomes = ToAuditOutcomes(filter.MinimumSeverity);
        if (outcomes is { Length: 0 })
        {
            return SourceLoadResult.Empty;
        }

        var resourceFilter = ParseAuditResourceRef(filter.ResourceRef);
        if (!string.IsNullOrWhiteSpace(filter.ResourceRef) && resourceFilter is null)
        {
            return SourceLoadResult.Empty;
        }

        var auditFilter = new AuditLogFilter
        {
            From = filter.From,
            To = filter.To,
            Actor = filter.Actor,
            ResourceType = resourceFilter?.ResourceType,
            ResourceId = resourceFilter?.ResourceId,
            Outcomes = outcomes,
            CorrelationId = filter.CorrelationId,
            PageSize = pageSize
        };

        var results = new List<OperateEvent>(pageSize);
        string? cursor = null;
        var hasRemainingPage = false;
        var cursorCycleDetected = false;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        var scannedPages = 0;
        for (var scanPage = 0; scanPage < MaxSourceScanPages && results.Count < pageSize; scanPage++)
        {
            AuditEventPage page;
            try
            {
                page = await _auditReader!.ListAsync(auditFilter with { Cursor = cursor }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new SourceLoadResult(
                    results,
                    ScanTruncated: true,
                    PartialResult: true,
                    Exception: ex);
            }

            scannedPages++;
            var itemIndex = 0;
            while (itemIndex < page.Items.Count && results.Count < pageSize)
            {
                var value = MapAuditRecord(page.Items[itemIndex]);
                if (MatchesFilter(filter, value, matchResourceRef: false) &&
                    MatchesAuditResourceFilter(resourceFilter, value.ResourceRef))
                {
                    results.Add(value);
                }

                itemIndex++;
            }

            hasRemainingPage = !string.IsNullOrEmpty(page.NextCursor);
            if (!hasRemainingPage)
            {
                break;
            }

            if (!seenCursors.Add(page.NextCursor!))
            {
                cursorCycleDetected = true;
                break;
            }

            cursor = page.NextCursor;
        }

        var scanTruncated = cursorCycleDetected || (scannedPages == MaxSourceScanPages && hasRemainingPage);
        return new SourceLoadResult(results, scanTruncated, PartialResult: scanTruncated);
    }

    private static bool MatchesAuditResourceFilter(
        AuditResourceFilter? filter,
        string? resourceRef)
    {
        if (filter is null)
        {
            return true;
        }

        if (filter.ResourceId is not null)
        {
            return string.Equals(
                resourceRef,
                $"{filter.ResourceType}/{filter.ResourceId}",
                StringComparison.Ordinal);
        }

        return string.Equals(resourceRef, filter.ResourceType, StringComparison.Ordinal) ||
            (resourceRef?.StartsWith(filter.ResourceType + "/", StringComparison.Ordinal) ?? false);
    }

    private async Task<SourceLoadResult> LoadJobsAsync(
        OperateEventFilter filter,
        int pageSize,
        CancellationToken cancellationToken)
    {
        if (TryGetDirectJobLookup(filter, out var operationId))
        {
            return await LoadSingleJobAsync(filter, operationId, cancellationToken).ConfigureAwait(false);
        }

        var results = new List<OperateEvent>(pageSize * 2);
        var seenOperationIds = new HashSet<string>(StringComparer.Ordinal);
        var seenEventIds = new HashSet<string>(StringComparer.Ordinal);
        var scanTruncated = false;
        var partialResult = false;
        Exception? firstException = null;

        if (_jobStore is not null)
        {
            var durable = await LoadDurableJobsAsync(filter, pageSize, cancellationToken).ConfigureAwait(false);
            AddUniqueJobEvents(
                results,
                durable.Items,
                seenOperationIds,
                seenEventIds);
            scanTruncated |= durable.ScanTruncated;
            partialResult |= durable.PartialResult;
            firstException ??= durable.Exception;
        }

        if (_progressStore is not null)
        {
            var progress = await LoadProgressJobsAsync(filter, pageSize, cancellationToken).ConfigureAwait(false);
            AddUniqueJobEvents(
                results,
                progress.Items,
                seenOperationIds,
                seenEventIds);
            scanTruncated |= progress.ScanTruncated;
            partialResult |= progress.PartialResult;
            firstException ??= progress.Exception;
        }

        results.Sort(static (left, right) => right.OccurredAt.CompareTo(left.OccurredAt));
        if (results.Count <= pageSize)
        {
            return new SourceLoadResult(results, scanTruncated, partialResult, firstException);
        }

        return new SourceLoadResult(results.GetRange(0, pageSize), scanTruncated, partialResult, firstException);
    }

    private Task<SourceLoadResult> LoadReleasesAsync(OperateEventFilter filter, int pageSize)
    {
        Debug.Assert(_releaseTimeline is not null);

        var results = _releaseTimeline!.Snapshot()
            .Where(value => MatchesFilter(filter, value))
            .OrderByDescending(static value => value.OccurredAt)
            .Take(pageSize)
            .ToArray();

        // This buffer is per-instance and non-durable, so it cannot prove cross-replica
        // completeness, retention across restarts, or delivery of every local transition.
        // Report incomplete coverage without claiming a known next matching row.
        return Task.FromResult(new SourceLoadResult(
            results,
            ScanTruncated: true,
            PartialResult: true));
    }

    private async Task<SourceLoadResult> LoadProgressJobsAsync(
        OperateEventFilter filter,
        int pageSize,
        CancellationToken cancellationToken)
    {
        Debug.Assert(_progressStore is not null);

        if (!ProgressJobSourceCanMatch(filter))
        {
            return SourceLoadResult.Empty;
        }

        if (!string.IsNullOrWhiteSpace(filter.ResourceRef))
        {
            return SourceLoadResult.Empty;
        }

        var enumerationIncomplete = !_progressStore!.ProvidesClusterWideActiveOperationEnumeration;
        IReadOnlyList<string> ids;
        try
        {
            ids = await _progressStore.GetActiveOperationIdsAsync(operationType: null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SourceLoadResult(
                Array.Empty<OperateEvent>(),
                ScanTruncated: true,
                PartialResult: true,
                Exception: ex);
        }

        if (ids.Count == 0)
        {
            return new SourceLoadResult(
                Array.Empty<OperateEvent>(),
                ScanTruncated: enumerationIncomplete,
                PartialResult: enumerationIncomplete);
        }

        // GetActiveOperationIdsAsync returns IDs in undefined order; we must
        // load and order everything before truncating so the newest jobs are
        // not silently dropped when ids.Count > pageSize.
        var mapped = new List<OperateEvent>(ids.Count);
        var scanTruncated = enumerationIncomplete;
        var partialResult = enumerationIncomplete;
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IOperationProgress? progress;
            try
            {
                progress = await _progressStore!.GetProgressAsync(id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                scanTruncated = true;
                partialResult = true;
                ObservabilityFeedLog.ProgressFetchFailed(_logger, id, ex);
                continue;
            }

            if (progress is null)
            {
                // The active-ID snapshot raced expiry/deletion or a backend replica did
                // not expose the indexed record, so completeness cannot be proven.
                scanTruncated = true;
                partialResult = true;
                continue;
            }

            var value = MapProgress(progress);
            if (!MatchesFilter(filter, value))
            {
                continue;
            }

            mapped.Add(value);
        }

        mapped.Sort(static (left, right) => right.OccurredAt.CompareTo(left.OccurredAt));
        if (mapped.Count <= pageSize)
        {
            return new SourceLoadResult(mapped, scanTruncated, partialResult);
        }

        return new SourceLoadResult(mapped.GetRange(0, pageSize), scanTruncated, partialResult);
    }

    private async Task<SourceLoadResult> LoadSingleJobAsync(
        OperateEventFilter filter,
        string operationId,
        CancellationToken cancellationToken)
    {
        var partialResult = false;
        var scanTruncated = false;
        Exception? firstException = null;
        if (_jobStore is not null)
        {
            ExecutionJobRecord? job = null;
            try
            {
                job = await _jobStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                partialResult = true;
                scanTruncated = true;
                firstException = ex;
            }

            if (job is not null)
            {
                var durable = MapExecutionJob(job);
                return new SourceLoadResult(
                    MatchesDurableJobFilter(filter, job, durable) ? new[] { durable } : Array.Empty<OperateEvent>(),
                    ScanTruncated: false,
                    partialResult,
                    firstException);
            }
        }

        if (_progressStore is null)
        {
            return new SourceLoadResult(
                Array.Empty<OperateEvent>(),
                scanTruncated,
                partialResult,
                firstException);
        }

        var progress = await LoadSingleProgressJobAsync(filter, operationId, cancellationToken).ConfigureAwait(false);
        return progress with
        {
            ScanTruncated = scanTruncated || progress.ScanTruncated,
            PartialResult = partialResult || progress.PartialResult,
            Exception = firstException ?? progress.Exception,
        };
    }

    private async Task<SourceLoadResult> LoadSingleProgressJobAsync(
        OperateEventFilter filter,
        string operationId,
        CancellationToken cancellationToken)
    {
        Debug.Assert(_progressStore is not null);

        var lookupIncomplete = !_progressStore!.ProvidesClusterWideActiveOperationEnumeration;
        IOperationProgress? progress;
        try
        {
            progress = await _progressStore.GetProgressAsync(operationId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ObservabilityFeedLog.ProgressFetchFailed(_logger, operationId, ex);
            return new SourceLoadResult(
                Array.Empty<OperateEvent>(),
                ScanTruncated: true,
                PartialResult: true);
        }

        if (progress is null)
        {
            return new SourceLoadResult(
                Array.Empty<OperateEvent>(),
                ScanTruncated: lookupIncomplete,
                PartialResult: lookupIncomplete);
        }

        var value = MapProgress(progress);
        if (!MatchesFilter(filter, value))
        {
            return new SourceLoadResult(
                Array.Empty<OperateEvent>(),
                ScanTruncated: lookupIncomplete,
                PartialResult: lookupIncomplete);
        }

        return new SourceLoadResult(
            new[] { value },
            ScanTruncated: lookupIncomplete,
            PartialResult: lookupIncomplete);
    }

    private async Task<SourceLoadResult> LoadDurableJobsAsync(
        OperateEventFilter filter,
        int pageSize,
        CancellationToken cancellationToken)
    {
        Debug.Assert(_jobStore is not null);

        if (!DurableJobSourceCanMatch(filter))
        {
            return SourceLoadResult.Empty;
        }

        var scanLimit = Math.Min(MaxPageSize, Math.Max(pageSize, DurableJobScanPageSize));
        var results = new List<OperateEvent>(MaxSourceScanPages * scanLimit);
        var statuses = ToExecutionJobStatuses(filter.MinimumSeverity);
        string? cursor = null;
        var hasRemainingPage = false;
        var cursorCycleDetected = false;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        for (var scanPage = 0; scanPage < MaxSourceScanPages; scanPage++)
        {
            ExecutionJobPage page;
            try
            {
                page = await _jobStore.QueryAsync(
                        new ExecutionJobQuery
                        {
                            Statuses = statuses,
                            RequestedBy = filter.Actor,
                            CorrelationId = filter.CorrelationId,
                            TraceId = filter.TraceId,
                            ResourceRef = filter.ResourceRef,
                            ReleaseId = filter.ReleaseId,
                            ChangeSetId = filter.ChangeSetId,
                            // Lower time bounds are event-time filters because durable
                            // job events emit UpdatedAt/CompletedAt, not CreatedAt.
                            // CreatedTo is still safe: a job cannot emit before creation.
                            CreatedTo = filter.To,
                            Cursor = cursor,
                            Limit = scanLimit
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Sort(static (left, right) => right.OccurredAt.CompareTo(left.OccurredAt));
                var preserved = results.Count <= pageSize ? results : results.GetRange(0, pageSize);
                return new SourceLoadResult(
                    preserved,
                    ScanTruncated: true,
                    PartialResult: true,
                    Exception: ex);
            }

            foreach (var job in page.Items)
            {
                var value = MapExecutionJob(job);
                if (!MatchesDurableJobFilter(filter, job, value))
                {
                    continue;
                }

                results.Add(value);
            }

            hasRemainingPage = !string.IsNullOrEmpty(page.NextCursor);
            if (!hasRemainingPage)
            {
                break;
            }

            if (!seenCursors.Add(page.NextCursor!))
            {
                cursorCycleDetected = true;
                break;
            }

            cursor = page.NextCursor;
        }

        results.Sort(static (left, right) => right.OccurredAt.CompareTo(left.OccurredAt));
        var trimmed = results.Count <= pageSize ? results : results.GetRange(0, pageSize);
        var scanTruncated = cursorCycleDetected || hasRemainingPage;
        return new SourceLoadResult(
            trimmed,
            ScanTruncated: scanTruncated,
            PartialResult: scanTruncated);
    }

    private static bool MatchesFilter(OperateEventFilter filter, OperateEvent value, bool matchResourceRef = true)
    {
        if (filter.From is { } fromBound && value.OccurredAt < fromBound)
        {
            return false;
        }

        if (filter.To is { } toBound && value.OccurredAt >= toBound)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filter.ServiceId) &&
            !string.Equals(value.ServiceId, filter.ServiceId, StringComparison.Ordinal))
        {
            return false;
        }

        if (filter.LayerId is { } layerId && value.LayerId != layerId)
        {
            return false;
        }

        if (filter.MinimumSeverity is { } min && value.Severity < min)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filter.CorrelationId) &&
            !string.Equals(value.CorrelationId, filter.CorrelationId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filter.TraceId) &&
            !string.Equals(value.TraceId, filter.TraceId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filter.RequestId) &&
            !string.Equals(value.RequestId, filter.RequestId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filter.OperationId) &&
            !string.Equals(value.OperationId, filter.OperationId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filter.ReleaseId) &&
            !string.Equals(value.ReleaseId, filter.ReleaseId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filter.ChangeSetId) &&
            !string.Equals(value.ChangeSetId, filter.ChangeSetId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filter.Actor) &&
            !string.Equals(value.Actor, filter.Actor, StringComparison.Ordinal))
        {
            return false;
        }

        if (matchResourceRef &&
            !string.IsNullOrWhiteSpace(filter.ResourceRef) &&
            !string.Equals(value.ResourceRef, filter.ResourceRef, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static bool MatchesDurableJobFilter(
        OperateEventFilter filter,
        ExecutionJobRecord job,
        OperateEvent value)
    {
        if (!MatchesFilter(filter, value, matchResourceRef: false))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(filter.ResourceRef) ||
            string.Equals(value.ResourceRef, filter.ResourceRef, StringComparison.Ordinal))
        {
            return true;
        }

        return GetResourceRefs(job).Contains(filter.ResourceRef, StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryGetDirectJobLookup(OperateEventFilter filter, out string operationId)
    {
        if (!string.IsNullOrWhiteSpace(filter.ResourceRef) &&
            TryParseJobResourceRef(filter.ResourceRef, out operationId))
        {
            return true;
        }

        operationId = filter.OperationId ?? string.Empty;
        return !string.IsNullOrWhiteSpace(operationId);
    }

    private static void AddUniqueJobEvents(
        List<OperateEvent> target,
        IReadOnlyList<OperateEvent> source,
        HashSet<string> seenOperationIds,
        HashSet<string> seenEventIds)
    {
        foreach (var item in source)
        {
            if (!string.IsNullOrWhiteSpace(item.OperationId))
            {
                if (!seenOperationIds.Add(item.OperationId))
                {
                    continue;
                }
            }
            else if (!seenEventIds.Add(item.EventId))
            {
                continue;
            }

            target.Add(item);
        }
    }

    private static bool AlertSourceCanMatch(OperateEventFilter filter)
        => string.IsNullOrWhiteSpace(filter.CorrelationId) &&
           string.IsNullOrWhiteSpace(filter.TraceId) &&
           string.IsNullOrWhiteSpace(filter.RequestId) &&
           string.IsNullOrWhiteSpace(filter.OperationId) &&
           string.IsNullOrWhiteSpace(filter.ReleaseId) &&
           string.IsNullOrWhiteSpace(filter.ChangeSetId) &&
           string.IsNullOrWhiteSpace(filter.Actor);

    private static bool AuditSourceCanMatch(OperateEventFilter filter)
        => string.IsNullOrWhiteSpace(filter.ServiceId) &&
           filter.LayerId is null &&
           string.IsNullOrWhiteSpace(filter.TraceId) &&
           string.IsNullOrWhiteSpace(filter.RequestId) &&
           string.IsNullOrWhiteSpace(filter.OperationId) &&
           string.IsNullOrWhiteSpace(filter.ReleaseId) &&
           string.IsNullOrWhiteSpace(filter.ChangeSetId);

    private static bool DurableJobSourceCanMatch(OperateEventFilter filter)
        => string.IsNullOrWhiteSpace(filter.ServiceId) &&
           filter.LayerId is null &&
           string.IsNullOrWhiteSpace(filter.RequestId) &&
           filter.MinimumSeverity != OperateEventSeverity.Critical;

    private static bool ReleaseSourceCanMatch(OperateEventFilter filter)
        => string.IsNullOrWhiteSpace(filter.ServiceId) &&
           filter.LayerId is null &&
           string.IsNullOrWhiteSpace(filter.TraceId) &&
           string.IsNullOrWhiteSpace(filter.RequestId) &&
           string.IsNullOrWhiteSpace(filter.ChangeSetId);

    private static bool ProgressJobSourceCanMatch(OperateEventFilter filter)
        => string.IsNullOrWhiteSpace(filter.ServiceId) &&
           filter.LayerId is null &&
           string.IsNullOrWhiteSpace(filter.CorrelationId) &&
           string.IsNullOrWhiteSpace(filter.TraceId) &&
           string.IsNullOrWhiteSpace(filter.RequestId) &&
           string.IsNullOrWhiteSpace(filter.ReleaseId) &&
           string.IsNullOrWhiteSpace(filter.ChangeSetId) &&
           string.IsNullOrWhiteSpace(filter.Actor) &&
           filter.MinimumSeverity != OperateEventSeverity.Critical;

    private static AlertSeverity[]? ToAlertSeverities(OperateEventSeverity? minimumSeverity)
        => minimumSeverity switch
        {
            null or OperateEventSeverity.Info => null,
            OperateEventSeverity.Notice or OperateEventSeverity.Warning => new[] { AlertSeverity.Warning, AlertSeverity.Critical },
            OperateEventSeverity.Error or OperateEventSeverity.Critical => new[] { AlertSeverity.Critical },
            _ => null
        };

    private static AuditOutcome[]? ToAuditOutcomes(OperateEventSeverity? minimumSeverity)
        => minimumSeverity switch
        {
            null or OperateEventSeverity.Info or OperateEventSeverity.Notice => null,
            OperateEventSeverity.Warning => new[] { AuditOutcome.Failure, AuditOutcome.Denied },
            OperateEventSeverity.Error => new[] { AuditOutcome.Failure },
            OperateEventSeverity.Critical => Array.Empty<AuditOutcome>(),
            _ => null
        };

    private static ExecutionJobStatus[] ToExecutionJobStatuses(OperateEventSeverity? minimumSeverity)
        => minimumSeverity switch
        {
            null or OperateEventSeverity.Info => Array.Empty<ExecutionJobStatus>(),
            OperateEventSeverity.Notice => NoticeExecutionJobStatuses,
            OperateEventSeverity.Warning => WarningExecutionJobStatuses,
            OperateEventSeverity.Error => ErrorExecutionJobStatuses,
            _ => Array.Empty<ExecutionJobStatus>()
        };

    private static bool TryParseAlertResourceRef(string resourceRef, out long eventId)
    {
        const string Prefix = "alert/";
        eventId = 0;
        return resourceRef.StartsWith(Prefix, StringComparison.Ordinal) &&
               long.TryParse(resourceRef[Prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out eventId);
    }

    private static bool TryParseJobResourceRef(string resourceRef, out string operationId)
    {
        const string Prefix = "job/";
        operationId = string.Empty;
        if (!resourceRef.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        operationId = resourceRef[Prefix.Length..];
        return !string.IsNullOrWhiteSpace(operationId);
    }

    private static AuditResourceFilter? ParseAuditResourceRef(string? resourceRef)
    {
        if (string.IsNullOrWhiteSpace(resourceRef))
        {
            return null;
        }

        var slash = resourceRef.IndexOf('/');
        if (slash < 0)
        {
            return new AuditResourceFilter(resourceRef, ResourceId: null);
        }

        if (slash == 0 || slash == resourceRef.Length - 1)
        {
            return null;
        }

        return new AuditResourceFilter(resourceRef[..slash], resourceRef[(slash + 1)..]);
    }

    private static OperateEvent MapAlertEvent(AlertEventSummary summary)
    {
        var ref_ = "alert/" + summary.EventId.ToString(CultureInfo.InvariantCulture);
        return new OperateEvent
        {
            EventId = "alert:" + summary.EventId.ToString(CultureInfo.InvariantCulture),
            Kind = OperateEventKind.Alert,
            Severity = MapAlertSeverity(summary.Severity),
            OccurredAt = summary.OccurredAt,
            Title = $"{summary.TriggerType} alert on layer {summary.LayerId.ToString(CultureInfo.InvariantCulture)}",
            Summary = summary.RuleName,
            ServiceId = summary.ServiceId,
            LayerId = summary.LayerId,
            ObjectId = summary.ObjectId,
            ResourceRef = ref_
        };
    }

    private static OperateEvent MapAuditRecord(AuditEventRecord record)
    {
        var ref_ = string.IsNullOrEmpty(record.ResourceId)
            ? record.ResourceType
            : $"{record.ResourceType}/{record.ResourceId}";

        return new OperateEvent
        {
            EventId = "audit:" + record.AuditId.ToString(CultureInfo.InvariantCulture),
            Kind = OperateEventKind.Audit,
            Severity = MapAuditSeverity(record.Outcome),
            OccurredAt = record.Timestamp,
            Title = record.Action,
            Summary = $"{record.EventType} by {record.Actor}",
            Actor = record.Actor,
            CorrelationId = record.CorrelationId,
            ResourceRef = ref_,
            DetailsJson = AuditDetailsProjection.Project(record),
        };
    }

    private static OperateEvent MapProgress(IOperationProgress progress)
    {
        return new OperateEvent
        {
            EventId = "job:" + progress.OperationId,
            Kind = OperateEventKind.Job,
            Severity = MapJobSeverity(progress.Status),
            OccurredAt = progress.CompletedAt ?? progress.StartedAt,
            Title = $"{progress.Type} {progress.Status}",
            Summary = progress.CurrentPhase,
            OperationId = progress.OperationId,
            ResourceRef = "job/" + progress.OperationId
        };
    }

    private static OperateEvent MapExecutionJob(ExecutionJobRecord job)
    {
        return new OperateEvent
        {
            EventId = "job:" + job.OperationId,
            Kind = OperateEventKind.Job,
            Severity = MapExecutionJobSeverity(job.Status),
            OccurredAt = job.CompletedAt ?? job.UpdatedAt,
            Title = $"{job.Spec.Kind} {job.Status}",
            Summary = job.CurrentPhase,
            Actor = job.Audit.RequestedBy,
            CorrelationId = job.Audit.CorrelationId,
            TraceId = GetParameter(job, ExecutionJobParameterKeys.TraceId),
            OperationId = job.OperationId,
            ReleaseId = GetParameter(job, ExecutionJobParameterKeys.ReleaseId),
            ChangeSetId = GetParameter(job, ExecutionJobParameterKeys.ChangeSetId),
            ResourceRef = "job/" + job.OperationId
        };
    }

    private static OperateEventSeverity MapAlertSeverity(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Info => OperateEventSeverity.Info,
        AlertSeverity.Warning => OperateEventSeverity.Warning,
        AlertSeverity.Critical => OperateEventSeverity.Critical,
        _ => OperateEventSeverity.Notice
    };

    private static OperateEventSeverity MapAuditSeverity(AuditOutcome outcome) => outcome switch
    {
        AuditOutcome.Success => OperateEventSeverity.Notice,
        AuditOutcome.Failure => OperateEventSeverity.Error,
        AuditOutcome.Denied => OperateEventSeverity.Warning,
        _ => OperateEventSeverity.Info
    };

    private static OperateEventSeverity MapJobSeverity(OperationStatus status) => status switch
    {
        OperationStatus.Failed => OperateEventSeverity.Error,
        OperationStatus.Cancelled => OperateEventSeverity.Warning,
        OperationStatus.Completed => OperateEventSeverity.Notice,
        _ => OperateEventSeverity.Info
    };

    private static OperateEventSeverity MapExecutionJobSeverity(ExecutionJobStatus status) => status switch
    {
        ExecutionJobStatus.Failed => OperateEventSeverity.Error,
        ExecutionJobStatus.Cancelled => OperateEventSeverity.Warning,
        ExecutionJobStatus.Succeeded => OperateEventSeverity.Notice,
        _ => OperateEventSeverity.Info
    };

    private static string? GetParameter(ExecutionJobRecord job, string key)
        => ExecutionJobMetadata.GetParameter(job, key);

    private static string[] GetResourceRefs(ExecutionJobRecord job)
    {
        var raw = GetParameter(job, ExecutionJobParameterKeys.ResourceRefs);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        return raw.Split(
                ExecutionJobParameterKeys.MetadataListSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private sealed record SourceLoadResult(
        IReadOnlyList<OperateEvent> Items,
        bool ScanTruncated,
        bool PartialResult = false,
        Exception? Exception = null)
    {
        public static SourceLoadResult Empty { get; } = new(Array.Empty<OperateEvent>(), ScanTruncated: false);
    }

    private sealed record AuditResourceFilter(string ResourceType, string? ResourceId);
}

internal static partial class ObservabilityFeedLog
{
    [LoggerMessage(EventId = 7401, Level = LogLevel.Warning, Message = "Operate event feed failed to load alert events.")]
    public static partial void AlertSourceFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 7402, Level = LogLevel.Warning, Message = "Operate event feed failed to load audit events.")]
    public static partial void AuditSourceFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 7403, Level = LogLevel.Warning, Message = "Operate event feed failed to load job/operation events.")]
    public static partial void JobSourceFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 7405, Level = LogLevel.Warning, Message = "Operate event feed failed to load release/deploy events.")]
    public static partial void ReleaseSourceFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 7404, Level = LogLevel.Debug, Message = "Operate event feed could not fetch progress for operation {OperationId}.")]
    public static partial void ProgressFetchFailed(ILogger logger, string operationId, Exception exception);
}
