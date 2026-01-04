// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Monitoring;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Exporter;
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
        // Adaptive sampling configuration
        builder.AddAdaptiveSampling();

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

        var otlpEndpoint = ResolveOtlpEndpoint(builder.Configuration, tracingOptions);
        var otlpHeaders = ResolveOtlpHeaders(builder.Configuration, tracingOptions);
        var useOtlp = !string.IsNullOrWhiteSpace(otlpEndpoint);

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;

            if (useOtlp)
            {
                logging.AddOtlpExporter(options => ConfigureOtlpExporter(options, otlpEndpoint, otlpHeaders));
            }
        });

        var otelBuilder = builder.Services.AddOpenTelemetry();

        otelBuilder.WithMetrics(metrics =>
        {
            metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(HonuaTelemetry.ServiceName);

            if (useOtlp)
            {
                metrics.AddOtlpExporter(options => ConfigureOtlpExporter(options, otlpEndpoint, otlpHeaders));
            }
        });

        if (tracingOptions.Enabled)
        {
            otelBuilder.WithTracing(tracing =>
            {
                // Configure sampling using DI at provider build time to avoid early ServiceProvider creation.
                tracing.SetSampler(serviceProvider =>
                {
                    var adaptiveSamplingOptions = serviceProvider.GetService<IOptions<AdaptiveSamplingOptions>>()?.Value;

                    if (adaptiveSamplingOptions?.Enabled == true)
                    {
                        var adaptiveSampler = serviceProvider.GetRequiredService<IAdaptiveSampler>();
                        return new AdaptiveOpenTelemetrySampler(adaptiveSampler, tracingOptions);
                    }

                    if (tracingOptions.SamplingRatio < 1.0)
                    {
                        return new TraceIdRatioBasedSampler(tracingOptions.SamplingRatio);
                    }

                    return new AlwaysOnSampler();
                });

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

                if (ShouldAddSpanSanitizer(tracingOptions))
                {
                    tracing.AddProcessor(new SpanSanitizingProcessor(tracingOptions));
                }

                if (useOtlp)
                {
                    tracing.AddOtlpExporter(options => ConfigureOtlpExporter(options, otlpEndpoint, otlpHeaders));
                }
            });
        }

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

    private static string? ResolveOtlpEndpoint(IConfiguration configuration, TracingOptions tracingOptions)
    {
        if (!string.IsNullOrWhiteSpace(tracingOptions.OtlpEndpoint))
        {
            return tracingOptions.OtlpEndpoint;
        }

        return configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
    }

    private static string? ResolveOtlpHeaders(IConfiguration configuration, TracingOptions tracingOptions)
    {
        if (!string.IsNullOrWhiteSpace(tracingOptions.OtlpHeaders))
        {
            return tracingOptions.OtlpHeaders;
        }

        return configuration["OTEL_EXPORTER_OTLP_HEADERS"];
    }

    private static void ConfigureOtlpExporter(OtlpExporterOptions options, string? endpoint, string? headers)
    {
        if (!string.IsNullOrWhiteSpace(endpoint) &&
            Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            options.Endpoint = uri;
        }

        if (!string.IsNullOrWhiteSpace(headers))
        {
            options.Headers = headers;
        }
    }

    private static bool ShouldAddSpanSanitizer(TracingOptions tracingOptions)
    {
        return !tracingOptions.IncludeDbStatementText ||
               tracingOptions.MaxAttributesPerSpan > 0 ||
               tracingOptions.MaxEventsPerSpan > 0;
    }

    private sealed class SpanSanitizingProcessor : BaseProcessor<Activity>
    {
        private static readonly string[] _dbStatementTags =
        [
            "db.statement",
            "db.query.text",
            "db.statement.text"
        ];

        private readonly bool _includeDbStatementText;
        private readonly int _maxAttributes;
        private readonly int _maxEvents;

        public SpanSanitizingProcessor(TracingOptions tracingOptions)
        {
            _includeDbStatementText = tracingOptions.IncludeDbStatementText;
            _maxAttributes = tracingOptions.MaxAttributesPerSpan;
            _maxEvents = tracingOptions.MaxEventsPerSpan;
        }

        public override void OnEnd(Activity activity)
        {
            if (!_includeDbStatementText)
            {
                foreach (var tag in _dbStatementTags)
                {
                    activity.SetTag(tag, null);
                }
            }

            if (_maxAttributes > 0)
            {
                TrimTags(activity, _maxAttributes);
            }

            if (_maxEvents > 0)
            {
                var eventCount = activity.Events.Count();
                if (eventCount > _maxEvents)
                {
                    activity.SetTag("otel.events.truncated", eventCount - _maxEvents);
                }
            }
        }

        private static void TrimTags(Activity activity, int maxAttributes)
        {
            var tags = activity.TagObjects.ToList();
            if (tags.Count <= maxAttributes)
            {
                return;
            }

            for (var i = maxAttributes; i < tags.Count; i++)
            {
                activity.SetTag(tags[i].Key, null);
            }
        }
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

    /// <summary>
    /// Adds adaptive sampling services for intelligent distributed tracing.
    /// </summary>
    private static IHostApplicationBuilder AddAdaptiveSampling(this IHostApplicationBuilder builder)
    {
        // Bind adaptive sampling configuration from environment variables
        builder.Services.Configure<AdaptiveSamplingOptions>(
            builder.Configuration.GetSection(AdaptiveSamplingOptions.SectionName));

        // Register system metrics collector as singleton
        builder.Services.AddSingleton<ISystemMetricsCollector, SystemMetricsCollector>();

        // Register adaptive sampler as singleton
        builder.Services.AddSingleton<IAdaptiveSampler, AdaptiveSampler>();

        return builder;
    }
}
