// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.AuditLog.Abstractions;

/// <summary>
/// Read-side companion to <see cref="IAuditLog"/>.
/// </summary>
/// <remarks>
/// <para>
/// The write-side sink is intentionally minimal and append-only. Console-facing
/// query, forensic, and compliance workflows need filtered, paginated reads
/// without touching the write path; this interface keeps them on separate
/// dependency seams.
/// </para>
/// <para>
/// Implementations MUST respect the privacy posture documented on
/// <see cref="AuditEvent"/>: remote IP and user agent may already be null in the
/// stored row when capture is disabled. Callers see whatever was persisted.
/// </para>
/// </remarks>
public interface IAuditLogReader
{
    /// <summary>
    /// Returns a page of audit events matching the filter, ordered newest-first.
    /// </summary>
    /// <param name="filter">Query criteria.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AuditEventPage> ListAsync(AuditLogFilter filter, CancellationToken cancellationToken = default);
}

/// <summary>
/// Filter criteria for <see cref="IAuditLogReader.ListAsync"/>.
/// </summary>
public sealed record AuditLogFilter
{
    /// <summary>
    /// Lower bound (inclusive) for <see cref="AuditEvent.Timestamp"/>.
    /// </summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>
    /// Upper bound (exclusive) for <see cref="AuditEvent.Timestamp"/>.
    /// </summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>
    /// Optional actor filter (exact match).
    /// </summary>
    public string? Actor { get; init; }

    /// <summary>
    /// Optional actor type filter — events with any of these actor types are returned.
    /// </summary>
    public IReadOnlyList<AuditActorType>? ActorTypes { get; init; }

    /// <summary>
    /// Optional resource type filter (exact match).
    /// </summary>
    public string? ResourceType { get; init; }

    /// <summary>
    /// Optional resource id filter (exact match).
    /// </summary>
    public string? ResourceId { get; init; }

    /// <summary>
    /// Optional dotted action filter (exact match).
    /// </summary>
    public string? Action { get; init; }

    /// <summary>
    /// Optional event type filter — events with any of these event types are returned.
    /// </summary>
    public IReadOnlyList<AuditEventType>? EventTypes { get; init; }

    /// <summary>
    /// Optional outcome filter — events with any of these outcomes are returned.
    /// </summary>
    public IReadOnlyList<AuditOutcome>? Outcomes { get; init; }

    /// <summary>
    /// Optional correlation id filter (exact match) — enables joining to log/trace records.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Maximum number of rows to return. Implementations clamp to a server-side maximum.
    /// </summary>
    public int PageSize { get; init; } = 50;

    /// <summary>
    /// Opaque pagination cursor returned by a prior page.
    /// </summary>
    public string? Cursor { get; init; }
}

/// <summary>
/// A single page of audit log records, ordered newest-first.
/// </summary>
public sealed record AuditEventPage
{
    /// <summary>
    /// Audit records returned in this page.
    /// </summary>
    public required IReadOnlyList<AuditEventRecord> Items { get; init; }

    /// <summary>
    /// Cursor to retrieve the next page, or null when there are no more rows.
    /// </summary>
    public string? NextCursor { get; init; }
}

/// <summary>
/// Read model for a stored audit event row.
/// </summary>
/// <remarks>
/// Differs from <see cref="AuditEvent"/> by carrying the persisted row identifier so
/// callers can correlate paginated reads with downstream investigation pins.
/// </remarks>
public sealed record AuditEventRecord
{
    /// <summary>
    /// Sink-assigned row identifier.
    /// </summary>
    public required long AuditId { get; init; }

    /// <summary>
    /// Original event timestamp.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Event type category.
    /// </summary>
    public required AuditEventType EventType { get; init; }

    /// <summary>
    /// Actor identifier.
    /// </summary>
    public required string Actor { get; init; }

    /// <summary>
    /// Actor classification.
    /// </summary>
    public required AuditActorType ActorType { get; init; }

    /// <summary>
    /// Resource type, "-" when not applicable.
    /// </summary>
    public required string ResourceType { get; init; }

    /// <summary>
    /// Optional resource identifier.
    /// </summary>
    public string? ResourceId { get; init; }

    /// <summary>
    /// Dotted action name.
    /// </summary>
    public required string Action { get; init; }

    /// <summary>
    /// Outcome classification.
    /// </summary>
    public required AuditOutcome Outcome { get; init; }

    /// <summary>
    /// Correlation id captured at emit time.
    /// </summary>
    public required string CorrelationId { get; init; }

    /// <summary>
    /// Remote IP address when capture is enabled.
    /// </summary>
    public string? RemoteIp { get; init; }

    /// <summary>
    /// User-Agent header when capture is enabled.
    /// </summary>
    public string? UserAgent { get; init; }

    /// <summary>
    /// Pre-sanitized structured detail.
    /// </summary>
    public string Details { get; init; } = string.Empty;
}
