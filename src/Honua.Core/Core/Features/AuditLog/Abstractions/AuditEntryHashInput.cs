// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.AuditLog.Abstractions;

// The fields of one audit row that its chain digest binds, in canonical order.
public sealed record AuditEntryHashInput
{
    public string? PreviousHash { get; init; }
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
    public required string Details { get; init; }

    // Null when no agent acted, which keeps legacy digests unchanged.
    public string? ActorAgent { get; init; }
}
