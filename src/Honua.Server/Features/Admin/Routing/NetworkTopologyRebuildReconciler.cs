// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Routing.Features.Routing.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Honua.Server.Features.Admin.Routing;

/// <summary>
/// Self-healing reconciliation for isolated shadow-topology rebuild attempts (#2720):
/// finds attempts whose fencing lease has expired and either lets them become eligible for
/// takeover (re-enqueuing the durable job when it is still active so a fresh worker claims
/// it and calls <see cref="INetworkTopologyRebuildStore.TryAcquireOrTakeoverLeaseAsync"/>),
/// or fails the attempt with a stable sanitized code and cleans its orphan shadow artifacts
/// when the owning execution job has already reached a terminal failure state. Never
/// touches the active generation or a rollback-eligible retired generation's artifacts —
/// only attempt-scoped shadow tables for non-terminal or newly-failed attempts.
/// </summary>
internal sealed partial class NetworkTopologyRebuildReconciler
{
    private const string OrphanedFailureCode = "routing.topology.rebuild_orphaned";

    private readonly INetworkTopologyRebuildStore _rebuildStore;
    private readonly IExecutionJobStore _jobStore;
    private readonly IJobQueue? _jobQueue;
    private readonly ILogger<NetworkTopologyRebuildReconciler> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NetworkTopologyRebuildReconciler"/> class.
    /// </summary>
    public NetworkTopologyRebuildReconciler(
        INetworkTopologyRebuildStore rebuildStore,
        IExecutionJobStore jobStore,
        ILogger<NetworkTopologyRebuildReconciler> logger,
        IJobQueue? jobQueue = null)
    {
        _rebuildStore = rebuildStore ?? throw new ArgumentNullException(nameof(rebuildStore));
        _jobStore = jobStore ?? throw new ArgumentNullException(nameof(jobStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _jobQueue = jobQueue;
    }

    /// <summary>
    /// Runs one reconciliation pass. Returns the number of attempts adopted (requeued for
    /// takeover or failed-and-cleaned).
    /// </summary>
    public async Task<int> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var expired = await _rebuildStore.ListExpiredLeasesAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        var adopted = 0;

        foreach (var attempt in expired)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var job = await _jobStore.GetAsync(attempt.OperationId, cancellationToken).ConfigureAwait(false);
            if (job is null || IsTerminalFailure(job.Status))
            {
                var failed = await _rebuildStore.TryFailAsync(
                        attempt.DatasetId, attempt.Generation, attempt.Attempt, attempt.FencingToken,
                        OrphanedFailureCode, cancellationToken)
                    .ConfigureAwait(false);
                if (failed)
                {
                    await _rebuildStore.CleanupOrphanShadowArtifactsAsync(
                            attempt.DatasetId, attempt.Generation, keepAttempt: null, cancellationToken)
                        .ConfigureAwait(false);
                    Log.AttemptOrphanedAndFailed(_logger, attempt.DatasetId, attempt.Generation, attempt.Attempt);
                    adopted++;
                }

                continue;
            }

            if (_jobQueue is not null && job.Status is ExecutionJobStatus.Queued or ExecutionJobStatus.Provisioning or ExecutionJobStatus.Running)
            {
                // The durable job is still (nominally) active but our lease expired without a
                // heartbeat — most likely a crashed or partitioned worker. Re-enqueue so a fresh
                // worker claims it; the fencing token protects against the old owner's writes
                // landing after a new owner takes over via TryAcquireOrTakeoverLeaseAsync.
                await _jobQueue.RequeueAsync(attempt.OperationId, cancellationToken: cancellationToken).ConfigureAwait(false);
                Log.AttemptRequeuedForTakeover(_logger, attempt.DatasetId, attempt.Generation, attempt.Attempt);
                adopted++;
            }
        }

        return adopted;
    }

    private static bool IsTerminalFailure(ExecutionJobStatus status)
        => status is ExecutionJobStatus.Failed or ExecutionJobStatus.Cancelled;

    private static partial class Log
    {
        [LoggerMessage(9280, LogLevel.Warning,
            "Topology rebuild reconciler failed orphaned attempt for dataset '{DatasetId}' generation {Generation} attempt {Attempt}")]
        public static partial void AttemptOrphanedAndFailed(ILogger logger, string datasetId, long generation, long attempt);

        [LoggerMessage(9281, LogLevel.Information,
            "Topology rebuild reconciler requeued expired-lease attempt for dataset '{DatasetId}' generation {Generation} attempt {Attempt} for takeover")]
        public static partial void AttemptRequeuedForTakeover(ILogger logger, string datasetId, long generation, long attempt);
    }
}

/// <summary>
/// Periodically runs <see cref="NetworkTopologyRebuildReconciler"/> so an expired rebuild
/// lease is adopted without requiring an operator to notice and intervene manually (#2720).
/// </summary>
internal sealed partial class NetworkTopologyRebuildReconcilerBackgroundService : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NetworkTopologyRebuildReconcilerBackgroundService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NetworkTopologyRebuildReconcilerBackgroundService"/> class.
    /// </summary>
    public NetworkTopologyRebuildReconcilerBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<NetworkTopologyRebuildReconcilerBackgroundService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ReconcileInterval);
        do
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var reconciler = scope.ServiceProvider.GetService<NetworkTopologyRebuildReconciler>();
                if (reconciler is not null)
                {
                    var adopted = await reconciler.ReconcileAsync(stoppingToken).ConfigureAwait(false);
                    if (adopted > 0)
                    {
                        Log.ReconciliationPassAdopted(_logger, adopted);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.ReconciliationPassFailed(_logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private static partial class Log
    {
        [LoggerMessage(9282, LogLevel.Information,
            "Topology rebuild reconciliation pass adopted {Count} expired-lease attempts")]
        public static partial void ReconciliationPassAdopted(ILogger logger, int count);

        [LoggerMessage(9283, LogLevel.Error,
            "Topology rebuild reconciliation pass failed")]
        public static partial void ReconciliationPassFailed(ILogger logger, Exception exception);
    }
}
