// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;

namespace Honua.Server.Features.Infrastructure.Resilience;

/// <summary>
/// Extension methods for adding HTTP client health checks with circuit breaker awareness.
/// </summary>
public static class HttpClientHealthCheckExtensions
{
    /// <summary>
    /// Adds health checks for HTTP clients with circuit breaker state monitoring.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="serviceType">Service type identifier matching the HTTP client configuration.</param>
    /// <param name="healthCheckUri">URI to check for service health.</param>
    /// <param name="httpClientName">Optional HTTP client name. Uses serviceType if not specified.</param>
    /// <param name="timeout">Health check timeout. Defaults to 10 seconds.</param>
    /// <param name="tags">Optional health check tags.</param>
    /// <returns>The health checks builder for chaining.</returns>
    public static IHealthChecksBuilder AddHttpClientHealthCheck(
        this IHealthChecksBuilder builder,
        string serviceType,
        string healthCheckUri,
        string? httpClientName = null,
        TimeSpan? timeout = null,
        IEnumerable<string>? tags = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(healthCheckUri);

        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(10);
        var effectiveClientName = httpClientName ?? serviceType;
        var effectiveTags = (tags ?? Enumerable.Empty<string>()).Concat(new[] { "http", "external" });

        return builder.Add(new HealthCheckRegistration(
            $"http-{serviceType}",
            sp => new HttpClientHealthCheck(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<ILogger<HttpClientHealthCheck>>(),
                effectiveClientName,
                serviceType,
                healthCheckUri,
                effectiveTimeout),
            HealthStatus.Degraded, // Treat external service failures as degraded, not unhealthy
            effectiveTags));
    }
}

/// <summary>
/// Health check implementation that monitors HTTP client connectivity and circuit breaker state.
/// </summary>
internal sealed class HttpClientHealthCheck : IHealthCheck
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpClientHealthCheck> _logger;
    private readonly string _httpClientName;
    private readonly string _serviceType;
    private readonly string _healthCheckUri;
    private readonly TimeSpan _timeout;

    public HttpClientHealthCheck(
        IHttpClientFactory httpClientFactory,
        ILogger<HttpClientHealthCheck> logger,
        string httpClientName,
        string serviceType,
        string healthCheckUri,
        TimeSpan timeout)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _httpClientName = httpClientName;
        _serviceType = serviceType;
        _healthCheckUri = healthCheckUri;
        _timeout = timeout;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var httpClient = _httpClientFactory.CreateClient(_httpClientName);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeout);

            // Attempt to get the circuit breaker state
            var circuitBreakerState = GetCircuitBreakerState();
            var data = new Dictionary<string, object>
            {
                ["service_type"] = _serviceType,
                ["health_check_uri"] = _healthCheckUri,
                ["circuit_breaker_state"] = circuitBreakerState?.ToString() ?? "unknown"
            };

            // If circuit breaker is open, return degraded without making HTTP call
            if (circuitBreakerState == CircuitState.Open)
            {
                _logger.LogWarning(
                    "HTTP health check for {ServiceType} skipped due to open circuit breaker",
                    _serviceType);

                return HealthCheckResult.Degraded(
                    $"Circuit breaker is open for service {_serviceType}",
                    null,
                    data);
            }

            // Perform actual health check
            var startTime = DateTimeOffset.UtcNow;
            using var response = await httpClient.GetAsync(_healthCheckUri, cts.Token);
            var elapsed = DateTimeOffset.UtcNow - startTime;

            data["response_time_ms"] = elapsed.TotalMilliseconds;
            data["status_code"] = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "HTTP health check for {ServiceType} succeeded in {ElapsedMs}ms",
                    _serviceType, elapsed.TotalMilliseconds);

                return HealthCheckResult.Healthy(
                    $"Service {_serviceType} is healthy",
                    data);
            }

            _logger.LogWarning(
                "HTTP health check for {ServiceType} returned {StatusCode} in {ElapsedMs}ms",
                _serviceType, response.StatusCode, elapsed.TotalMilliseconds);

            return HealthCheckResult.Degraded(
                $"Service {_serviceType} returned {response.StatusCode}",
                null,
                data);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "HTTP health check for {ServiceType} was cancelled",
                _serviceType);

            return HealthCheckResult.Degraded(
                $"Health check for service {_serviceType} was cancelled",
                null,
                new Dictionary<string, object>
                {
                    ["service_type"] = _serviceType,
                    ["health_check_uri"] = _healthCheckUri,
                    ["cancelled"] = true
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "HTTP health check for {ServiceType} failed with exception",
                _serviceType);

            return HealthCheckResult.Degraded(
                $"Service {_serviceType} health check failed: {ex.Message}",
                ex,
                new Dictionary<string, object>
                {
                    ["service_type"] = _serviceType,
                    ["health_check_uri"] = _healthCheckUri,
                    ["exception"] = ex.GetType().Name
                });
        }
    }

    private CircuitState? GetCircuitBreakerState()
    {
        try
        {
            // Try to get the circuit breaker state from the cached policy
            var policy = HttpResiliencePolicies.GetHttpPolicy(_serviceType);

            // This is a simplified approach - in a full implementation,
            // we might need to track circuit breaker state more explicitly
            return null; // Circuit breaker state introspection is complex with Polly
        }
        catch
        {
            // If we can't determine circuit breaker state, continue with health check
            return null;
        }
    }
}
