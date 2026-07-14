// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Alerts;

internal sealed partial class AlertEvaluationBackgroundService : BackgroundService, IAlertEvaluationHealth
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILeaderElectionStrategy _leaderElection;
    private readonly AlertOptions _options;
    private readonly AlertPipelineMetrics _metrics;
    private readonly ILogger<AlertEvaluationBackgroundService> _logger;

    private volatile bool _isRunning;
    private long _lastHeartbeatAtTicks;
    private long _lastLeaderPassAtTicks;
    private long _leaderAcquisitionFailingSinceTicks;

    public AlertEvaluationBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILeaderElectionStrategy leaderElection,
        IOptions<AlertOptions> options,
        AlertPipelineMetrics metrics,
        ILogger<AlertEvaluationBackgroundService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _leaderElection = leaderElection ?? throw new ArgumentNullException(nameof(leaderElection));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool IsEvaluatorEnabled => _options.Enabled;

    /// <inheritdoc />
    public bool IsEvaluatorRunning => _isRunning;

    /// <inheritdoc />
    public bool IsLeader => _leaderElection.IsLeader;

    /// <inheritdoc />
    public DateTimeOffset? LastHeartbeatAt => ReadTimestamp(ref _lastHeartbeatAtTicks);

    /// <inheritdoc />
    public DateTimeOffset? LastLeaderPassAt => ReadTimestamp(ref _lastLeaderPassAtTicks);

    /// <inheritdoc />
    public DateTimeOffset? LeaderAcquisitionFailingSince => ReadTimestamp(ref _leaderAcquisitionFailingSinceTicks);

    private static DateTimeOffset? ReadTimestamp(ref long ticks)
    {
        var value = Interlocked.Read(ref ticks);
        return value == 0 ? null : new DateTimeOffset(value, TimeSpan.Zero);
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

        _isRunning = true;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Heartbeat every iteration so the health check can prove the loop is alive
                    // independently of whether this node currently leads.
                    Interlocked.Exchange(ref _lastHeartbeatAtTicks, DateTimeOffset.UtcNow.UtcTicks);

                    var isLeader = await _leaderElection.TryAcquireAsync(stoppingToken).ConfigureAwait(false);
                    UpdateLeaderAcquisitionState();
                    if (!isLeader)
                    {
                        await Task.Delay(_options.Evaluation.IdleDelay, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var pipeline = scope.ServiceProvider.GetRequiredService<IAlertPipeline>();
                    var checkpointStore = scope.ServiceProvider.GetRequiredService<IAlertCheckpointStore>();

                    var previousGeneration = checkpoint.LastGeneration;
                    var nextGeneration = await pipeline
                        .ProcessChangesAsync(previousGeneration, _options.Evaluation.ChangeBatchSize, stoppingToken)
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

                    if (nextGeneration != previousGeneration || shouldSweep)
                    {
                        checkpoint = checkpoint with { LastGeneration = nextGeneration };
                        await checkpointStore.SetAsync(checkpoint, stoppingToken).ConfigureAwait(false);
                    }

                    // Record a productive leader-pass heartbeat: this node holds leadership and
                    // completed a pass, so a stale value here means the leader is wedged.
                    Interlocked.Exchange(ref _lastLeaderPassAtTicks, DateTimeOffset.UtcNow.UtcTicks);

                    // Delay only when the batch made no progress; a productive batch loops
                    // immediately so a change backlog drains at full speed.
                    if (nextGeneration == previousGeneration && !shouldSweep)
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
                    // Intentionally generic: this is a long-running background evaluation
                    // loop. A single failed iteration must not kill the host's background
                    // service; log, back off, and keep polling.
                    LogLoopFailed(_logger, ex);
                    await Task.Delay(_options.Evaluation.IdleDelay, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _isRunning = false;
        }

        await _leaderElection.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
        LogStopped(_logger);
    }

    /// <summary>
    /// Tracks the fleet-wide no-leader signal from the coordinator's last acquisition attempt.
    /// A faulted attempt (the coordinator errored) starts/continues a "failing since" window; any
    /// determinate result (led, or cleanly conceded to another holder) clears it. The evaluation
    /// health check compares the window age to <see cref="AlertEvaluationOptions.NoLeaderThreshold"/>.
    /// The gauge mirrors the boolean failing state for scrape-time visibility.
    /// </summary>
    private void UpdateLeaderAcquisitionState()
    {
        if (_leaderElection.LastAcquireFaulted)
        {
            // Start the window on the first fault; keep the original start on subsequent faults.
            Interlocked.CompareExchange(ref _leaderAcquisitionFailingSinceTicks, DateTimeOffset.UtcNow.UtcTicks, 0);
            _metrics.RecordEvaluationLeaderAcquisitionFailing(true);
        }
        else
        {
            Interlocked.Exchange(ref _leaderAcquisitionFailingSinceTicks, 0);
            _metrics.RecordEvaluationLeaderAcquisitionFailing(false);
        }
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
