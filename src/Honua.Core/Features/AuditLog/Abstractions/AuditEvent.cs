// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.AuditLog.Abstractions;

// Structured context must be sanitized before it is placed in the record, because the sink does not sanitize it.
public sealed record AuditEvent
{
    public const string AnonymousActor = "anonymous";

    // The caller stamps the timestamp so a retry or a queue does not re-stamp the event.
    public required DateTimeOffset Timestamp { get; init; }

    public required AuditEventType EventType { get; init; }

    // An API-key actor is a hash of the key id, never the raw key.
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
