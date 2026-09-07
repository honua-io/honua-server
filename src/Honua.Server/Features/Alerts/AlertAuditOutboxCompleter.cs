// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.AuditLog.Abstractions;

namespace Honua.Server.Features.Alerts;

/// <summary>
/// Completes alert domain audit intents: records the audit event described by a
/// durable intent and marks the intent complete (#3865).
/// </summary>
/// <remarks>
/// Used by the request path (which completes its own intent inline so the audit
/// record normally lands within the operator's request) and by
/// <see cref="AlertAuditOutboxReconciler"/> (which completes whatever a fault or a
/// process death left behind). Both routes replay the intent's ORIGINAL actor,
/// action, note, timestamp and correlation id, so lifecycle evidence and audit
/// evidence agree regardless of which one wrote the record.
/// </remarks>
internal sealed partial class AlertAuditOutboxCompleter : IAlertAuditCompleter
{
    /// <summary>
    /// How long a completer owns an intent. Long enough for a slow audit sink,
    /// short enough that a dead claimant is retried promptly.
    /// </summary>
    internal static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(2);

    private readonly IAuditLog _auditLog;
    private readonly IAlertAuditOutbox _outbox;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AlertAuditOutboxCompleter> _logger;

    public AlertAuditOutboxCompleter(
        IAuditLog auditLog,
        IAlertAuditOutbox outbox,
        TimeProvider timeProvider,
        ILogger<AlertAuditOutboxCompleter> logger)
    {
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _auditLog = auditLog;
        _outbox = outbox;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> CompleteAsync(
        AlertAuditOutboxEntry intent,
        string? remoteIp = null,
        string? userAgent = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.IsCompleted)
        {
            return true;
        }

        // Claim before writing: the request path and the reconciler both complete
        // intents, and only the lease holder may write the domain audit record.
        var now = _timeProvider.GetUtcNow();
        if (!await _outbox.TryClaimAsync(intent.OutboxId, now + ClaimLease, cancellationToken).ConfigureAwait(false))
        {
            Log.AlreadyClaimed(_logger, intent.OutboxId, intent.Action);
            return false;
        }

        var auditEvent = new AuditEvent
        {
            // The intent's timestamp, not "now": the audit record describes when the
            // lifecycle transition happened, not when the record was finally written.
            Timestamp = intent.OccurredAt,
            EventType = AuditEventType.AdminAction,
            Actor = intent.Actor,
            ActorType = AuditActorType.UserId,
            ResourceType = AlertAuditActions.ResourceType,
            ResourceId = intent.EventId.ToString(CultureInfo.InvariantCulture),
            Action = intent.Action,
            Outcome = AuditOutcome.Success,
            CorrelationId = intent.CorrelationId,
            RemoteIp = remoteIp,
            UserAgent = userAgent,
            Details = intent.Details
        };

        string? auditId;
        try
        {
            auditId = await _auditLog.RecordAsync(auditEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.AuditWriteFailed(_logger, intent.OutboxId, intent.Action, ex);
            await _outbox.RecordAttemptFailureAsync(intent.OutboxId, ex.GetType().Name, cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        if (auditId is null)
        {
            // A non-persisted or failing sink assigned no identity. Leave the intent
            // pending so reconciliation retries it rather than declaring an audit
            // record that does not exist.
            Log.AuditNotPersisted(_logger, intent.OutboxId, intent.Action);
            await _outbox.RecordAttemptFailureAsync(intent.OutboxId, "audit sink assigned no identity", cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        await _outbox.CompleteAsync(intent.OutboxId, auditId, _timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private static partial class Log
    {
        [LoggerMessage(9420, LogLevel.Error,
            "Alert domain audit intent {OutboxId} ({Action}) could not be recorded; it stays pending for reconciliation")]
        public static partial void AuditWriteFailed(ILogger logger, long outboxId, string action, Exception exception);

        [LoggerMessage(9425, LogLevel.Debug,
            "Alert domain audit intent {OutboxId} ({Action}) is already claimed by another completer")]
        public static partial void AlreadyClaimed(ILogger logger, long outboxId, string action);

        [LoggerMessage(9421, LogLevel.Warning,
            "Alert domain audit intent {OutboxId} ({Action}) was not durably persisted; it stays pending for reconciliation")]
        public static partial void AuditNotPersisted(ILogger logger, long outboxId, string action);
    }
}
