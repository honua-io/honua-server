// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Alerts.Domain;

/// <summary>
/// The operator lifecycle mutations that carry a required alert domain audit
/// action. Shared middleware request audit does NOT substitute for these: the
/// control-plane integrity boundary is the domain action, not the HTTP call.
/// </summary>
public enum AlertLifecycleAction
{
    /// <summary>Operator acknowledged the event (<c>alert.acknowledge</c>).</summary>
    Acknowledge = 0,

    /// <summary>Operator suppressed the event until an expiry (<c>alert.suppress</c>).</summary>
    Suppress = 1,

    /// <summary>Operator resolved the event (<c>alert.resolve</c>).</summary>
    Resolve = 2,
}

/// <summary>
/// One operator lifecycle mutation plus the audit intent that must accompany it.
/// </summary>
/// <remarks>
/// The store applies both in a single transaction, so a committed mutation can
/// never be externally observable without either its domain audit record or a
/// durable reconciliation record that deterministically completes it (#3865).
/// </remarks>
public sealed record AlertLifecycleCommand
{
    /// <summary>Event the operator acted on.</summary>
    public required long EventId { get; init; }

    /// <summary>Which lifecycle mutation to apply.</summary>
    public required AlertLifecycleAction Action { get; init; }

    /// <summary>Stable identifier of the acting operator.</summary>
    public required string Actor { get; init; }

    /// <summary>Timestamp of the action. The caller supplies it so a retry replays it.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>Request correlation id, carried onto both evidence trails.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>
    /// Retry identity. Two requests carrying the same key are the SAME operator
    /// action: they must produce one logical lifecycle transition and one domain
    /// audit action, never duplicates.
    /// </summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>Optional operator note.</summary>
    public string? Note { get; init; }

    /// <summary>Suppression expiry. Required for <see cref="AlertLifecycleAction.Suppress"/>.</summary>
    public DateTimeOffset? SuppressUntil { get; init; }

    /// <summary>Action-specific audit detail payload, persisted verbatim.</summary>
    public string Details { get; init; } = string.Empty;

    /// <summary>The canonical audit action name for <see cref="Action"/>.</summary>
    public string AuditAction => AlertAuditActions.ForLifecycle(Action);
}

/// <summary>Canonical alert domain audit action names.</summary>
public static class AlertAuditActions
{
    /// <summary>Audit action recorded for an acknowledge.</summary>
    public const string Acknowledge = "alert.acknowledge";

    /// <summary>Audit action recorded for a suppress.</summary>
    public const string Suppress = "alert.suppress";

    /// <summary>Audit action recorded for a resolve.</summary>
    public const string Resolve = "alert.resolve";

    /// <summary>Audit resource type every alert domain action is recorded against.</summary>
    public const string ResourceType = "alert_event";

    /// <summary>Maps a lifecycle mutation to its canonical audit action name.</summary>
    /// <param name="action">Lifecycle mutation.</param>
    public static string ForLifecycle(AlertLifecycleAction action) => action switch
    {
        AlertLifecycleAction.Acknowledge => Acknowledge,
        AlertLifecycleAction.Suppress => Suppress,
        AlertLifecycleAction.Resolve => Resolve,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown alert lifecycle action.")
    };
}

/// <summary>
/// Outcome of applying an <see cref="AlertLifecycleCommand"/>.
/// </summary>
public sealed record AlertLifecycleTransition
{
    /// <summary>The lifecycle row after the mutation, or null when the event does not exist.</summary>
    public AlertEventLifecycle? Lifecycle { get; init; }

    /// <summary>The durable audit intent written alongside the mutation.</summary>
    public AlertAuditOutboxEntry? Intent { get; init; }

    /// <summary>
    /// True when the command's idempotency key had already been applied, so this
    /// request replayed an existing transition instead of creating a second one.
    /// </summary>
    public bool Replayed { get; init; }
}

/// <summary>
/// A durable intent to record one alert domain audit action, written in the same
/// transaction as the lifecycle mutation it describes.
/// </summary>
public sealed record AlertAuditOutboxEntry
{
    /// <summary>Durable identity of the intent.</summary>
    public required long OutboxId { get; init; }

    /// <summary>Event the operator acted on.</summary>
    public required long EventId { get; init; }

    /// <summary>Canonical audit action name (see <see cref="AlertAuditActions"/>).</summary>
    public required string Action { get; init; }

    /// <summary>Stable identifier of the acting operator.</summary>
    public required string Actor { get; init; }

    /// <summary>Request correlation id.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>Retry identity of the originating operator action.</summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>Timestamp of the lifecycle mutation this intent describes.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>Optional operator note.</summary>
    public string? Note { get; init; }

    /// <summary>Action-specific audit detail payload.</summary>
    public string Details { get; init; } = string.Empty;

    /// <summary>Durable audit identity once the intent has been completed.</summary>
    public string? AuditId { get; init; }

    /// <summary>When the intent was completed, or null while it is still pending.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Whether the domain audit record for this intent already exists.</summary>
    public bool IsCompleted => CompletedAt is not null;
}
