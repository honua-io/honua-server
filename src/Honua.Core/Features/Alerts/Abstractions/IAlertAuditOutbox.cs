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
    /// committed, whose domain audit record has not been written yet, and whose
    /// completion lease (if any) has expired.
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
    /// Takes a bounded completion lease on a pending intent.
    /// </summary>
    /// <remarks>
    /// A completer must hold the lease before it writes the intent's audit record,
    /// so the request path and the reconciler - or two reconcilers - cannot both
    /// write one. The lease expires so a claimant that dies does not strand the
    /// intent; the residual duplicate window is a process death between the audit
    /// write and <see cref="CompleteAsync"/>, which leaves two truthful records
    /// rather than a missing one.
    /// </remarks>
    /// <param name="outboxId">Intent identity.</param>
    /// <param name="claimedUntil">When the lease expires.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when this caller now owns the intent's completion.</returns>
    Task<bool> TryClaimAsync(
        long outboxId,
        DateTimeOffset claimedUntil,
        CancellationToken cancellationToken = default);

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

/// <summary>
/// Completes an alert domain audit intent: records the audit event the intent
/// describes and marks it complete (#3865).
/// </summary>
/// <remarks>
/// The lifecycle endpoints complete their own intent inline so the audit record
/// normally lands within the operator's request, and the alert reconciler
/// completes whatever a fault or a process death left behind. Both routes replay
/// the intent's ORIGINAL actor, action, note, timestamp and correlation id, so
/// lifecycle evidence and audit evidence agree regardless of which one wrote the
/// record. The abstraction lives in Core so the Admin endpoints can drive
/// completion without reaching into the Alerts feature.
/// </remarks>
public interface IAlertAuditCompleter
{
    /// <summary>
    /// Records the domain audit event for one intent and marks it complete.
    /// </summary>
    /// <param name="intent">The pending intent.</param>
    /// <param name="remoteIp">Optional caller address when completing inline on a request.</param>
    /// <param name="userAgent">Optional caller user agent when completing inline on a request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the domain audit record now exists for this intent.</returns>
    Task<bool> CompleteAsync(
        AlertAuditOutboxEntry intent,
        string? remoteIp = null,
        string? userAgent = null,
        CancellationToken cancellationToken = default);
}
