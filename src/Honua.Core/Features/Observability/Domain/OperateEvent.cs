// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Observability.Domain;

/// <summary>
/// Category of event surfaced through the Operate event feed.
/// </summary>
public enum OperateEventKind
{
    /// <summary>
    /// Spatial / threshold alert produced by the alert pipeline.
    /// </summary>
    Alert = 1,

    /// <summary>
    /// Security-relevant audit record.
    /// </summary>
    Audit = 2,

    /// <summary>
    /// Background job or long-running operation event (import, export, raster, etc.).
    /// </summary>
    Job = 3,

    /// <summary>
    /// Release / deploy / GitOps event.
    /// </summary>
    Release = 4,

    /// <summary>
    /// Disconnected sync conflict resolution event.
    /// </summary>
    SyncConflict = 5,

    /// <summary>
    /// Temporal data operation (snapshot, diff, restore).
    /// </summary>
    TemporalOp = 6,

    /// <summary>
    /// Captured server log record.
    /// </summary>
    Log = 7,
}

/// <summary>
/// Severity bucket used for cross-source filtering and Console badges.
/// </summary>
public enum OperateEventSeverity
{
    /// <summary>
    /// Informational event.
    /// </summary>
    Info = 0,

    /// <summary>
    /// Notable event worth tracking but not actionable.
    /// </summary>
    Notice = 1,

    /// <summary>
    /// Warning-level event.
    /// </summary>
    Warning = 2,

    /// <summary>
    /// Error-level event.
    /// </summary>
    Error = 3,

    /// <summary>
    /// Critical event.
    /// </summary>
    Critical = 4,
}

/// <summary>
/// Provider link used to deep-link from a normalized event to the original
/// observability backend (Grafana, Loki, SIEM, CI, cloud console, etc.).
/// </summary>
public sealed record OperateProviderLink
{
    /// <summary>
    /// Provider category (e.g. "grafana", "loki", "ci", "cloud-console", "siem").
    /// </summary>
    public required string Provider { get; init; }

    /// <summary>
    /// Human-readable label.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Absolute or relative URL the Console can open.
    /// </summary>
    public required string Url { get; init; }
}

/// <summary>
/// Single normalized event entry on the unified Operate event timeline.
/// </summary>
public sealed record OperateEvent
{
    /// <summary>
    /// Source-prefixed event identifier (e.g. "alert:42", "audit:917", "job:abc").
    /// </summary>
    public required string EventId { get; init; }

    /// <summary>
    /// Event category.
    /// </summary>
    public required OperateEventKind Kind { get; init; }

    /// <summary>
    /// Normalized severity bucket.
    /// </summary>
    public required OperateEventSeverity Severity { get; init; }

    /// <summary>
    /// Wall-clock time when the event occurred.
    /// </summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// Short human-readable title for list rows.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Optional one-line summary.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Service identifier when scoped to a service.
    /// </summary>
    public string? ServiceId { get; init; }

    /// <summary>
    /// Layer identifier when scoped to a layer.
    /// </summary>
    public int? LayerId { get; init; }

    /// <summary>
    /// Feature object identifier when scoped to a feature.
    /// </summary>
    public long? ObjectId { get; init; }

    /// <summary>
    /// Actor that produced or is associated with the event, when known.
    /// </summary>
    public string? Actor { get; init; }

    /// <summary>
    /// Cross-cutting correlation id (typically the request <c>X-Correlation-ID</c>).
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Distributed trace id when known.
    /// </summary>
    public string? TraceId { get; init; }

    /// <summary>
    /// Request id when known.
    /// </summary>
    public string? RequestId { get; init; }

    /// <summary>
    /// Operation/job id when the event is tied to a long-running operation.
    /// </summary>
    public string? OperationId { get; init; }

    /// <summary>
    /// Release identifier when the event is tied to a GitOps/deploy release.
    /// </summary>
    public string? ReleaseId { get; init; }

    /// <summary>
    /// Replica identifier when the event is tied to a specific replica/instance.
    /// </summary>
    public string? ReplicaId { get; init; }

    /// <summary>
    /// Change-set identifier for data mutation flows.
    /// </summary>
    public string? ChangeSetId { get; init; }

    /// <summary>
    /// Typed resource reference (e.g. "alert/42", "job/abc", "release/2026.05.21").
    /// </summary>
    public string? ResourceRef { get; init; }

    /// <summary>
    /// Deep links to upstream observability providers.
    /// </summary>
    public IReadOnlyList<OperateProviderLink>? ProviderLinks { get; init; }

    /// <summary>
    /// Optional source-specific structured detail as JSON text.
    /// </summary>
    public string? DetailsJson { get; init; }
}

/// <summary>
/// Filter criteria for <c>IOperateEventFeed.ListAsync</c>.
/// </summary>
public sealed record OperateEventFilter
{
    /// <summary>
    /// Lower bound (inclusive) for <see cref="OperateEvent.OccurredAt"/>.
    /// </summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>
    /// Upper bound (exclusive) for <see cref="OperateEvent.OccurredAt"/>.
    /// </summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>
    /// Optional service id filter.
    /// </summary>
    public string? ServiceId { get; init; }

    /// <summary>
    /// Optional layer id filter.
    /// </summary>
    public int? LayerId { get; init; }

    /// <summary>
    /// Optional kinds filter — events of any of these kinds are returned.
    /// </summary>
    public IReadOnlyList<OperateEventKind>? Kinds { get; init; }

    /// <summary>
    /// Optional severity floor — only events with severity &gt;= this are returned.
    /// </summary>
    public OperateEventSeverity? MinimumSeverity { get; init; }

    /// <summary>
    /// Optional correlation id filter.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Optional trace id filter.
    /// </summary>
    public string? TraceId { get; init; }

    /// <summary>
    /// Optional request id filter.
    /// </summary>
    public string? RequestId { get; init; }

    /// <summary>
    /// Optional operation/job id filter.
    /// </summary>
    public string? OperationId { get; init; }

    /// <summary>
    /// Optional release id filter.
    /// </summary>
    public string? ReleaseId { get; init; }

    /// <summary>
    /// Optional change-set id filter.
    /// </summary>
    public string? ChangeSetId { get; init; }

    /// <summary>
    /// Optional actor filter.
    /// </summary>
    public string? Actor { get; init; }

    /// <summary>
    /// Optional resource ref filter (exact match, e.g. "job/abc").
    /// </summary>
    public string? ResourceRef { get; init; }

    /// <summary>
    /// Maximum number of items per source page. Implementations clamp to a server-side maximum.
    /// </summary>
    public int PageSize { get; init; } = 50;
}

/// <summary>
/// Result page from the unified Operate event feed.
/// </summary>
public sealed record OperateEventPage
{
    /// <summary>
    /// Items returned in this page, ordered newest-first by <see cref="OperateEvent.OccurredAt"/>.
    /// </summary>
    public required IReadOnlyList<OperateEvent> Items { get; init; }

    /// <summary>
    /// Set to true when at least one upstream source returned a partial result
    /// (typically because the source threw or timed out). The available items
    /// from other sources are still returned.
    /// </summary>
    public bool PartialResult { get; init; }

    /// <summary>
    /// Per-source diagnostic messages produced while assembling the page.
    /// </summary>
    public IReadOnlyDictionary<OperateEventKind, string>? SourceErrors { get; init; }
}
