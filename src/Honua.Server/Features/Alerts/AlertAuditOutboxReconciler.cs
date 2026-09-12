// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Abstractions;

namespace Honua.Server.Features.Alerts;

/// <summary>
/// Drains alert domain audit intents that the request path did not complete (#3865).
/// </summary>
/// <remarks>
/// A lifecycle mutation and its audit intent commit together, so a fault or a
/// process death between the mutation and the audit write leaves a committed
/// mutation with a PENDING intent, never with nothing. This service is what makes
/// that intent deterministic: on every startup, and then on a bounded interval, it
/// records the outstanding domain audit events with their original actor, action,
/// note, timestamp and correlation id and marks the intents complete.
/// </remarks>
internal sealed partial class AlertAuditOutboxReconciler : BackgroundService
{
    /// <summary>Intents drained per pass. Alert operator actions are low volume.</summary>
    internal const int BatchSize = 100;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AlertAuditOutboxReconciler> _logger;
    private readonly TimeSpan _interval;

    public AlertAuditOutboxReconciler(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<AlertAuditOutboxReconciler> logger)
        : this(scopeFactory, timeProvider, logger, TimeSpan.FromSeconds(30))
    {
    }

    internal AlertAuditOutboxReconciler(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<AlertAuditOutboxReconciler> logger,
        TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
        _interval = interval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run once immediately: a restart is exactly the case this exists for.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Never let a reconciliation fault stop the loop: the pending intents
                // are durable and the next pass retries them.
                Log.PassFailed(_logger, ex);
            }

            try
            {
                await Task.Delay(_interval, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Completes every pending intent currently visible. Returns the number of
    /// intents whose domain audit record now exists.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal async Task<int> ReconcileOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var outbox = scope.ServiceProvider.GetService<IAlertAuditOutbox>();
        if (outbox is null)
        {
            return 0;
        }

        var completer = scope.ServiceProvider.GetRequiredService<IAlertAuditCompleter>();
        var pending = await outbox.ListPendingAsync(BatchSize, cancellationToken).ConfigureAwait(false);
        if (pending.Count == 0)
        {
            return 0;
        }

        Log.ReconcilingPending(_logger, pending.Count);

        var completed = 0;
        foreach (var intent in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await completer.CompleteAsync(intent, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                completed++;
                Log.Reconciled(_logger, intent.OutboxId, intent.Action, intent.EventId);
            }
        }

        return completed;
    }

    private static partial class Log
    {
        [LoggerMessage(9422, LogLevel.Information,
            "Reconciling {Count} pending alert domain audit intents")]
        public static partial void ReconcilingPending(ILogger logger, int count);

        [LoggerMessage(9423, LogLevel.Information,
            "Completed alert domain audit intent {OutboxId} ({Action}) for event {EventId} during reconciliation")]
        public static partial void Reconciled(ILogger logger, long outboxId, string action, long eventId);

        [LoggerMessage(9424, LogLevel.Error,
            "Alert domain audit reconciliation pass failed; pending intents are retried on the next pass")]
        public static partial void PassFailed(ILogger logger, Exception exception);
    }
}
