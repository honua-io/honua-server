// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.Infrastructure.Monitoring;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Middleware;

/// <summary>
/// Middleware that collects performance metrics for HTTP requests and tracks system resource usage.
/// </summary>
/// <remarks>
/// This middleware measures request duration, tracks active requests, monitors memory usage,
/// and provides comprehensive performance telemetry for operational monitoring.
/// </remarks>
internal sealed class PerformanceMonitoringMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<PerformanceMonitoringMiddleware> _logger;
    private readonly IPerformanceMonitor _performanceMonitor;
    private readonly PerformanceMonitoringOptions _options;

    public PerformanceMonitoringMiddleware(
        RequestDelegate next,
        ILogger<PerformanceMonitoringMiddleware> logger,
        IPerformanceMonitor performanceMonitor,
        IOptions<PerformanceMonitoringOptions> options)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        var endpoint = GetNormalizedEndpoint(context.Request.Path);
        IOperationScope? operationScope = null;

        if (_options.EnableDetailedRequestTracking)
        {
            operationScope = _performanceMonitor.StartOperation("http_request");
            operationScope
                .WithTag("method", context.Request.Method)
                .WithTag("endpoint", endpoint);
        }

        // Track active request count
        _performanceMonitor.RecordActiveHttpRequestDelta(1);

        // Record memory usage if enabled and at sampling interval
        if (_options.EnableMemoryTracking && ShouldSampleMemory())
        {
            RecordMemoryMetrics();
        }

        try
        {
            // Continue to next middleware
            await _next(context);
        }
        catch (Exception ex)
        {
            operationScope?.WithTag("error", ex.GetType().Name);
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            }

            // Log performance impact of exceptions
            PerformanceMonitoringLog.RequestExceptionOccurred(_logger,
                context.Request.Method,
                context.Request.Path,
                ex.GetType().Name,
                stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
        finally
        {
            stopwatch.Stop();

            // Decrement active request count
            _performanceMonitor.RecordActiveHttpRequestDelta(-1);

            // Record request metrics
            RecordRequestMetrics(context, endpoint, stopwatch.Elapsed);

            if (operationScope is not null)
            {
                operationScope.WithTag("status_code", context.Response.StatusCode.ToString(CultureInfo.InvariantCulture));
                operationScope.Dispose();
            }

            // Log slow requests
            if (stopwatch.Elapsed > _options.SlowRequestThreshold)
            {
                PerformanceMonitoringLog.SlowRequestDetected(_logger,
                    context.Request.Method,
                    context.Request.Path,
                    stopwatch.Elapsed.TotalMilliseconds,
                    _options.SlowRequestThreshold.TotalMilliseconds);
            }
        }
    }

    /// <summary>
    /// Records HTTP request performance metrics.
    /// </summary>
    private void RecordRequestMetrics(HttpContext context, string endpoint, TimeSpan duration)
    {
        var method = context.Request.Method;
        var statusCode = context.Response.StatusCode;

        _performanceMonitor.RecordHttpRequest(method, endpoint, statusCode, duration);

        // Record additional metrics for detailed monitoring
        var tags = new Dictionary<string, string>
        {
            { "method", method },
            { "endpoint", endpoint },
            { "status_code", statusCode.ToString() },
            { "protocol", context.Request.Protocol }
        };

        _performanceMonitor.RecordHistogram("honua_request_duration_detailed_ms", duration.TotalMilliseconds, tags);

        // Track payload sizes if available
        if (context.Request.ContentLength.HasValue)
        {
            _performanceMonitor.RecordHistogram("honua_request_size_bytes", context.Request.ContentLength.Value, tags);
        }
    }

    /// <summary>
    /// Records current memory usage metrics.
    /// </summary>
    private void RecordMemoryMetrics()
    {
        try
        {
            var memoryUsage = MemoryMonitor.GetMemoryUsage();

            _performanceMonitor.RecordMemoryUsage(
                memoryUsage.AllocatedBytes,
                memoryUsage.Gen0Collections,
                memoryUsage.Gen1Collections,
                memoryUsage.Gen2Collections);

            // Record additional memory metrics
            var tags = new Dictionary<string, string>
            {
                { "component", "memory_monitor" }
            };

            _performanceMonitor.RecordHistogram("honua_memory_heap_size_bytes", memoryUsage.HeapSizeBytes, tags);
            _performanceMonitor.RecordHistogram("honua_memory_pressure_percentage", memoryUsage.MemoryPressurePercentage, tags);

            // Log high memory pressure
            if (memoryUsage.IsHighMemoryPressure)
            {
                PerformanceMonitoringLog.HighMemoryPressureDetected(_logger,
                    memoryUsage.MemoryPressurePercentage,
                    memoryUsage.AllocatedBytes / (1024 * 1024)); // Convert to MB
            }
        }
        catch (Exception ex)
        {
            // Don't let memory monitoring failures impact request processing
            PerformanceMonitoringLog.MemoryMonitoringFailed(_logger, ex.Message, ex);
        }
    }

    /// <summary>
    /// Normalizes endpoint paths to reduce cardinality in metrics.
    /// </summary>
    private static string GetNormalizedEndpoint(PathString path)
    {
        var pathValue = path.Value ?? "/";
        if (pathValue == "/")
        {
            return pathValue;
        }

        var segments = pathValue.Split('/', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];

            if (segment.Equals("services", StringComparison.OrdinalIgnoreCase) && i + 1 < segments.Length)
            {
                segments[i + 1] = "{serviceId}";
            }

            if (segment.Equals("collections", StringComparison.OrdinalIgnoreCase) && i + 1 < segments.Length)
            {
                segments[i + 1] = "{collectionId}";
            }

            if (segment.Equals("items", StringComparison.OrdinalIgnoreCase) && i + 1 < segments.Length)
            {
                segments[i + 1] = "{featureId}";
            }

            if (segment.Equals("connections", StringComparison.OrdinalIgnoreCase) && i + 1 < segments.Length)
            {
                segments[i + 1] = "{connectionId}";
            }

            if (segment.Equals("jobs", StringComparison.OrdinalIgnoreCase) && i + 1 < segments.Length)
            {
                segments[i + 1] = "{jobId}";
            }
        }

        pathValue = "/" + string.Join('/', segments);

        // Normalize common patterns to reduce metric cardinality
        // Replace IDs, GUIDs, and OData key segments with placeholders
        pathValue = System.Text.RegularExpressions.Regex.Replace(pathValue, @"\([^/]+\)", "({id})");
        pathValue = System.Text.RegularExpressions.Regex.Replace(pathValue, @"\b\d+\b", "{id}");
        pathValue = System.Text.RegularExpressions.Regex.Replace(pathValue,
            @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
            "{guid}");

        return pathValue;
    }

    /// <summary>
    /// Determines if memory should be sampled based on configuration.
    /// </summary>
    private bool ShouldSampleMemory()
    {
        // Simple sampling: sample every N requests (thread-safe approximation)
        return Environment.TickCount % _options.MemorySamplingInterval == 0;
    }
}

