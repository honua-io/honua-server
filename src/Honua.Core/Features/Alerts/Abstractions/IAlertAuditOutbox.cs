// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;

namespace Honua.Core.Features.Alerts.Abstractions;

/// <summary>
/// Completion side of the alert domain audit outbox (#3865).
/// </summary>
/// <remarks>
/// Intents are CREATED by <see cref="IAlertLifecycleStore.ApplyAsync"/>, inside the
/// same transaction as the lifecycle mutation they describe. This surface is how
/// they are drained: the request path completes its own intent inline, and the
/// reconciler completes anything a crash left behind. Nothing here may create an
/// intent, because an intent created outside the lifecycle transaction would
/// reopen exactly the window the outbox exists to close.
/// </remarks>
public interface IAlertAuditOutbox
{
    /// <summary>
    /// Returns pending intents oldest-first: those whose lifecycle mutation is
    /// committed but whose domain audit record has not been written yet.
    /// </summary>
    /// <param name="limit">Maximum number of intents to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<AlertAuditOutboxEntry>> ListPendingAsync(
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one intent by its durable identity, completed or not.
    /// </summary>
    /// <param name="outboxId">Intent identity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AlertAuditOutboxEntry?> GetAsync(long outboxId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an intent completed against the durable audit identity that was
    /// assigned to its domain audit record. Idempotent: completing an already
    /// completed intent keeps the first audit identity and returns false, so a
    /// racing reconciler can never produce a second audit action.
    /// </summary>
    /// <param name="outboxId">Intent identity.</param>
    /// <param name="auditId">Durable audit identity assigned by the audit sink.</param>
    /// <param name="completedAt">Completion timestamp.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when this call completed the intent; false when it was already complete.</returns>
    Task<bool> CompleteAsync(
        long outboxId,
        string auditId,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a failed completion attempt so an intent that cannot be written is
    /// visible to operators instead of silently retried forever.
    /// </summary>
    /// <param name="outboxId">Intent identity.</param>
    /// <param name="error">Short, non-sensitive failure description.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RecordAttemptFailureAsync(
        long outboxId,
        string error,
        CancellationToken cancellationToken = default);
}
