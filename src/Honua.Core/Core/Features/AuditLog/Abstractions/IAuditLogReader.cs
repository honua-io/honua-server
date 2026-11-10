// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.AuditLog.Abstractions;

// Remote IP and user agent may already be null in the stored row when capture was disabled, and readers must not invent them.
public interface IAuditLogReader
{
    Task<AuditEventPage> ListAsync(AuditLogFilter filter, CancellationToken cancellationToken = default);
}

public sealed class InvalidAuditLogCursorException : ArgumentException
{
    public InvalidAuditLogCursorException()
        : base("Audit-log cursor is malformed.", "cursor")
    {
    }
}

public sealed record AuditLogFilter
{
    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }

    public string? Actor { get; init; }

    public IReadOnlyList<AuditActorType>? ActorTypes { get; init; }

    public string? ResourceType { get; init; }

    public string? ResourceId { get; init; }

    public string? Action { get; init; }

    public IReadOnlyList<AuditEventType>? EventTypes { get; init; }

    public IReadOnlyList<AuditOutcome>? Outcomes { get; init; }

    public string? CorrelationId { get; init; }

    public int PageSize { get; init; } = 50;

    public string? Cursor { get; init; }
}

public sealed record AuditEventPage
{
    public required IReadOnlyList<AuditEventRecord> Items { get; init; }

    public string? NextCursor { get; init; }
}

public sealed record AuditEventRecord
{
    public required long AuditId { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required AuditEventType EventType { get; init; }

    public required string Actor { get; init; }

    public required AuditActorType ActorType { get; init; }

    public required string ResourceType { get; init; }

    public string? ResourceId { get; init; }

    public required string Action { get; init; }

    public required AuditOutcome Outcome { get; init; }

    public required string CorrelationId { get; init; }

    public string? RemoteIp { get; init; }

    public string? UserAgent { get; init; }

    public string Details { get; init; } = string.Empty;
}
