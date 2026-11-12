// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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
    private readonly TimeProvider _time;
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
        ILogger<AlertEvaluationBackgroundService> logger,
        TimeProvider? time = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _leaderElection = leaderElection ?? throw new ArgumentNullException(nameof(leaderElection));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
    }

    public bool IsEvaluatorEnabled => _options.Enabled;

    public bool IsEvaluatorRunning => _isRunning;

    public bool IsLeader => _leaderElection.IsLeader;

    public DateTimeOffset? LastHeartbeatAt => ReadTimestamp(ref _lastHeartbeatAtTicks);

    public DateTimeOffset? LastLeaderPassAt => ReadTimestamp(ref _lastLeaderPassAtTicks);

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
        AlertWorkerCheckpoint? checkpoint = null;
        var leading = false;

        // The checkpoint read stays inside the loop. A database blip at startup must
        // retry here instead of escaping ExecuteAsync and stopping the host (#6190).
        _isRunning = true;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Heartbeat every iteration so the health check can prove the loop is alive
                    // independently of whether this node currently leads.
                    Interlocked.Exchange(ref _lastHeartbeatAtTicks, _time.GetUtcNow().UtcTicks);

                    var isLeader = await _leaderElection.TryAcquireAsync(stoppingToken).ConfigureAwait(false);
                    UpdateLeaderAcquisitionState();
                    if (!isLeader)
                    {
                        leading = false;
                        await Task.Delay(_options.Evaluation.IdleDelay, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    AlertWorkerCheckpoint activeCheckpoint;
                    if (leading && checkpoint is not null)
                    {
                        activeCheckpoint = checkpoint;
                    }
                    else
                    {
                        activeCheckpoint = await GetCheckpointAsync(workerName, stoppingToken).ConfigureAwait(false);
                        leading = true;
                        LogStarting(_logger, workerName, activeCheckpoint.LastGeneration);
                    }

                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var pipeline = scope.ServiceProvider.GetRequiredService<IAlertPipeline>();
                    var checkpointStore = scope.ServiceProvider.GetRequiredService<IAlertCheckpointStore>();

                    // The lease outlives one renewal interval only, so a long pass keeps renewing it
                    // and stops as soon as it cannot, before a successor can take over and evaluate
                    // the same generations.
                    using var lostLeadership = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    using var passFinished = new CancellationTokenSource();
                    var renewal = KeepLeadershipAsync(lostLeadership, passFinished.Token);
                    var passToken = lostLeadership.Token;

                    var previousGeneration = activeCheckpoint.LastGeneration;
                    var now = _time.GetUtcNow();
                    long nextGeneration;
                    bool shouldSweep;
                    try
                    {
                        nextGeneration = await pipeline
                            .ProcessChangesAsync(previousGeneration, _options.Evaluation.ChangeBatchSize, now, passToken)
                            .ConfigureAwait(false);

                        shouldSweep = !activeCheckpoint.LastDwellSweepAt.HasValue ||
                            now - activeCheckpoint.LastDwellSweepAt.Value >= _options.Evaluation.DwellSweepInterval;

                        if (shouldSweep)
                        {
                            var evaluated = await pipeline
                                .SweepDwellAsync(now, _options.Evaluation.ChangeBatchSize, passToken)
                                .ConfigureAwait(false);
                            LogDwellSweep(_logger, evaluated);
                            activeCheckpoint = activeCheckpoint with { LastDwellSweepAt = now };
                        }

                        if (nextGeneration != previousGeneration || shouldSweep)
                        {
                            activeCheckpoint = activeCheckpoint with { LastGeneration = nextGeneration };
                            await checkpointStore.SetAsync(activeCheckpoint, passToken).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested && passToken.IsCancellationRequested)
                    {
                        LogLeadershipLostDuringPass(_logger);
                        leading = false;
                        checkpoint = null;
                        continue;
                    }
                    finally
                    {
                        await passFinished.CancelAsync().ConfigureAwait(false);
                        await renewal.ConfigureAwait(false);
                    }

                    checkpoint = activeCheckpoint;

                    // Record a productive leader-pass heartbeat: this node holds leadership and
                    // completed a pass, so a stale value here means the leader is wedged.
                    Interlocked.Exchange(ref _lastLeaderPassAtTicks, _time.GetUtcNow().UtcTicks);

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
                catch (Exception ex) when (ex is not OutOfMemoryException)
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

    private async Task KeepLeadershipAsync(CancellationTokenSource lostLeadership, CancellationToken passFinished)
    {
        var interval = _options.Evaluation.LeaderLeaseDuration / 3;
        try
        {
            while (true)
            {
                await Task.Delay(interval, _time, passFinished).ConfigureAwait(false);
                if (!await _leaderElection.TryAcquireAsync(passFinished).ConfigureAwait(false))
                {
                    await lostLeadership.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (passFinished.IsCancellationRequested)
        {
        }
    }

    private void UpdateLeaderAcquisitionState()
    {
        if (_leaderElection.LastAcquireFaulted)
        {
            // Start the window on the first fault; keep the original start on subsequent faults.
            Interlocked.CompareExchange(ref _leaderAcquisitionFailingSinceTicks, _time.GetUtcNow().UtcTicks, 0);
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

    [LoggerMessage(EventId = 9415, Level = LogLevel.Warning, Message = "Alert evaluator lost its leader lease during a pass and stopped the pass.")]
    private static partial void LogLeadershipLostDuringPass(ILogger logger);
}
