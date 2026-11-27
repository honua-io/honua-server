// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Internal;
using Honua.Core.Features.Infrastructure.Monitoring;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Middleware;

internal sealed class PerformanceMonitoringMiddleware
{
    private const string UnmatchedEndpointMetricTag = "/{unmatched}";
    private static readonly string[] KnownFrameworkEndpoints =
    [
        "/healthz/live",
        "/healthz/ready",
        "/healthz/deep",
        "/healthz/metrics",
        "/metrics"
    ];

    private readonly RequestDelegate _next;
    private readonly ILogger<PerformanceMonitoringMiddleware> _logger;
    private readonly IPerformanceMonitor _performanceMonitor;
    private readonly ISystemMetricsCollector _systemMetricsCollector;
    private readonly PerformanceMonitoringOptions _options;

    public PerformanceMonitoringMiddleware(
        RequestDelegate next,
        ILogger<PerformanceMonitoringMiddleware> logger,
        IPerformanceMonitor performanceMonitor,
        ISystemMetricsCollector systemMetricsCollector,
        IOptions<PerformanceMonitoringOptions> options)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
        _systemMetricsCollector = systemMetricsCollector ?? throw new ArgumentNullException(nameof(systemMetricsCollector));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var endpoint = GetNormalizedEndpoint(context);
        // Not a `using` declaration: `operationScope` starts null and is conditionally
        // reassigned below (only when EnableDetailedRequestTracking is on); C# does not
        // allow reassigning a `using`-declared local, so it is disposed explicitly in the
        // `finally` block instead.
        IOperationScope? operationScope = null;
        var requestErrored = false;

        using var systemMetricsScope = _systemMetricsCollector.TrackRequest();

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
        // Intentional: this is a top-level middleware boundary observing every request; it
        // must record telemetry/tag the failure for any exception type before rethrowing
        // unchanged so the shared error-handling middleware further up the pipeline still
        // produces the actual response.
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
            {
                operationScope?.WithTag("error", "client_abort");
                throw;
            }

            requestErrored = true;
            operationScope?.WithTag("error", ex.GetType().Name);
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            }

            // Log performance impact of exceptions
            var requestPath = context.Request.Path.Value ?? string.Empty;
            var exceptionType = ex.GetType().Name;
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            PerformanceMonitoringLog.RequestExceptionOccurred(_logger,
                context.Request.Method,
                requestPath,
                exceptionType,
                elapsedMs);
            throw;
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp);

            // Decrement active request count
            _performanceMonitor.RecordActiveHttpRequestDelta(-1);

            // Record request metrics
            RecordRequestMetrics(context, endpoint, elapsed);
            var isError = requestErrored ||
                          context.Response.StatusCode >= StatusCodes.Status500InternalServerError ||
                          context.Response.StatusCode == StatusCodes.Status408RequestTimeout;
            _systemMetricsCollector.RecordRequest(elapsed, isError);

            operationScope?.WithTag("status_code", context.Response.StatusCode.ToString(CultureInfo.InvariantCulture));

            // Log slow requests
            if (elapsed > _options.SlowRequestThreshold)
            {
                var requestPath = context.Request.Path.Value ?? string.Empty;
                PerformanceMonitoringLog.SlowRequestDetected(_logger,
                    context.Request.Method,
                    requestPath,
                    elapsed.TotalMilliseconds,
                    _options.SlowRequestThreshold.TotalMilliseconds);
            }

            DeferredDisposal.Dispose(operationScope);
        }
    }

    private void RecordRequestMetrics(HttpContext context, string endpoint, TimeSpan duration)
    {
        var method = context.Request.Method;
        var statusCode = context.Response.StatusCode;

        _performanceMonitor.RecordHttpRequest(method, endpoint, statusCode, duration);

        // Record additional metrics for detailed monitoring
        var tags = new TagList
        {
            { "method", method },
            { "endpoint", endpoint },
            { "status_code", statusCode.ToString(CultureInfo.InvariantCulture) },
            { "protocol", context.Request.Protocol }
        };

        _performanceMonitor.RecordHistogram("honua_request_duration_detailed_ms", duration.TotalMilliseconds, tags);

        // Track payload sizes if available
        if (context.Request.ContentLength.HasValue)
        {
            _performanceMonitor.RecordHistogram("honua_request_size_bytes", context.Request.ContentLength.Value, tags);
        }
    }

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
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Don't let memory monitoring failures impact request processing
            PerformanceMonitoringLog.MemoryMonitoringFailed(_logger, ex.Message, ex);
        }
    }

    private static string GetNormalizedEndpoint(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint is RouteEndpoint routeEndpoint &&
            !string.IsNullOrWhiteSpace(routeEndpoint.RoutePattern.RawText))
        {
            return routeEndpoint.RoutePattern.RawText!;
        }

        return ResolveKnownFrameworkEndpoint(context.Request.Path) ?? UnmatchedEndpointMetricTag;
    }

    private static string? ResolveKnownFrameworkEndpoint(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        foreach (var candidate in KnownFrameworkEndpoints)
        {
            if (string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    private bool ShouldSampleMemory()
    {
        // Simple sampling: sample every N requests (thread-safe approximation)
        if (_options.MemorySamplingInterval <= 0)
        {
            return false;
        }

        return Environment.TickCount % _options.MemorySamplingInterval == 0;
    }
}

internal static class PerformanceMonitoringMiddlewareExtensions
{
    internal static IApplicationBuilder UsePerformanceMonitoring(this IApplicationBuilder app)
    {
        return app.UseMiddleware<PerformanceMonitoringMiddleware>();
    }

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
