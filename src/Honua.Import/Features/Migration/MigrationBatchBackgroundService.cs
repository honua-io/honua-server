// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Migration.Abstractions;
using Microsoft.Extensions.Hosting;

namespace Honua.Migration;

/// <summary>
/// Issue #1253. Drives footprint-driven batch migration runs forward. On each
/// tick the leader node loads the active batches and calls
/// <see cref="IMigrationBatchOrchestrator.AdvanceAsync"/> for each, which
/// reconciles child import-job status, queues the next ready child, rolls up
/// batch progress, and applies relationships once all children publish (#1256).
/// </summary>
/// <remarks>
/// Advancing is idempotent and gated by the shared import leader election so
/// exactly one node advances a batch at a time. When leadership is unavailable
/// (no Redis, dev profile) the local fallback leader still advances batches.
/// </remarks>
internal sealed partial class MigrationBatchBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDistributedImportJobManager _jobManager;
    private readonly ILogger<MigrationBatchBackgroundService> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(5);

    public MigrationBatchBackgroundService(
        IServiceScopeFactory scopeFactory,
        IDistributedImportJobManager jobManager,
        ILogger<MigrationBatchBackgroundService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _jobManager = jobManager ?? throw new ArgumentNullException(nameof(jobManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.ServiceStarting(_logger, _jobManager.LeaderElection.InstanceId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_pollInterval, stoppingToken).ConfigureAwait(false);

                var isLeader = await _jobManager.LeaderElection.TryAcquireLeadershipAsync(stoppingToken).ConfigureAwait(false);
                if (!isLeader)
                {
                    continue;
                }

                await AdvanceActiveBatchesAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.AdvanceLoopError(_logger, ex);
            }
        }

        Log.ServiceStopped(_logger, _jobManager.LeaderElection.InstanceId);
    }

    private async Task AdvanceActiveBatchesAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IMigrationBatchRunCatalog>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IMigrationBatchOrchestrator>();

        var activeIds = await catalog.GetActiveBatchIdsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var batchId in activeIds)
        {
            try
            {
                await orchestrator.AdvanceAsync(batchId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.BatchAdvanceError(_logger, batchId, ex);
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(7984, LogLevel.Information, "Migration batch background service starting (instance: {InstanceId})")]
        public static partial void ServiceStarting(ILogger logger, string instanceId);

        [LoggerMessage(7985, LogLevel.Information, "Migration batch background service stopped (instance: {InstanceId})")]
        public static partial void ServiceStopped(ILogger logger, string instanceId);

        [LoggerMessage(7986, LogLevel.Error, "Error in migration batch advance loop")]
        public static partial void AdvanceLoopError(ILogger logger, Exception exception);

        [LoggerMessage(7987, LogLevel.Warning, "Failed to advance migration batch {BatchId}")]
        public static partial void BatchAdvanceError(ILogger logger, string batchId, Exception exception);
    }
}