/// <summary>
/// Extension methods for registering performance monitoring middleware.
/// </summary>
internal static class PerformanceMonitoringMiddlewareExtensions
{
    /// <summary>
    /// Adds performance monitoring middleware to the application pipeline.
    /// </summary>
    /// <param name="app">The application builder</param>
    /// <returns>The application builder for chaining</returns>
    internal static IApplicationBuilder UsePerformanceMonitoring(this IApplicationBuilder app)
    {
        return app.UseMiddleware<PerformanceMonitoringMiddleware>();
    }

    /// <summary>
    /// Adds performance monitoring services to the service collection.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Optional configuration action</param>
    /// <returns>The service collection for chaining</returns>
    internal static IServiceCollection AddPerformanceMonitoring(
        this IServiceCollection services,
        Action<PerformanceMonitoringOptions>? configure = null)
    {
        // Register configuration
        var options = new PerformanceMonitoringOptions();
        configure?.Invoke(options);
        services.Configure<PerformanceMonitoringOptions>(config =>
        {
            config.EnableMemoryTracking = options.EnableMemoryTracking;
            config.SlowRequestThreshold = options.SlowRequestThreshold;
            config.MemorySamplingInterval = options.MemorySamplingInterval;
            config.EnableDetailedRequestTracking = options.EnableDetailedRequestTracking;
        });

        // Register performance monitor
        services.AddDefaultPerformanceMonitor();

        return services;
    }
}
