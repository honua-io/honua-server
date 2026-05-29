// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Infrastructure.Crs;

internal sealed partial class PostgresCrsWarmupService : BackgroundService
{
    private static readonly string[] _warmupIdentifiers =
    [
        "http://www.opengis.net/def/crs/OGC/1.3/CRS84"
    ];

    private static readonly int[] _warmupSrids =
    [
        4326,
        3857
    ];

    private static readonly TimeSpan LeaderElectionRetryInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan WarmupInterval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDistributedLeaderElection? _leaderElection;
    private readonly ILogger<PostgresCrsWarmupService> _logger;

    public PostgresCrsWarmupService(
        IServiceScopeFactory scopeFactory,
        ILogger<PostgresCrsWarmupService> logger,
        IDistributedLeaderElection? leaderElection = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _leaderElection = leaderElection;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_leaderElection == null)
        {
            Log.NoLeaderElection(_logger);
            await PerformWarmupAsync(stoppingToken);
            return;
        }

        Log.WarmupServiceStarted(_logger);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Try to acquire leadership
                var isLeader = await _leaderElection.TryAcquireLeadershipAsync(stoppingToken);

                if (isLeader)
                {
                    Log.LeadershipAcquiredForWarmup(_logger);

                    // Perform initial warmup
                    await PerformWarmupAsync(stoppingToken);

                    // Continue warming up periodically while we're the leader
                    while (_leaderElection.IsLeader && !stoppingToken.IsCancellationRequested)
                    {
                        try
                        {
                            await Task.Delay(WarmupInterval, stoppingToken);
                            if (_leaderElection.IsLeader)
                            {
                                await PerformWarmupAsync(stoppingToken);
                            }
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                            break;
                        }
                    }

                    Log.LeadershipLostForWarmup(_logger);
                }
                else
                {
                    Log.FollowerWaiting(_logger);
                    await Task.Delay(LeaderElectionRetryInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.WarmupLoopError(_logger, ex);
                await Task.Delay(LeaderElectionRetryInterval, stoppingToken);
            }
        }

        // Release leadership on shutdown
        if (_leaderElection.IsLeader)
        {
            try
            {
                await _leaderElection.ReleaseLeadershipAsync(stoppingToken);
                Log.LeadershipReleasedOnShutdown(_logger);
            }
            catch (Exception ex)
            {
                Log.LeadershipReleaseError(_logger, ex);
            }
        }
    }

    private async Task PerformWarmupAsync(CancellationToken stoppingToken)
    {
        try
        {
            Log.WarmupStarted(_logger);

            using var scope = _scopeFactory.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<ICrsRegistry>();

            foreach (var identifier in _warmupIdentifiers)
            {
                await registry.ResolveAsync(identifier, stoppingToken).ConfigureAwait(false);
            }

            foreach (var srid in _warmupSrids)
            {
                await registry.ResolveBySridAsync(srid, stoppingToken).ConfigureAwait(false);
            }

            Log.WarmupCompleted(_logger);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.WarmupFailed(_logger, ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(7201, LogLevel.Warning, "CRS warmup failed")]
        public static partial void WarmupFailed(ILogger logger, Exception exception);

        [LoggerMessage(7202, LogLevel.Information, "CRS warmup service started")]
        public static partial void WarmupServiceStarted(ILogger logger);

        [LoggerMessage(7203, LogLevel.Information, "No leader election configured, performing warmup as single instance")]
        public static partial void NoLeaderElection(ILogger logger);

        [LoggerMessage(7204, LogLevel.Information, "Leadership acquired, beginning CRS warmup")]
        public static partial void LeadershipAcquiredForWarmup(ILogger logger);

        [LoggerMessage(7205, LogLevel.Information, "Leadership lost, stopping CRS warmup")]
        public static partial void LeadershipLostForWarmup(ILogger logger);

        [LoggerMessage(7206, LogLevel.Debug, "Follower instance waiting for leadership")]
        public static partial void FollowerWaiting(ILogger logger);

        [LoggerMessage(7207, LogLevel.Information, "CRS warmup started")]
        public static partial void WarmupStarted(ILogger logger);

        [LoggerMessage(7208, LogLevel.Information, "CRS warmup completed")]
        public static partial void WarmupCompleted(ILogger logger);

        [LoggerMessage(7209, LogLevel.Warning, "CRS warmup loop error")]
        public static partial void WarmupLoopError(ILogger logger, Exception exception);

        [LoggerMessage(7210, LogLevel.Information, "Leadership released on service shutdown")]
        public static partial void LeadershipReleasedOnShutdown(ILogger logger);

        [LoggerMessage(7211, LogLevel.Warning, "Failed to release leadership on shutdown")]
        public static partial void LeadershipReleaseError(ILogger logger, Exception exception);
    }
}
