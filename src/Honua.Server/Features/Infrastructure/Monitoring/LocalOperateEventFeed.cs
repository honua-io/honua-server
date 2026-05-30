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
    private readonly ILogger<LocalOperateEventFeed> _logger;

    public LocalOperateEventFeed(
        ILogger<LocalOperateEventFeed> logger,
        IAlertEventQuery? alertQuery = null,
        IAuditLogReader? auditReader = null,
        IUniversalProgressStore? progressStore = null,
        IExecutionJobStore? jobStore = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _alertQuery = alertQuery;
        _auditReader = auditReader;
        _progressStore = progressStore;
        _jobStore = jobStore;
    }

    public async Task<OperateEventPage> ListAsync(OperateEventFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var pageSize = Math.Min(MaxPageSize, Math.Max(1, filter.PageSize));
        var requestedKinds = filter.Kinds is { Count: > 0 } ? new HashSet<OperateEventKind>(filter.Kinds) : null;
        bool Wanted(OperateEventKind kind) => requestedKinds is null || requestedKinds.Contains(kind);

        var collected = new List<OperateEvent>();
        Dictionary<OperateEventKind, string>? sourceErrors = null;
        var partial = false;

        var alertTask = Wanted(OperateEventKind.Alert) && _alertQuery is not null
            ? LoadAlertsAsync(filter, pageSize, cancellationToken)
            : Task.FromResult<IReadOnlyList<OperateEvent>>(Array.Empty<OperateEvent>());

        var auditTask = Wanted(OperateEventKind.Audit) && _auditReader is not null
            ? LoadAuditAsync(filter, pageSize, cancellationToken)
            : Task.FromResult<IReadOnlyList<OperateEvent>>(Array.Empty<OperateEvent>());

        var jobTask = Wanted(OperateEventKind.Job) && (_progressStore is not null || _jobStore is not null)
            ? LoadJobsAsync(filter, pageSize, cancellationToken)
            : Task.FromResult<IReadOnlyList<OperateEvent>>(Array.Empty<OperateEvent>());

        try
        {
            collected.AddRange(await alertTask.ConfigureAwait(false));
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
            collected.AddRange(await auditTask.ConfigureAwait(false));
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
            collected.AddRange(await jobTask.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            partial = true;
            sourceErrors ??= new();
            sourceErrors[OperateEventKind.Job] = "job source unavailable";
            ObservabilityFeedLog.JobSourceFailed(_logger, ex);
        }

        collected.RemoveAll(item => !MatchesFilter(filter, item, matchResourceRef: false));

        collected.Sort(static (left, right) => right.OccurredAt.CompareTo(left.OccurredAt));
        var trimmed = collected.Take(pageSize).ToArray();

        return new OperateEventPage
        {
            Items = trimmed,
            PartialResult = partial,
            SourceErrors = sourceErrors
        };
    }

    private async Task<IReadOnlyList<OperateEvent>> LoadAlertsAsync(
        OperateEventFilter filter,
        int pageSize,
        CancellationToken cancellationToken)
    {
        Debug.Assert(_alertQuery is not null);

        if (!AlertSourceCanMatch(filter))
        {
            return Array.Empty<OperateEvent>();
        }

        if (!string.IsNullOrWhiteSpace(filter.ResourceRef))
        {
            if (!TryParseAlertResourceRef(filter.ResourceRef, out var eventId))
            {
                return Array.Empty<OperateEvent>();
            }

            var item = await _alertQuery!.GetAsync(eventId, cancellationToken).ConfigureAwait(false);
            if (item is null)
            {
                return Array.Empty<OperateEvent>();
            }

            var value = MapAlertEvent(item);
            return MatchesFilter(filter, value) ? new[] { value } : Array.Empty<OperateEvent>();
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
        for (var scanPage = 0; scanPage < MaxSourceScanPages && results.Count < pageSize; scanPage++)
        {
            var page = await _alertQuery!.ListAsync(alertFilter with { Cursor = cursor }, cancellationToken).ConfigureAwait(false);
            foreach (var item in page.Items)
            {
                var value = MapAlertEvent(item);
                if (MatchesFilter(filter, value))
                {
                    results.Add(value);
                    if (results.Count == pageSize)
                    {
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(page.NextCursor) ||
                string.Equals(page.NextCursor, cursor, StringComparison.Ordinal))
            {
                break;
            }

            cursor = page.NextCursor;
        }

        return results;
    }

    private async Task<IReadOnlyList<OperateEvent>> LoadAuditAsync(
        OperateEventFilter filter,
        int pageSize,
        CancellationToken cancellationToken)
    {
        Debug.Assert(_auditReader is not null);

        if (!AuditSourceCanMatch(filter))
        {
            return Array.Empty<OperateEvent>();
        }

        var outcomes = ToAuditOutcomes(filter.MinimumSeverity);
        if (outcomes is { Length: 0 })
        {
            return Array.Empty<OperateEvent>();
        }

        var resourceFilter = ParseAuditResourceRef(filter.ResourceRef);
        if (!string.IsNullOrWhiteSpace(filter.ResourceRef) && resourceFilter is null)
        {
            return Array.Empty<OperateEvent>();
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
        for (var scanPage = 0; scanPage < MaxSourceScanPages && results.Count < pageSize; scanPage++)
        {
            var page = await _auditReader!.ListAsync(auditFilter with { Cursor = cursor }, cancellationToken).ConfigureAwait(false);
            foreach (var item in page.Items)
            {
                var value = MapAuditRecord(item);
                if (MatchesFilter(filter, value))
                {
                    results.Add(value);
                    if (results.Count == pageSize)
                    {
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(page.NextCursor) ||
                string.Equals(page.NextCursor, cursor, StringComparison.Ordinal))
            {
                break;
            }

            cursor = page.NextCursor;
        }

        return results;
    }

    private async Task<IReadOnlyList<OperateEvent>> LoadJobsAsync(
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

        if (_jobStore is not null)
        {
            AddUniqueJobEvents(
                results,
                await LoadDurableJobsAsync(filter, pageSize, cancellationToken).ConfigureAwait(false),
                seenOperationIds,
                seenEventIds);
        }

        if (_progressStore is not null)
        {
            AddUniqueJobEvents(
                results,
                await LoadProgressJobsAsync(filter, pageSize, cancellationToken).ConfigureAwait(false),
                seenOperationIds,
                seenEventIds);
        }

        results.Sort(static (left, right) => right.OccurredAt.CompareTo(left.OccurredAt));
        if (results.Count <= pageSize)
        {
            return results;
        }

        return results.GetRange(0, pageSize);
    }

    private async Task<IReadOnlyList<OperateEvent>> LoadProgressJobsAsync(
        OperateEventFilter filter,
        int pageSize,
        CancellationToken cancellationToken)
    {
        Debug.Assert(_progressStore is not null);

        if (!ProgressJobSourceCanMatch(filter))
        {
            return Array.Empty<OperateEvent>();
        }

        if (!string.IsNullOrWhiteSpace(filter.ResourceRef))
        {
            return Array.Empty<OperateEvent>();
        }

        var ids = await _progressStore!.GetActiveOperationIdsAsync(operationType: null, cancellationToken).ConfigureAwait(false);
        if (ids.Count == 0)
        {
            return Array.Empty<OperateEvent>();
        }

        // GetActiveOperationIdsAsync returns IDs in undefined order; we must
        // load and order everything before truncating so the newest jobs are
        // not silently dropped when ids.Count > pageSize.
        var mapped = new List<OperateEvent>(ids.Count);
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
                ObservabilityFeedLog.ProgressFetchFailed(_logger, id, ex);
                continue;
            }

            if (progress is null)
            {
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
            return mapped;
        }

        return mapped.GetRange(0, pageSize);
    }

    private async Task<IReadOnlyList<OperateEvent>> LoadSingleJobAsync(
        OperateEventFilter filter,
        string operationId,
        CancellationToken cancellationToken)
    {
        if (_jobStore is not null)
        {
            var job = await _jobStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (job is not null)
            {
                var durable = MapExecutionJob(job);
                return MatchesDurableJobFilter(filter, job, durable) ? new[] { durable } : Array.Empty<OperateEvent>();
            }
        }

        return _progressStore is null
            ? Array.Empty<OperateEvent>()
            : await LoadSingleProgressJobAsync(filter, operationId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<OperateEvent>> LoadSingleProgressJobAsync(
        OperateEventFilter filter,
        string operationId,
        CancellationToken cancellationToken)
    {
        Debug.Assert(_progressStore is not null);

        IOperationProgress? progress;
        try
        {
            progress = await _progressStore!.GetProgressAsync(operationId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ObservabilityFeedLog.ProgressFetchFailed(_logger, operationId, ex);
            return Array.Empty<OperateEvent>();
        }

        if (progress is null)
        {
            return Array.Empty<OperateEvent>();
        }

        var value = MapProgress(progress);
        if (!MatchesFilter(filter, value))
        {
            return Array.Empty<OperateEvent>();
        }

        return new[] { value };
    }

    private async Task<IReadOnlyList<OperateEvent>> LoadDurableJobsAsync(
        OperateEventFilter filter,
        int pageSize,
        CancellationToken cancellationToken)
    {
        Debug.Assert(_jobStore is not null);

        if (!DurableJobSourceCanMatch(filter))
        {
            return Array.Empty<OperateEvent>();
        }

        var results = new List<OperateEvent>(pageSize);
        var scanLimit = Math.Min(MaxPageSize, Math.Max(pageSize, DurableJobScanPageSize));
        var statuses = ToExecutionJobStatuses(filter.MinimumSeverity);
        string? cursor = null;
        for (var scanPage = 0; scanPage < MaxSourceScanPages && results.Count < pageSize; scanPage++)
        {
            var page = await _jobStore.QueryAsync(
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

            foreach (var job in page.Items)
            {
                var value = MapExecutionJob(job);
                if (!MatchesDurableJobFilter(filter, job, value))
                {
                    continue;
                }

                results.Add(value);
                if (results.Count == pageSize)
                {
                    break;
                }
            }

            if (string.IsNullOrEmpty(page.NextCursor) ||
                string.Equals(page.NextCursor, cursor, StringComparison.Ordinal))
            {
                break;
            }

            cursor = page.NextCursor;
        }

        return results;
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
            ResourceRef = ref_
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

    [LoggerMessage(EventId = 7404, Level = LogLevel.Debug, Message = "Operate event feed could not fetch progress for operation {OperationId}.")]
    public static partial void ProgressFetchFailed(ILogger logger, string operationId, Exception exception);
}
