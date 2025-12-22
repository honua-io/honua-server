// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.Server.Features.Infrastructure.Logging;

namespace Honua.Server.Features.HealthCheck;

/// <summary>
/// Service for orchestrating readiness health checks
/// </summary>
public sealed class ReadinessCheckService(
    IDatabaseHealthChecker databaseHealthChecker,
    ILogger<ReadinessCheckService> logger) : IReadinessCheckService
{
    private readonly IDatabaseHealthChecker _databaseHealthChecker = databaseHealthChecker;
    private readonly ILogger<ReadinessCheckService> _logger = logger;

    /// <summary>
    /// Performs all readiness checks and returns the overall result
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Readiness check result</returns>
    public async Task<ReadinessResult> CheckReadinessAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Check database health
            bool isDatabaseHealthy = await _databaseHealthChecker.IsDatabaseHealthyAsync(cancellationToken);

            if (isDatabaseHealthy)
            {
                // Log successful health check execution
                Log.HealthCheckExecuted(_logger, "DatabaseHealth", "Healthy", 0.0);
                return ReadinessResult.Ready();
            }
            else
            {
                // Log unhealthy database without exception
                Log.HealthCheckExecuted(_logger, "DatabaseHealth", "Unhealthy", 0.0);
                return ReadinessResult.NotReady("Database unavailable");
            }
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
