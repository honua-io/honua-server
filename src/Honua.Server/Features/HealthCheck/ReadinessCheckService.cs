// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Infrastructure.Monitoring;
using Honua.Server.Features.Infrastructure.Logging;
using System.Diagnostics;

namespace Honua.Server.Features.HealthCheck;

/// <summary>
/// Service for orchestrating readiness health checks
/// </summary>
internal sealed class ReadinessCheckService : IReadinessCheckService
{
    private readonly IDatabaseHealthChecker _databaseHealthChecker;
    private readonly ICacheHealthChecker? _cacheHealthChecker;
    private readonly IFeatureChangeEventStoreHealth? _featureChangeEventStoreHealth;
    private readonly MigrationState _migrationState;
    private readonly ILogger<ReadinessCheckService> _logger;

    public ReadinessCheckService(
        IDatabaseHealthChecker databaseHealthChecker,
        MigrationState migrationState,
        ILogger<ReadinessCheckService> logger,
        ICacheHealthChecker? cacheHealthChecker = null,
        IFeatureChangeEventStoreHealth? featureChangeEventStoreHealth = null)
    {
        _databaseHealthChecker = databaseHealthChecker;
        _cacheHealthChecker = cacheHealthChecker;
        _featureChangeEventStoreHealth = featureChangeEventStoreHealth;
        _migrationState = migrationState ?? throw new ArgumentNullException(nameof(migrationState));
        _logger = logger;
    }

    /// <summary>
    /// Performs all readiness checks and returns the overall result
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Readiness check result</returns>
    public async Task<ReadinessResult> CheckReadinessAsync(CancellationToken cancellationToken = default)
    {
        var currentCheckName = "Database";
        try
        {
            if (_migrationState.IsFailed)
            {
                return ReadinessResult.NotReady("Database migrations failed");
            }

            if (_migrationState.IsRunning)
            {
                return ReadinessResult.NotReady("Database migrations in progress");
            }

            if (!_migrationState.IsReady)
            {
                return ReadinessResult.NotReady("Database migrations not completed");
            }

            // Check database health
            var databaseStopwatch = Stopwatch.StartNew();
            bool isDatabaseHealthy = await _databaseHealthChecker.IsDatabaseHealthyAsync(cancellationToken);
            databaseStopwatch.Stop();

            if (!isDatabaseHealthy)
            {
                // Log unhealthy database without exception
                Log.HealthCheckExecuted(_logger, "DatabaseHealth", "Unhealthy", databaseStopwatch.Elapsed.TotalMilliseconds);
                return ReadinessResult.NotReady("Database unavailable");
            }

            Log.HealthCheckExecuted(_logger, "DatabaseHealth", "Healthy", databaseStopwatch.Elapsed.TotalMilliseconds);

            // Check cache health (optional - cache unavailability doesn't make system not ready)
            if (_cacheHealthChecker != null)
            {
                currentCheckName = "Cache";
                var cacheStopwatch = Stopwatch.StartNew();
                bool isCacheHealthy = await _cacheHealthChecker.IsCacheHealthyAsync(cancellationToken);
                cacheStopwatch.Stop();
                string cacheStatus = isCacheHealthy
                    ? (_cacheHealthChecker.IsUsingFallback ? "Healthy (fallback)" : "Healthy")
                    : "Unhealthy";
                Log.HealthCheckExecuted(_logger, "CacheHealth", cacheStatus, cacheStopwatch.Elapsed.TotalMilliseconds);

                if (!isCacheHealthy)
                {
                    return ReadinessResult.NotReady("Cache unavailable");
                }
            }

            if (_featureChangeEventStoreHealth is not null)
            {
                currentCheckName = "Feature-change event storage";
                var featureChangeStoreStopwatch = Stopwatch.StartNew();
                var canPersistEvents = _featureChangeEventStoreHealth.CanPersistEvents;
                featureChangeStoreStopwatch.Stop();

                if (!canPersistEvents)
                {
                    Log.HealthCheckExecuted(
                        _logger,
                        "FeatureChangeEventStore",
                        "Unhealthy",
                        featureChangeStoreStopwatch.Elapsed.TotalMilliseconds);
                    return ReadinessResult.NotReady("Feature-change event storage unavailable");
                }
            }

            return ReadinessResult.Ready();
        }
        catch (OperationCanceledException)
        {
            // Re-throw cancellation exceptions to properly propagate cancellation
            throw;
        }
        catch (Exception ex)
        {
            // Log the error for debugging but don't expose details in response
            var failureMessage = $"{currentCheckName} health check failed";
            Log.DatabaseConnectionFailed(_logger, $"{failureMessage}: {ex.Message}", ex);
            return ReadinessResult.NotReady(failureMessage, ex);
        }
    }
}
