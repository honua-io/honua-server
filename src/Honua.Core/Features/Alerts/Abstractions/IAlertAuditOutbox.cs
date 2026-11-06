// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;

namespace Honua.Core.Features.Alerts.Abstractions;

// Nothing on this surface may create an intent; an intent created outside the lifecycle transaction would reopen the gap the outbox closes.
public interface IAlertAuditOutbox
{
    Task<IReadOnlyList<AlertAuditOutboxEntry>> ListPendingAsync(
        int limit,
        CancellationToken cancellationToken = default);

    Task<AlertAuditOutboxEntry?> GetAsync(long outboxId, CancellationToken cancellationToken = default);

    // A completer must hold the lease before writing the audit record, so two completers cannot both write it.
    Task<bool> TryClaimAsync(
        long outboxId,
        Guid claimToken,
        DateTimeOffset claimedUntil,
        CancellationToken cancellationToken = default);

    // Completing an already completed intent keeps the first audit identity, so a racing reconciler cannot write a second action.
    Task<bool> CompleteAsync(
        long outboxId,
        Guid claimToken,
        string auditId,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default);

    // The failure text is non-sensitive, and backoff stops a poison intent from starving every later batch.
    Task RecordAttemptFailureAsync(
        long outboxId,
        string error,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default);
}

public interface IAlertAuditCompleter
{
    Task<bool> CompleteAsync(
        AlertAuditOutboxEntry intent,
        string? remoteIp = null,
        string? userAgent = null,
        CancellationToken cancellationToken = default);
}
