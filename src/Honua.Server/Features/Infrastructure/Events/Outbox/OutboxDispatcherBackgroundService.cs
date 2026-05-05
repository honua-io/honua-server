// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Infrastructure.Events.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Events.Outbox;

/// <summary>
/// Background worker that claims rows from <c>honua.feature_change_outbox</c> and
/// republishes them through the canonical <see cref="IFeatureChangeEventPublisher"/>
/// path that already feeds replay storage, webhook delivery, and live streaming.
/// </summary>
/// <remarks>
/// Multi-node safety relies on the repository's atomic claim primitive
/// (<c>FOR UPDATE SKIP LOCKED</c> on Postgres). Each claimed row is stamped with
/// the worker's node id and a TTL so a crashed worker does not orphan rows: the
/// recovery loop resets expired leases back to <c>pending</c> for re-claim.
/// </remarks>
internal sealed partial class OutboxDispatcherBackgroundService : BackgroundService, IOutboxHealth
{
    private readonly IServiceProvider _services;
    private readonly IOutboxCapabilityProvider _capability;
    private readonly OutboxDispatcherOptions _options;
    private readonly ILogger<OutboxDispatcherBackgroundService> _logger;
    private readonly string _nodeId;

    private DateTimeOffset _lastRecoveryAt = DateTimeOffset.MinValue;
    private DateTimeOffset? _lastDispatchAt;
    private OutboxBacklogMetrics? _lastBacklog;
    private DateTimeOffset? _lastSuccessfulPollAt;
    private DateTimeOffset? _lastStorageFailureAt;
    private int _running;

    public OutboxDispatcherBackgroundService(
        IServiceProvider services,
        IOutboxCapabilityProvider capability,
        IOptions<OutboxDispatcherOptions> options,
        ILogger<OutboxDispatcherBackgroundService> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _capability = capability ?? throw new ArgumentNullException(nameof(capability));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _nodeId = $"{Environment.MachineName}:{Environment.ProcessId}";
    }

