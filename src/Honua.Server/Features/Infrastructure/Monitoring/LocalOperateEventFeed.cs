// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Infrastructure.Monitoring;

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

    private readonly IAlertEventQuery? _alertQuery;
    private readonly IAuditLogReader? _auditReader;
    private readonly IUniversalProgressStore? _progressStore;
    private readonly ILogger<LocalOperateEventFeed> _logger;

    public LocalOperateEventFeed(
        ILogger<LocalOperateEventFeed> logger,
        IAlertEventQuery? alertQuery = null,
        IAuditLogReader? auditReader = null,
        IUniversalProgressStore? progressStore = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _alertQuery = alertQuery;
        _auditReader = auditReader;
        _progressStore = progressStore;
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

        var jobTask = Wanted(OperateEventKind.Job) && _progressStore is not null
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

        if (filter.MinimumSeverity is { } min)
        {
            collected.RemoveAll(item => item.Severity < min);
        }

        if (!string.IsNullOrWhiteSpace(filter.CorrelationId))
        {
            collected.RemoveAll(item => !string.Equals(item.CorrelationId, filter.CorrelationId, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(filter.TraceId))
        {
            collected.RemoveAll(item => !string.Equals(item.TraceId, filter.TraceId, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(filter.RequestId))
        {
            collected.RemoveAll(item => !string.Equals(item.RequestId, filter.RequestId, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(filter.OperationId))
        {
            collected.RemoveAll(item => !string.Equals(item.OperationId, filter.OperationId, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(filter.ReleaseId))
        {
            collected.RemoveAll(item => !string.Equals(item.ReleaseId, filter.ReleaseId, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(filter.ChangeSetId))
        {
            collected.RemoveAll(item => !string.Equals(item.ChangeSetId, filter.ChangeSetId, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(filter.Actor))
        {
            collected.RemoveAll(item => !string.Equals(item.Actor, filter.Actor, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(filter.ResourceRef))
        {
            collected.RemoveAll(item => !string.Equals(item.ResourceRef, filter.ResourceRef, StringComparison.Ordinal));
        }

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

        var alertFilter = new AlertEventFilter
        {
            From = filter.From,
            To = filter.To,
            ServiceId = filter.ServiceId,
            LayerId = filter.LayerId,
            PageSize = pageSize
        };

        var page = await _alertQuery!.ListAsync(alertFilter, cancellationToken).ConfigureAwait(false);
        var results = new List<OperateEvent>(page.Items.Count);
        foreach (var item in page.Items)
        {
            results.Add(MapAlertEvent(item));
        }

        return results;
    }

    private async Task<IReadOnlyList<OperateEvent>> LoadAuditAsync(
        OperateEventFilter filter,
        int pageSize,
        CancellationToken cancellationToken)
    {
        Debug.Assert(_auditReader is not null);

        var auditFilter = new AuditLogFilter
        {
            From = filter.From,
            To = filter.To,
            Actor = filter.Actor,
            CorrelationId = filter.CorrelationId,
            PageSize = pageSize
        };

        var page = await _auditReader!.ListAsync(auditFilter, cancellationToken).ConfigureAwait(false);
        var results = new List<OperateEvent>(page.Items.Count);
        foreach (var item in page.Items)
        {
            results.Add(MapAuditRecord(item));
        }

        return results;
    }

    private async Task<IReadOnlyList<OperateEvent>> LoadJobsAsync(
        OperateEventFilter filter,
        int pageSize,
        CancellationToken cancellationToken)
    {
        Debug.Assert(_progressStore is not null);

        var ids = await _progressStore!.GetActiveOperationIdsAsync(operationType: null, cancellationToken).ConfigureAwait(false);
        if (ids.Count == 0)
        {
            return Array.Empty<OperateEvent>();
        }

        var results = new List<OperateEvent>(Math.Min(ids.Count, pageSize));
        foreach (var id in ids)
        {
            if (results.Count >= pageSize)
            {
                break;
            }

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

            var mapped = MapProgress(progress);
            if (filter.From is { } fromBound && mapped.OccurredAt < fromBound)
            {
                continue;
            }

            if (filter.To is { } toBound && mapped.OccurredAt >= toBound)
            {
                continue;
            }

            results.Add(mapped);
        }

        return results;
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
