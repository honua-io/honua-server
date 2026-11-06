// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Alerts.Domain;

// Shared request audit does not substitute for the domain action.
public enum AlertLifecycleAction
{
    Acknowledge = 0,

    Suppress = 1,

    Resolve = 2,
}

// The store applies the mutation and its audit intent in one transaction.
public sealed record AlertLifecycleCommand
{
    public required long EventId { get; init; }

    public required AlertLifecycleAction Action { get; init; }

    public required string Actor { get; init; }

    // The caller supplies the action timestamp so a retry replays the same instant.
    public required DateTimeOffset OccurredAt { get; init; }

    public required string CorrelationId { get; init; }

    // Two requests with the same retry key are one operator action and must not produce a second lifecycle transition or audit action.
    public required string IdempotencyKey { get; init; }

    public string? Note { get; init; }

    public DateTimeOffset? SuppressUntil { get; init; }

    public string Details { get; init; } = string.Empty;

    public string AuditAction => AlertAuditActions.ForLifecycle(Action);
}

public static class AlertAuditActions
{
    public const string Acknowledge = "alert.acknowledge";

    public const string Suppress = "alert.suppress";

    public const string Resolve = "alert.resolve";

    public const string ResourceType = "alert_event";

    public static string ForLifecycle(AlertLifecycleAction action) => action switch
    {
        AlertLifecycleAction.Acknowledge => Acknowledge,
        AlertLifecycleAction.Suppress => Suppress,
        AlertLifecycleAction.Resolve => Resolve,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown alert lifecycle action.")
    };
}

public sealed record AlertLifecycleTransition
{
    public AlertEventLifecycle? Lifecycle { get; init; }

    public AlertAuditOutboxEntry? Intent { get; init; }

    public bool Replayed { get; init; }
}

public sealed record AlertAuditOutboxEntry
{
    public required long OutboxId { get; init; }

    public required long EventId { get; init; }

    public required string Action { get; init; }

    public required string Actor { get; init; }

    public required string CorrelationId { get; init; }

    public required string IdempotencyKey { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public string? Note { get; init; }

    public string Details { get; init; } = string.Empty;

    public int Attempts { get; init; }

    public string? AuditId { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public bool IsCompleted => CompletedAt is not null;
}
