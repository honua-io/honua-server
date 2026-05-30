// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Alerts;

internal sealed partial class AlertEvaluationBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILeaderElectionStrategy _leaderElection;
    private readonly AlertOptions _options;
    private readonly ILogger<AlertEvaluationBackgroundService> _logger;

    public AlertEvaluationBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILeaderElectionStrategy leaderElection,
        IOptions<AlertOptions> options,
        ILogger<AlertEvaluationBackgroundService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _leaderElection = leaderElection ?? throw new ArgumentNullException(nameof(leaderElection));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            LogDisabled(_logger);
            return;
        }

        var workerName = _options.Evaluation.WorkerName;
        var checkpoint = await GetCheckpointAsync(workerName, stoppingToken).ConfigureAwait(false);

        LogStarting(_logger, workerName, checkpoint.LastGeneration);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var isLeader = await _leaderElection.TryAcquireAsync(stoppingToken).ConfigureAwait(false);
                if (!isLeader)
                {
                    await Task.Delay(_options.Evaluation.IdleDelay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await using var scope = _scopeFactory.CreateAsyncScope();
                var pipeline = scope.ServiceProvider.GetRequiredService<IAlertPipeline>();
                var checkpointStore = scope.ServiceProvider.GetRequiredService<IAlertCheckpointStore>();

                var nextGeneration = await pipeline
                    .ProcessChangesAsync(checkpoint.LastGeneration, _options.Evaluation.ChangeBatchSize, stoppingToken)
                    .ConfigureAwait(false);

                var now = DateTimeOffset.UtcNow;
                var shouldSweep = !checkpoint.LastDwellSweepAt.HasValue ||
                    now - checkpoint.LastDwellSweepAt.Value >= _options.Evaluation.DwellSweepInterval;

                if (shouldSweep)
                {
                    var evaluated = await pipeline
                        .SweepDwellAsync(now, _options.Evaluation.ChangeBatchSize, stoppingToken)
                        .ConfigureAwait(false);
                    LogDwellSweep(_logger, evaluated);
                    checkpoint = checkpoint with { LastDwellSweepAt = now };
                }

                if (nextGeneration != checkpoint.LastGeneration || shouldSweep)
                {
                    checkpoint = checkpoint with { LastGeneration = nextGeneration };
                    await checkpointStore.SetAsync(checkpoint, stoppingToken).ConfigureAwait(false);
                }

                if (nextGeneration == checkpoint.LastGeneration && !shouldSweep)
                {
                    await Task.Delay(_options.Evaluation.IdleDelay, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogLoopFailed(_logger, ex);
                await Task.Delay(_options.Evaluation.IdleDelay, stoppingToken).ConfigureAwait(false);
            }
        }

        await _leaderElection.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
        LogStopped(_logger);
    }

    private async Task<AlertWorkerCheckpoint> GetCheckpointAsync(string workerName, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var checkpointStore = scope.ServiceProvider.GetRequiredService<IAlertCheckpointStore>();
        return await checkpointStore.GetAsync(workerName, cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 9410, Level = LogLevel.Information, Message = "Alert evaluator is disabled.")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(EventId = 9411, Level = LogLevel.Information, Message = "Starting alert evaluator worker {WorkerName} at generation {Generation}.")]
    private static partial void LogStarting(ILogger logger, string workerName, long generation);

    [LoggerMessage(EventId = 9412, Level = LogLevel.Debug, Message = "Completed dwell sweep over {EvaluatedCount} candidate states.")]
    private static partial void LogDwellSweep(ILogger logger, int evaluatedCount);

    [LoggerMessage(EventId = 9413, Level = LogLevel.Warning, Message = "Alert evaluator loop failed.")]
    private static partial void LogLoopFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9414, Level = LogLevel.Information, Message = "Alert evaluator stopped.")]
    private static partial void LogStopped(ILogger logger);
}
