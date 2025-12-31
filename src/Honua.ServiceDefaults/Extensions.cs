// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Monitoring;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Honua.ServiceDefaults;

/// <summary>
/// Service default configuration extensions for application setup.
/// </summary>
public static partial class Extensions
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
        // Bind tracing options from configuration
        var tracingOptions = new TracingOptions();
        builder.Configuration.GetSection(TracingOptions.SectionName).Bind(tracingOptions);
        builder.Services.Configure<TracingOptions>(builder.Configuration.GetSection(TracingOptions.SectionName));

        var useOtlp = !string.IsNullOrWhiteSpace(tracingOptions.OtlpEndpoint) ||
                      !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

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
                .AddMeter(HonuaTelemetry.ServiceName))
            .WithTracing(tracing =>
            {
                // Configure sampling based on options
                if (tracingOptions.SamplingRatio < 1.0)
                {
                    tracing.SetSampler(new TraceIdRatioBasedSampler(tracingOptions.SamplingRatio));
                }

                tracing
                    .AddSource(HonuaTelemetry.ServiceName)
                    .SetResourceBuilder(
                        ResourceBuilder.CreateDefault()
                            .AddService(
                                serviceName: HonuaTelemetry.ServiceName,
                                serviceVersion: HonuaTelemetry.ServiceVersion,
                                serviceInstanceId: Environment.MachineName))
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        // Enrich spans with protocol-specific tags
                        options.EnrichWithHttpRequest = (activity, request) =>
                        {
                            var path = request.Path.Value ?? string.Empty;
                            var protocol = GetProtocolFromPath(path);
                            activity.SetTag(HonuaTelemetry.Tags.Protocol, protocol);
                        };

                        options.EnrichWithHttpResponse = (activity, response) =>
                        {
                            if (response.StatusCode >= 400)
                            {
                                activity.SetTag(HonuaTelemetry.Tags.Error, true);
                            }
                        };

                        // Filter out health check endpoints based on options
                        options.Filter = context =>
                        {
                            if (tracingOptions.TraceHealthEndpoints)
                            {
                                return true;
                            }

                            var path = context.Request.Path.Value ?? string.Empty;
                            return !path.StartsWith("/healthz", StringComparison.OrdinalIgnoreCase) &&
                                   !path.StartsWith("/alive", StringComparison.OrdinalIgnoreCase);
                        };

                        // Record exception details based on options
                        options.RecordException = tracingOptions.RecordExceptionStackTraces;
                    })
                    .AddHttpClientInstrumentation()
                    .AddNpgsql();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    /// <summary>
    /// Determines the API protocol from the request path.
    /// </summary>
    private static string GetProtocolFromPath(string path)
    {
        if (path.Contains("/FeatureServer", StringComparison.OrdinalIgnoreCase))
            return HonuaTelemetry.Protocols.FeatureServer;
        if (path.Contains("/ogc/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/collections", StringComparison.OrdinalIgnoreCase))
            return HonuaTelemetry.Protocols.OgcFeatures;
        if (path.Contains("/odata", StringComparison.OrdinalIgnoreCase))
            return HonuaTelemetry.Protocols.OData;
        if (path.Contains("/import", StringComparison.OrdinalIgnoreCase))
            return HonuaTelemetry.Protocols.Import;
        if (path.Contains("/admin", StringComparison.OrdinalIgnoreCase))
            return HonuaTelemetry.Protocols.Admin;
        if (path.Contains("/health", StringComparison.OrdinalIgnoreCase))
            return HonuaTelemetry.Protocols.Health;
        return "unknown";
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
        builder.Services.AddDefaultPerformanceMonitor();

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
                        MemoryMonitoringLog.HighMemoryPressureDetected(
                            _logger,
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
                    MemoryMonitoringLog.MemoryMonitoringServiceFailed(_logger, ex);
                    await Task.Delay(_interval, stoppingToken);
                }
            }
        }
    }

    private static partial class MemoryMonitoringLog
    {
        [LoggerMessage(
            EventId = 8501,
            Level = LogLevel.Warning,
            Message = "High memory pressure detected: {Pressure:F1}% ({AllocatedMB:F0}MB allocated)")]
        public static partial void HighMemoryPressureDetected(ILogger logger, double pressure, double allocatedMB);

        [LoggerMessage(
            EventId = 8502,
            Level = LogLevel.Error,
            Message = "Error in memory monitoring service")]
        public static partial void MemoryMonitoringServiceFailed(ILogger logger, Exception exception);
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

        return app;
    }
}
