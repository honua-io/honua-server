// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.HealthChecks;

/// <summary>
/// Service interface for health checking operations.
/// Provides comprehensive health status reporting for application components.
/// </summary>
public interface IHealthCheckService
{
    /// <summary>
    /// Performs a complete health check of all registered components.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Comprehensive health check result</returns>
    Task<HealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a health check of a specific component.
    /// </summary>
    /// <param name="componentName">Name of the component to check</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Health check result for the specified component</returns>
    Task<ComponentHealthResult> CheckComponentHealthAsync(string componentName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the names of all registered health check components.
    /// </summary>
    /// <returns>List of component names</returns>
    IReadOnlyList<string> GetRegisteredComponents();
}

/// <summary>
/// Overall health check result for the application.
/// </summary>
public sealed record HealthCheckResult
{
    /// <summary>
    /// Overall health status of the application.
    /// </summary>
    public HealthStatus Status { get; init; }

    /// <summary>
    /// Total time taken to perform all health checks.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Individual component health results.
    /// </summary>
    public IReadOnlyDictionary<string, ComponentHealthResult> Components { get; init; } =
        new Dictionary<string, ComponentHealthResult>();

    /// <summary>
    /// Overall health summary message.
    /// </summary>
    public string? Description { get; init; }
}

/// <summary>
/// Health check result for an individual component.
/// </summary>
public sealed record ComponentHealthResult
{
    /// <summary>
    /// Health status of the component.
    /// </summary>
    public HealthStatus Status { get; init; }

    /// <summary>
    /// Time taken to check this component.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Descriptive message about the component's health.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Additional data about the component's health.
    /// </summary>
    public IReadOnlyDictionary<string, object>? Data { get; init; }

    /// <summary>
    /// Exception that occurred during health check, if any.
    /// </summary>
    public Exception? Exception { get; init; }
}

/// <summary>
/// Enumeration of possible health statuses.
/// </summary>
public enum HealthStatus
{
    /// <summary>
    /// Component is healthy and functioning normally.
    /// </summary>
    Healthy,

    /// <summary>
    /// Component has some issues but is still functioning.
    /// </summary>
    Degraded,

    /// <summary>
    /// Component is unhealthy and not functioning properly.
    /// </summary>
    Unhealthy
}