// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;
using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Server.Features.Infrastructure.Monitoring;
using Honua.Server.Features.Infrastructure.RateLimiting;
using Honua.Server.Features.Import;
using Honua.Server.Features.HealthCheck;

namespace Honua.Server.Features.Infrastructure.Resilience;

/// <summary>
/// Service collection extensions for comprehensive production resilience and monitoring.
/// </summary>
internal static class ProductionResilienceServiceCollectionExtensions
{
    /// <summary>
    /// Adds comprehensive production resilience, monitoring, and observability features.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddProductionResilience(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Configure circuit breaker options
        services.Configure<ExternalServiceCircuitBreakerOptions>(
            configuration.GetSection(ExternalServiceCircuitBreakerOptions.SectionName));

        // Configure rate limiting options
        services.Configure<RateLimitingOptions>(
            configuration.GetSection(RateLimitingOptions.SectionName));

        // Configure file upload options
        services.Configure<FileUploadOptions>(
            configuration.GetSection(FileUploadOptions.SectionName));

        // Add resilience patterns
        services.AddResiliencePatterns(configuration);

        // Add database resilience patterns
        services.AddDatabaseResilience(configuration);

        // Add production monitoring and metrics
        services.AddProductionMonitoring();

        // Add comprehensive health checks for production dependencies
        services.AddProductionHealthChecks(configuration);

        // Add file upload service with streaming and backpressure
        services.AddScoped<StreamingFileUploadService>();

        return services;
    }

    /// <summary>
    /// Adds production monitoring and metrics collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddProductionMonitoring(this IServiceCollection services)
    {
        // Register connection pool metrics
        services.AddSingleton<ConnectionPoolMetrics>();

        // Register production metrics collector
        services.AddSingleton<ProductionMetricsCollector>();

        // Enhance existing active connection tracker
        services.Decorate<IActiveDbConnectionTracker, MonitoredActiveDbConnectionTracker>();

        return services;
    }

    /// <summary>
    /// Configures HTTP client with comprehensive resilience patterns.
    /// </summary>
    /// <param name="httpClientBuilder">The HTTP client builder.</param>
    /// <param name="serviceName">Service name for circuit breaker configuration.</param>
    /// <returns>The HTTP client builder for chaining.</returns>
    public static IHttpClientBuilder AddComprehensiveResilience(
        this IHttpClientBuilder httpClientBuilder,
        string serviceName)
    {
        return httpClientBuilder
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false
            })
            .AddResiliencePolicy(new CircuitBreakerOptions(), serviceName);
    }

    /// <summary>
    /// Adds rate limiting middleware to the application pipeline.
    /// </summary>
    /// <param name="app">The web application.</param>
    /// <returns>The web application for chaining.</returns>
    public static WebApplication UseProductionRateLimiting(this WebApplication app)
    {
        app.UseMiddleware<RateLimitingMiddleware>();
        return app;
    }

    /// <summary>
    /// Maps production monitoring endpoints.
    /// </summary>
    /// <param name="app">The web application.</param>
    /// <returns>The web application for chaining.</returns>
    public static WebApplication MapProductionMonitoring(this WebApplication app)
    {
        app.MapProductionMonitoringEndpoints();
        return app;
    }
}

/// <summary>
/// Decorator for active connection tracker that integrates with metrics collection.
/// </summary>
internal sealed class MonitoredActiveDbConnectionTracker : IActiveDbConnectionTracker
{
    private readonly IActiveDbConnectionTracker _innerTracker;
    private readonly ConnectionPoolMetrics _connectionPoolMetrics;
    private readonly ProductionMetricsCollector _metricsCollector;

    /// <summary>
    /// Initializes a new instance of the <see cref="MonitoredActiveDbConnectionTracker"/> class.
    /// </summary>
    /// <param name="innerTracker">The inner connection tracker.</param>
    /// <param name="connectionPoolMetrics">Connection pool metrics.</param>
    /// <param name="metricsCollector">Production metrics collector.</param>
    public MonitoredActiveDbConnectionTracker(
        IActiveDbConnectionTracker innerTracker,
        ConnectionPoolMetrics connectionPoolMetrics,
        ProductionMetricsCollector metricsCollector)
    {
        _innerTracker = innerTracker;
        _connectionPoolMetrics = connectionPoolMetrics;
        _metricsCollector = metricsCollector;
    }

    /// <inheritdoc/>
    public void Increment()
    {
        var startTime = DateTimeOffset.UtcNow;
        _innerTracker.Increment();
        _connectionPoolMetrics.Increment();

        var acquisitionTime = DateTimeOffset.UtcNow - startTime;
        _connectionPoolMetrics.RecordConnectionAcquisitionLatency(acquisitionTime);
    }

    /// <inheritdoc/>
    public void Decrement()
    {
        _innerTracker.Decrement();
        _connectionPoolMetrics.Decrement();
    }

    /// <inheritdoc/>
    public int GetActiveCount()
    {
        return _innerTracker.GetActiveCount();
    }
}

/// <summary>
/// Enhanced HTTP client handler with connection tracking.
/// </summary>
internal sealed class MonitoredHttpClientHandler : HttpClientHandler
{
    private readonly ProductionMetricsCollector _metricsCollector;
    private readonly string _serviceName;

    /// <summary>
    /// Initializes a new instance of the <see cref="MonitoredHttpClientHandler"/> class.
    /// </summary>
    /// <param name="metricsCollector">Production metrics collector.</param>
    /// <param name="serviceName">Service name for metrics.</param>
    public MonitoredHttpClientHandler(
        ProductionMetricsCollector metricsCollector,
        string serviceName)
    {
        _metricsCollector = metricsCollector;
        _serviceName = serviceName;
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        Exception? exception = null;

        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            var duration = DateTimeOffset.UtcNow - startTime;

            // Record metrics
            _metricsCollector.RecordQuery(
                duration,
                "HTTP",
                _serviceName,
                response.IsSuccessStatusCode);

            if (!response.IsSuccessStatusCode)
            {
                _metricsCollector.RecordError(
                    "HttpError",
                    _serviceName,
                    response.StatusCode.ToString());
            }

            return response;
        }
        catch (Exception ex)
        {
            exception = ex;
            var duration = DateTimeOffset.UtcNow - startTime;

            _metricsCollector.RecordQuery(duration, "HTTP", _serviceName, false);
            _metricsCollector.RecordError("HttpException", _serviceName, ex.GetType().Name);

            throw;
        }
    }
}