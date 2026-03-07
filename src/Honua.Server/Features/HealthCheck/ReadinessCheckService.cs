// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.Server.Features.Infrastructure.Monitoring;
using Honua.Server.Features.Infrastructure.Logging;

namespace Honua.Server.Features.HealthCheck;

/// <summary>
/// Service for orchestrating readiness health checks
/// </summary>
internal sealed class ReadinessCheckService : IReadinessCheckService
{
    private readonly IDatabaseHealthChecker _databaseHealthChecker;
    private readonly ICacheHealthChecker? _cacheHealthChecker;
    private readonly MigrationState _migrationState;
    private readonly ILogger<ReadinessCheckService> _logger;

    public ReadinessCheckService(
        IDatabaseHealthChecker databaseHealthChecker,
        MigrationState migrationState,
        ILogger<ReadinessCheckService> logger,
        ICacheHealthChecker? cacheHealthChecker = null)
    {
        _databaseHealthChecker = databaseHealthChecker;
        _cacheHealthChecker = cacheHealthChecker;
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
            bool isDatabaseHealthy = await _databaseHealthChecker.IsDatabaseHealthyAsync(cancellationToken);

            if (!isDatabaseHealthy)
            {
                // Log unhealthy database without exception
                Log.HealthCheckExecuted(_logger, "DatabaseHealth", "Unhealthy", 0.0);
                return ReadinessResult.NotReady("Database unavailable");
            }

            Log.HealthCheckExecuted(_logger, "DatabaseHealth", "Healthy", 0.0);

            // Check cache health (optional - cache unavailability doesn't make system not ready)
            if (_cacheHealthChecker != null)
            {
                bool isCacheHealthy = await _cacheHealthChecker.IsCacheHealthyAsync(cancellationToken);
                string cacheStatus = isCacheHealthy
                    ? (_cacheHealthChecker.IsUsingFallback ? "Healthy (fallback)" : "Healthy")
                    : "Unhealthy";
                Log.HealthCheckExecuted(_logger, "CacheHealth", cacheStatus, 0.0);
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
            Log.DatabaseConnectionFailed(_logger, ex.Message, ex);
            return ReadinessResult.NotReady("Database unavailable", ex);
        }
    }
}
