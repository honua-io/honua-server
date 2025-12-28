// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Honua.ServiceDefaults;

/// <summary>
/// Service default configuration extensions for application setup.
/// </summary>
// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Server.Features.Infrastructure.Middleware;
using Honua.Server.Features.Infrastructure.Monitoring;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Honua.ServiceDefaults;

/// <summary>
/// Service default configuration extensions for application setup.
/// </summary>
public static class Extensions
{
    public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder)
    {
        // OpenTelemetry (traces, metrics, logs)
        builder.ConfigureOpenTelemetry();

        // Performance monitoring
        builder.AddPerformanceMonitoring();

        // Health checks
        builder.AddDefaultHealthChecks();

        // Service discovery
        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddServiceDiscovery();
        });

        return builder;
    }

    public static IHostApplicationBuilder ConfigureOpenTelemetry(this IHostApplicationBuilder builder)
    {
        var useOtlp = !string.IsNullOrWhiteSpace(
            builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;

            if (useOtlp)
            {
                logging.AddOtlpExporter();
            }
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("Honua.Server"))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation());

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static IHostApplicationBuilder AddOpenTelemetryExporters(this IHostApplicationBuilder builder)
    {
        var useOtlp = !string.IsNullOrWhiteSpace(
            builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlp)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }

    /// <summary>
    /// Adds performance monitoring services to the application.
    /// </summary>
    /// <param name="builder">The host application builder</param>
    /// <returns>The host application builder for chaining</returns>
    public static IHostApplicationBuilder AddPerformanceMonitoring(this IHostApplicationBuilder builder)
    {
        // Add performance monitoring services
        builder.Services.AddSingleton<IPerformanceMonitor, DefaultPerformanceMonitor>();

        // Configure performance monitoring options
        builder.Services.Configure<PerformanceMonitoringOptions>(options =>
        {
            options.EnableMemoryTracking = true;
            options.SlowRequestThreshold = TimeSpan.FromMilliseconds(1000);
            options.MemorySamplingInterval = 100;
            options.EnableDetailedRequestTracking = true;
        });

        // Add memory monitoring background service
        builder.Services.AddHostedService<MemoryMonitoringService>();

        return builder;
    }

    /// <summary>
    /// Background service for periodic memory monitoring.
    /// </summary>
    internal sealed class MemoryMonitoringService : BackgroundService
    {
        private readonly IPerformanceMonitor _performanceMonitor;
        private readonly ILogger<MemoryMonitoringService> _logger;
        private readonly TimeSpan _interval = TimeSpan.FromSeconds(30);

        public MemoryMonitoringService(
            IPerformanceMonitor performanceMonitor,
            ILogger<MemoryMonitoringService> logger)
        {
            _performanceMonitor = performanceMonitor;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var memoryUsage = MemoryMonitor.GetMemoryUsage();

                    _performanceMonitor.RecordMemoryUsage(
                        memoryUsage.AllocatedBytes,
                        memoryUsage.Gen0Collections,
                        memoryUsage.Gen1Collections,
                        memoryUsage.Gen2Collections);

                    // Log high memory pressure
                    if (memoryUsage.IsHighMemoryPressure)
                    {
                        _logger.LogWarning(
                            "High memory pressure detected: {Pressure:F1}% ({AllocatedMB:F0}MB allocated)",
                            memoryUsage.MemoryPressurePercentage,
                            memoryUsage.AllocatedBytes / (1024.0 * 1024.0));
                    }

                    await Task.Delay(_interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancellation is requested
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in memory monitoring service");
                    await Task.Delay(_interval, stoppingToken);
                }
            }
        }
    }

    public static IHostApplicationBuilder AddDefaultHealthChecks(this IHostApplicationBuilder builder)
    {
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Map health endpoints for Aspire dashboard
        if (app.Environment.IsDevelopment())
        {
            app.MapHealthChecks("/healthz");
            app.MapHealthChecks("/alive", new HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains("live")
            });
        }

        // Map metrics endpoints
        app.MapMetricsEndpoints();

        return app;
    }
}
