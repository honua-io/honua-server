// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;

namespace Honua.Infrastructure.HealthChecks;

/// <summary>
/// Basic health check service implementation.
/// Provides simple health monitoring for essential application components.
/// </summary>
internal sealed class BasicHealthCheckService : IHealthCheckService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<BasicHealthCheckService> _logger;

    public BasicHealthCheckService(
        IServiceProvider serviceProvider,
        ILogger<BasicHealthCheckService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var components = new Dictionary<string, ComponentHealthResult>();

        BasicHealthCheckServiceLog.HealthCheckStarted(_logger);

        try
        {
            // Basic component checks
            components["database"] = await CheckDatabaseAsync(cancellationToken);
            components["cache"] = await CheckCacheAsync(cancellationToken);

            stopwatch.Stop();

            var overallStatus = DetermineOverallStatus(components.Values);
            var healthyCount = components.Values.Count(r => r.Status == HealthStatus.Healthy);

            var result = new HealthCheckResult
            {
                Status = overallStatus,
                Duration = stopwatch.Elapsed,
                Components = components,
                Description = $"{healthyCount}/{components.Count} components healthy"
            };

            BasicHealthCheckServiceLog.HealthCheckCompleted(_logger, overallStatus, stopwatch.ElapsedMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            BasicHealthCheckServiceLog.HealthCheckFailed(_logger, ex);

            return new HealthCheckResult
            {
                Status = HealthStatus.Unhealthy,
                Duration = stopwatch.Elapsed,
                Components = components,
                Description = $"Health check failed: {ex.Message}"
            };
        }
    }

    public async Task<ComponentHealthResult> CheckComponentHealthAsync(string componentName, CancellationToken cancellationToken = default)
    {
        return componentName.ToLowerInvariant() switch
        {
            "database" => await CheckDatabaseAsync(cancellationToken),
            "cache" => await CheckCacheAsync(cancellationToken),
            _ => new ComponentHealthResult
            {
                Status = HealthStatus.Unhealthy,
                Duration = TimeSpan.Zero,
                Description = $"Unknown component: {componentName}"
            }
        };
    }

    public IReadOnlyList<string> GetRegisteredComponents() => new[] { "database", "cache" };

    private static async Task<ComponentHealthResult> CheckDatabaseAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Very basic check - just ensure the application is running
            // In practice this would check database connectivity
            await Task.Delay(1, cancellationToken);
            stopwatch.Stop();

            return new ComponentHealthResult
            {
                Status = HealthStatus.Healthy,
                Duration = stopwatch.Elapsed,
                Description = "Database check placeholder (healthy)",
                Data = new Dictionary<string, object>
                {
                    ["check_type"] = "placeholder",
                    ["response_time_ms"] = stopwatch.ElapsedMilliseconds
                }
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new ComponentHealthResult
            {
                Status = HealthStatus.Unhealthy,
                Duration = stopwatch.Elapsed,
                Description = $"Database check failed: {ex.Message}",
                Exception = ex
            };
        }
    }

    private static async Task<ComponentHealthResult> CheckCacheAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Basic cache service availability check
            await Task.Delay(1, cancellationToken);
            stopwatch.Stop();

            return new ComponentHealthResult
            {
                Status = HealthStatus.Healthy,
                Duration = stopwatch.Elapsed,
                Description = "Cache check placeholder (healthy)",
                Data = new Dictionary<string, object>
                {
                    ["check_type"] = "placeholder",
                    ["response_time_ms"] = stopwatch.ElapsedMilliseconds
                }
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new ComponentHealthResult
            {
                Status = HealthStatus.Degraded,
                Duration = stopwatch.Elapsed,
                Description = $"Cache check failed: {ex.Message}",
                Exception = ex
            };
        }
    }

    private static HealthStatus DetermineOverallStatus(IEnumerable<ComponentHealthResult> componentResults)
    {
        var statusList = componentResults.Select(r => r.Status).ToList();

        if (statusList.Any(s => s == HealthStatus.Unhealthy))
            return HealthStatus.Unhealthy;

        if (statusList.Any(s => s == HealthStatus.Degraded))
            return HealthStatus.Degraded;

        return HealthStatus.Healthy;
    }
}