    public bool IsDispatcherRunning => Volatile.Read(ref _running) == 1;
    public DateTimeOffset? LastDispatchAt => _lastDispatchAt;
    public OutboxBacklogMetrics? LastBacklog => _lastBacklog;
    public DateTimeOffset? LastSuccessfulPollAt => _lastSuccessfulPollAt;
    public DateTimeOffset? LastStorageFailureAt => _lastStorageFailureAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_capability.SupportsTransactionalOutbox)
        {
            Log.DispatcherSkippedNoCapability(_logger, _capability.CapabilityLimitationDescription ?? "Provider does not support transactional outbox.");
            return;
        }

        Volatile.Write(ref _running, 1);
        Log.DispatcherStarted(_logger, _nodeId);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var dispatched = await ExecuteOnceAsync(stoppingToken).ConfigureAwait(false);
                if (dispatched == 0)
                {
                    try
                    {
                        await Task.Delay(_options.IdlePollIntervalMs, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            Volatile.Write(ref _running, 0);
            Log.DispatcherStopped(_logger, _nodeId);
        }
    }

    /// <summary>
    /// Executes one dispatch pass: recovers expired claims when due, claims a batch,
    /// publishes each entry, and refreshes the backlog gauges. Exposed as internal
    /// so integration tests can drive deterministic single passes.
    /// </summary>
    internal async Task<int> ExecuteOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = _services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetService<IFeatureChangeOutboxRepository>();
        var publisher = scope.ServiceProvider.GetRequiredService<IFeatureChangeEventPublisher>();

        if (repository is null)
        {
            return 0;
        }

        await MaybeRecoverExpiredClaimsAsync(repository, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<FeatureChangeOutboxEntry> claimed;
        try
        {
            claimed = await repository.ClaimPendingAsync(
                _nodeId,
                _options.BatchSize,
                TimeSpan.FromSeconds(_options.ClaimTtlSeconds),
                cancellationToken).ConfigureAwait(false);
            RecordStoragePollSuccess();
        }
        catch (Exception ex)
        {
            Log.ClaimFailed(_logger, ex);
            RecordStoragePollFailure();
            // Still try to refresh the backlog snapshot so health reflects the latest
            // state if backlog reads still work; if both fail, both failures are recorded.
            await UpdateBacklogAsync(repository, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var dispatched = 0;
        foreach (var entry in claimed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryDispatchAsync(repository, publisher, entry, cancellationToken).ConfigureAwait(false))
            {
                dispatched++;
            }
        }

        await UpdateBacklogAsync(repository, cancellationToken).ConfigureAwait(false);
        return dispatched;
    }

    private async Task<bool> TryDispatchAsync(
        IFeatureChangeOutboxRepository repository,
        IFeatureChangeEventPublisher publisher,
        FeatureChangeOutboxEntry entry,
        CancellationToken cancellationToken)
    {
        // ClaimPendingAsync stamps claim_node_id on the returned entry; the dispatcher
        // must thread that token back into terminal updates so a row whose lease was
        // recovered by another worker is never overwritten by this one.
        var ownerNodeId = entry.ClaimNodeId ?? _nodeId;

        FeatureChangeEventRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(
                entry.EventPayload,
                FeatureChangeEventsJsonContext.Default.FeatureChangeEventRequest);
        }
        catch (Exception ex)
        {
            // Payload corruption is permanent — mark as failed and let the retry budget burn out
            // so the dead-letter health check surfaces the row for operator review.
            Log.PayloadDecodeFailed(_logger, entry.OutboxId, ex);
            await SafeMarkFailedAsync(repository, entry.OutboxId, ownerNodeId, "Outbox payload decode failed.", cancellationToken).ConfigureAwait(false);
            OutboxMetrics.Failed.Add(1);
            return false;
        }

        if (request is null)
        {
            await SafeMarkFailedAsync(repository, entry.OutboxId, ownerNodeId, "Outbox payload deserialized to null.", cancellationToken).ConfigureAwait(false);
            OutboxMetrics.Failed.Add(1);
            return false;
        }

        try
        {
            // Strict publish: a failed durable append must not be silently traded for a
            // best-effort retry-queue enqueue. The outbox row stays claimed/failed and is
            // re-dispatched on the next pass.
            await publisher.PublishStrictAsync(request, cancellationToken).ConfigureAwait(false);
            var marked = await repository.MarkDispatchedAsync(entry.OutboxId, ownerNodeId, cancellationToken).ConfigureAwait(false);
            if (!marked)
            {
                // The lease was recovered (or reclaimed) before our update landed. Do not
                // count this as a dispatch — the new owner will publish again and update
                // the row's terminal state itself. Telemetry surfaces the recovered claim
                // so operators can correlate with dispatcher pauses.
                Log.StaleClaimAfterDispatch(_logger, entry.OutboxId, ownerNodeId);
                return false;
            }

            _lastDispatchAt = DateTimeOffset.UtcNow;
            OutboxMetrics.Dispatched.Add(1);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Allow the row's claim TTL to expire so another worker (or this one on the next pass)
            // can re-claim the row without us having to make a writable call during shutdown.
            throw;
        }
        catch (Exception ex)
        {
            Log.DispatchFailed(_logger, entry.OutboxId, entry.RetryCount + 1, ex);
            await SafeMarkFailedAsync(repository, entry.OutboxId, ownerNodeId, BuildSafeError(ex), cancellationToken).ConfigureAwait(false);
            // The repository transitions to dead_lettered when retries are exhausted; we count
            // the eventual transition by inspecting the post-update retry count + status, but
            // for simplicity emit failed_total here and dead_lettered_total once the next
            // backlog snapshot reflects the row in dead_lettered state.
            if (entry.RetryCount + 1 >= _options.MaxRetries)
            {
                OutboxMetrics.DeadLettered.Add(1);
            }
            else
            {
                OutboxMetrics.Failed.Add(1);
            }
            return false;
        }
    }

    private async Task SafeMarkFailedAsync(
        IFeatureChangeOutboxRepository repository,
        Guid outboxId,
        string ownerNodeId,
        string error,
        CancellationToken cancellationToken)
    {
        try
        {
            var marked = await repository.MarkFailedAsync(outboxId, ownerNodeId, error, _options.MaxRetries, cancellationToken).ConfigureAwait(false);
            if (!marked)
            {
                // Stale claim — another worker now owns this row's lifecycle. Skip the
                // bookkeeping silently rather than retrying so we do not race the new owner.
                Log.StaleClaimAfterFailure(_logger, outboxId, ownerNodeId);
            }
        }
        catch (Exception ex)
        {
            Log.MarkFailedErrored(_logger, outboxId, ex);
        }
    }

    private async Task MaybeRecoverExpiredClaimsAsync(IFeatureChangeOutboxRepository repository, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastRecoveryAt < TimeSpan.FromSeconds(_options.RecoveryIntervalSeconds))
        {
            return;
        }

        try
        {
            var recovered = await repository.RecoverExpiredClaimsAsync(cancellationToken).ConfigureAwait(false);
            if (recovered > 0)
            {
                OutboxMetrics.RecoveredClaims.Add(recovered);
                Log.ClaimsRecovered(_logger, recovered);
            }
            RecordStoragePollSuccess();
        }
        catch (Exception ex)
        {
            Log.RecoveryFailed(_logger, ex);
            RecordStoragePollFailure();
        }
        finally
        {
            _lastRecoveryAt = now;
        }
    }

    private async Task UpdateBacklogAsync(IFeatureChangeOutboxRepository repository, CancellationToken cancellationToken)
    {
        try
        {
            var backlog = await repository.GetBacklogMetricsAsync(cancellationToken).ConfigureAwait(false);
            _lastBacklog = backlog;
            OutboxMetrics.RecordBacklog(backlog.PendingCount, backlog.DeadLetteredCount, backlog.OldestPendingAgeSeconds);
            RecordStoragePollSuccess();
        }
        catch (Exception ex)
        {
            Log.BacklogQueryFailed(_logger, ex);
            RecordStoragePollFailure();
        }
    }

    /// <summary>
    /// Stamp the most recent successful storage poll. Both timestamps are kept and
    /// never auto-cleared so the readiness probe can compare them: a pending failure
    /// (failure timestamp newer than success) flags the dispatcher; once a success
    /// follows the failure, the timestamp comparison naturally clears the flag.
    /// </summary>
    private void RecordStoragePollSuccess()
    {
        _lastSuccessfulPollAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Stamp the most recent storage poll failure. Health surfaces this as Degraded
    /// when a prior pass had succeeded, or as Unhealthy when no pass has succeeded yet
    /// (cold start where the table or permissions are missing) so dispatcher stalls do
    /// not silently accumulate pending events behind a Healthy readiness probe.
    /// </summary>
    private void RecordStoragePollFailure()
    {
        _lastStorageFailureAt = DateTimeOffset.UtcNow;
    }

    private static string BuildSafeError(Exception ex)
    {
        // Avoid leaking nested exception details (stack frames, SQL state, connection strings).
        var message = ex.GetType().Name + ": " + ex.Message;
        return message.Length > 1024 ? message[..1024] : message;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 9300, Level = LogLevel.Information, Message = "Outbox dispatcher started on node {NodeId}.")]
        public static partial void DispatcherStarted(ILogger logger, string nodeId);

        [LoggerMessage(EventId = 9301, Level = LogLevel.Information, Message = "Outbox dispatcher stopped on node {NodeId}.")]
        public static partial void DispatcherStopped(ILogger logger, string nodeId);

        [LoggerMessage(EventId = 9302, Level = LogLevel.Information, Message = "Outbox dispatcher disabled: {Reason}")]
        public static partial void DispatcherSkippedNoCapability(ILogger logger, string reason);

        [LoggerMessage(EventId = 9303, Level = LogLevel.Warning, Message = "Outbox claim pass failed.")]
        public static partial void ClaimFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 9304, Level = LogLevel.Warning, Message = "Outbox dispatch failed for row {OutboxId} (attempt {Attempt}).")]
        public static partial void DispatchFailed(ILogger logger, Guid outboxId, int attempt, Exception exception);

        [LoggerMessage(EventId = 9305, Level = LogLevel.Warning, Message = "Outbox payload could not be decoded for row {OutboxId}.")]
        public static partial void PayloadDecodeFailed(ILogger logger, Guid outboxId, Exception exception);

        [LoggerMessage(EventId = 9306, Level = LogLevel.Warning, Message = "Outbox MarkFailed call errored for row {OutboxId}.")]
        public static partial void MarkFailedErrored(ILogger logger, Guid outboxId, Exception exception);

        [LoggerMessage(EventId = 9307, Level = LogLevel.Information, Message = "Outbox claim recovery reset {Count} expired leases.")]
        public static partial void ClaimsRecovered(ILogger logger, int count);

        [LoggerMessage(EventId = 9308, Level = LogLevel.Warning, Message = "Outbox claim recovery failed.")]
        public static partial void RecoveryFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 9309, Level = LogLevel.Debug, Message = "Outbox backlog query failed.")]
        public static partial void BacklogQueryFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 9310, Level = LogLevel.Information, Message = "Outbox row {OutboxId} no longer owned by node {OwnerNodeId} when marking dispatched; another worker will re-publish.")]
        public static partial void StaleClaimAfterDispatch(ILogger logger, Guid outboxId, string ownerNodeId);

        [LoggerMessage(EventId = 9311, Level = LogLevel.Information, Message = "Outbox row {OutboxId} no longer owned by node {OwnerNodeId} when recording failure; new owner will record terminal state.")]
        public static partial void StaleClaimAfterFailure(ILogger logger, Guid outboxId, string ownerNodeId);
    }
}
