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
    // Per-kind storage poll timestamps. A shared timestamp pair would mask the
    // kind that is still failing whenever a different kind succeeded later in the
    // same pass: e.g. claim throws → recorded failure; backlog read succeeds →
    // recorded success that post-dates the claim failure → readiness probe sees
    // "no pending failure" even though the claim path is still broken. Tracking
    // per-kind keeps each path's pending-failure status independent.
    private DateTimeOffset? _lastClaimPollSuccessAt;
    private DateTimeOffset? _lastClaimPollFailureAt;
    private DateTimeOffset? _lastRecoveryPollSuccessAt;
    private DateTimeOffset? _lastRecoveryPollFailureAt;
    private DateTimeOffset? _lastBacklogPollSuccessAt;
    private DateTimeOffset? _lastBacklogPollFailureAt;
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

    public DateTimeOffset? LastClaimPollSuccessAt => _lastClaimPollSuccessAt;
    public DateTimeOffset? LastClaimPollFailureAt => _lastClaimPollFailureAt;
    public DateTimeOffset? LastRecoveryPollSuccessAt => _lastRecoveryPollSuccessAt;
    public DateTimeOffset? LastRecoveryPollFailureAt => _lastRecoveryPollFailureAt;
    public DateTimeOffset? LastBacklogPollSuccessAt => _lastBacklogPollSuccessAt;
    public DateTimeOffset? LastBacklogPollFailureAt => _lastBacklogPollFailureAt;

    public bool IsStoragePollFailing =>
        IsKindFailing(_lastClaimPollFailureAt, _lastClaimPollSuccessAt)
        || IsKindFailing(_lastRecoveryPollFailureAt, _lastRecoveryPollSuccessAt)
        || IsKindFailing(_lastBacklogPollFailureAt, _lastBacklogPollSuccessAt);

    public DateTimeOffset? LastSuccessfulPollAt =>
        Latest(_lastClaimPollSuccessAt, _lastRecoveryPollSuccessAt, _lastBacklogPollSuccessAt);

    public DateTimeOffset? LastStorageFailureAt =>
        Latest(_lastClaimPollFailureAt, _lastRecoveryPollFailureAt, _lastBacklogPollFailureAt);

    private static bool IsKindFailing(DateTimeOffset? failureAt, DateTimeOffset? successAt)
    {
        if (failureAt is null)
        {
            return false;
        }

        return successAt is null || failureAt.Value > successAt.Value;
    }

    private static DateTimeOffset? Latest(DateTimeOffset? a, DateTimeOffset? b, DateTimeOffset? c)
    {
        DateTimeOffset? best = null;
        if (a is { } av && (best is null || av > best.Value)) best = av;
        if (b is { } bv && (best is null || bv > best.Value)) best = bv;
        if (c is { } cv && (best is null || cv > best.Value)) best = cv;
        return best;
    }

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
            _lastClaimPollSuccessAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            Log.ClaimFailed(_logger, ex);
            _lastClaimPollFailureAt = DateTimeOffset.UtcNow;
            // Still try to refresh the backlog snapshot so health reflects the latest
            // state if backlog reads still work. The backlog success does not clear
            // the pending claim-poll failure because each kind tracks its own
            // timestamps; the readiness probe surfaces the still-failing claim path
            // even when the backlog read succeeded after it.
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
            var outcome = await SafeMarkFailedAsync(repository, entry.OutboxId, ownerNodeId, entry.RetryCount, "Outbox payload decode failed.", cancellationToken).ConfigureAwait(false);
            RecordTerminalOutcomeMetric(outcome);
            return false;
        }

        if (request is null)
        {
            var outcome = await SafeMarkFailedAsync(repository, entry.OutboxId, ownerNodeId, entry.RetryCount, "Outbox payload deserialized to null.", cancellationToken).ConfigureAwait(false);
            RecordTerminalOutcomeMetric(outcome);
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
            var outcome = await SafeMarkFailedAsync(repository, entry.OutboxId, ownerNodeId, entry.RetryCount, BuildSafeError(ex), cancellationToken).ConfigureAwait(false);
            // Counter source-of-truth is the terminal outcome of MarkFailedAsync, not a
            // pre-call prediction: a stale-claim outcome must not double-count with the
            // new owner's eventual terminal record, and an Errored outcome leaves the row
            // unchanged for the next pass to retry.
            RecordTerminalOutcomeMetric(outcome);
            return false;
        }
    }

    /// <summary>
    /// Outcome of a <see cref="IFeatureChangeOutboxRepository.MarkFailedAsync"/> call,
    /// used by the dispatcher to decide which counter to increment so a stale-claim
    /// no-op or a thrown <c>MarkFailedAsync</c> never inflates failure/dead-letter
    /// counts beyond the rows that actually transitioned terminal state.
    /// </summary>
    private enum MarkFailedOutcome
    {
        /// <summary>Row transitioned to <c>failed</c>; retries remain.</summary>
        Failed,
        /// <summary>Row transitioned to <c>dead_lettered</c> because the retry budget was exhausted.</summary>
        DeadLettered,
        /// <summary>Repository returned <c>false</c>; the claim was reset/reclaimed before the terminal update could land.</summary>
        StaleClaim,
        /// <summary>Repository threw; the row remains <c>claimed</c> and the next pass (or recovery) will retry.</summary>
        Errored,
    }

    private async Task<MarkFailedOutcome> SafeMarkFailedAsync(
        IFeatureChangeOutboxRepository repository,
        Guid outboxId,
        string ownerNodeId,
        int currentRetryCount,
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
                return MarkFailedOutcome.StaleClaim;
            }

            // Mirror the repository's CASE expression: retry_count + 1 >= max_retries
            // transitions the row to dead_lettered. Computing this from the inputs the
            // repository has already validated avoids a second round trip while keeping
            // the outcome aligned with the persisted terminal state.
            return currentRetryCount + 1 >= _options.MaxRetries
                ? MarkFailedOutcome.DeadLettered
                : MarkFailedOutcome.Failed;
        }
        catch (Exception ex)
        {
            Log.MarkFailedErrored(_logger, outboxId, ex);
            return MarkFailedOutcome.Errored;
        }
    }

    private static void RecordTerminalOutcomeMetric(MarkFailedOutcome outcome)
    {
        switch (outcome)
        {
            case MarkFailedOutcome.Failed:
                OutboxMetrics.Failed.Add(1);
                break;
            case MarkFailedOutcome.DeadLettered:
                OutboxMetrics.DeadLettered.Add(1);
                break;
            // StaleClaim and Errored: the row's terminal state belongs elsewhere
            // (a different node, or a future pass), so this dispatcher must not
            // count it. The eventual owner records its own outcome.
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
            _lastRecoveryPollSuccessAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            Log.RecoveryFailed(_logger, ex);
            _lastRecoveryPollFailureAt = DateTimeOffset.UtcNow;
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
            _lastBacklogPollSuccessAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            Log.BacklogQueryFailed(_logger, ex);
            _lastBacklogPollFailureAt = DateTimeOffset.UtcNow;
        }
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
