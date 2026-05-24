// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

/// <summary>
/// Filter and cursor criteria for querying durable execution jobs.
/// </summary>
public sealed record ExecutionJobQuery
{
    /// <summary>
    /// Optional status filter. When multiple statuses are supplied, jobs matching
    /// any supplied status are returned.
    /// </summary>
    public IReadOnlyList<ExecutionJobStatus> Statuses { get; init; } = Array.Empty<ExecutionJobStatus>();

    /// <summary>
    /// Optional execution job kind filter.
    /// </summary>
    public ExecutionJobKind? Kind { get; init; }

    /// <summary>
    /// Optional backend identifier filter.
    /// </summary>
    public string? Backend { get; init; }

    /// <summary>
    /// Optional submitting actor filter.
    /// </summary>
    public string? RequestedBy { get; init; }

    /// <summary>
    /// Optional correlation identifier filter.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Optional distributed trace identifier filter.
    /// </summary>
    public string? TraceId { get; init; }

    /// <summary>
    /// Optional job definition identifier filter.
    /// </summary>
    public string? DefinitionId { get; init; }

    /// <summary>
    /// Optional resource reference filter.
    /// </summary>
    public string? ResourceRef { get; init; }

    /// <summary>
    /// Optional deployment or runtime environment filter.
    /// </summary>
    public string? Environment { get; init; }

    /// <summary>
    /// Optional server or worker instance filter.
    /// </summary>
    public string? Server { get; init; }

    /// <summary>
    /// Optional release identifier filter.
    /// </summary>
    public string? ReleaseId { get; init; }

    /// <summary>
    /// Optional change-set identifier filter.
    /// </summary>
    public string? ChangeSetId { get; init; }

    /// <summary>
    /// Optional alert identifier filter.
    /// </summary>
    public string? AlertId { get; init; }

    /// <summary>
    /// Inclusive lower bound for <see cref="ExecutionJobRecord.CreatedAt"/>.
    /// </summary>
    public DateTimeOffset? CreatedFrom { get; init; }

    /// <summary>
    /// Exclusive upper bound for <see cref="ExecutionJobRecord.CreatedAt"/>.
    /// </summary>
    public DateTimeOffset? CreatedTo { get; init; }

    /// <summary>
    /// Opaque cursor returned by a previous page.
    /// </summary>
    public string? Cursor { get; init; }

    /// <summary>
    /// Requested page size. Implementations clamp this to their supported range.
    /// </summary>
    public int Limit { get; init; } = 50;
}

/// <summary>
/// Cursor page of durable execution jobs.
/// </summary>
public sealed record ExecutionJobPage
{
    /// <summary>
    /// Jobs returned in this page, ordered newest first.
    /// </summary>
    public required IReadOnlyList<ExecutionJobRecord> Items { get; init; }

    /// <summary>
    /// Opaque cursor for the next page, or null when no more items are available.
    /// </summary>
    public string? NextCursor { get; init; }
}

/// <summary>
/// Cursor criteria for querying structured execution log entries.
/// </summary>
public sealed record ExecutionLogQuery
{
    /// <summary>
    /// Opaque cursor returned by a previous log page.
    /// </summary>
    public string? Cursor { get; init; }

    /// <summary>
    /// Requested page size. Implementations clamp this to their supported range.
    /// </summary>
    public int Limit { get; init; } = 100;
}

/// <summary>
/// Cursor page of structured execution log entries.
/// </summary>
public sealed record ExecutionLogPage
{
    /// <summary>
    /// Log entries returned in append order.
    /// </summary>
    public required IReadOnlyList<ExecutionLogEntry> Items { get; init; }

    /// <summary>
    /// Opaque cursor for the next page, or null when no more entries are available.
    /// </summary>
    public string? NextCursor { get; init; }
}
