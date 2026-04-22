// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Monitoring;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
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
    // Performance monitoring constants
    private const double DefaultSlowRequestThresholdMs = 1000.0;
    private const int DefaultMemorySamplingIntervalMs = 100;
    private const int DefaultHttpErrorStatusCode = 400;

    /// <summary>
    /// Adds the standard Honua service defaults for telemetry, health checks, and service discovery.
    /// </summary>
    /// <param name="builder">The application builder being configured.</param>
    /// <returns>The application builder for chaining.</returns>
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

    /// <summary>
    /// Adds the standard telemetry defaults without applying the full service-default stack.
    /// </summary>
    /// <param name="builder">The application builder being configured.</param>
    /// <returns>The application builder for chaining.</returns>
    public static IHostApplicationBuilder AddTelemetryDefaults(this IHostApplicationBuilder builder)
    {
        builder.AddAdaptiveSampling();
        builder.ConfigureOpenTelemetry();
        return builder;
    }

    /// <summary>
    /// Configures OpenTelemetry tracing, metrics, and logging exporters for the current application.
    /// </summary>
    /// <param name="builder">The application builder being configured.</param>
    /// <returns>The application builder for chaining.</returns>
    public static IHostApplicationBuilder ConfigureOpenTelemetry(this IHostApplicationBuilder builder)
    {
        // Bind tracing options from configuration
        var tracingOptions = new TracingOptions();
        builder.Configuration.GetSection(TracingOptions.SectionName).Bind(tracingOptions);
        builder.Services.Configure<TracingOptions>(builder.Configuration.GetSection(TracingOptions.SectionName));
        HonuaTelemetry.ConfigureExceptionRecording(
            tracingOptions.ExportExceptionDetails,
            tracingOptions.ExportExceptionDetails && tracingOptions.RecordExceptionStackTraces,
            tracingOptions.MaxExceptionDetailLength);

        // Bind Prometheus scraping options
        builder.Services.Configure<PrometheusOptions>(builder.Configuration.GetSection(PrometheusOptions.SectionName));

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
                .AddMeter(HonuaTelemetry.ServiceName)
                .AddPrometheusExporter();

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

                    // Clamp ratio to the valid [0,1] range and short-circuit the edges.
                    // TraceIdRatioBasedSampler accepts any double but documenting a negative
                    // or >1 value as either "off" or "on" is clearer than relying on library
                    // behavior for out-of-range input.
                    var ratio = Math.Clamp(tracingOptions.SamplingRatio, 0.0, 1.0);
                    if (ratio <= 0.0)
                    {
                        return new AlwaysOffSampler();
                    }

                    if (ratio < 1.0)
                    {
                        return new TraceIdRatioBasedSampler(ratio);
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
                        options.EnrichWithHttpResponse = (activity, response) =>
                        {
                            if (response.StatusCode >= DefaultHttpErrorStatusCode)
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

                        // Record exceptions explicitly through HonuaTelemetry so exported details
                        // consistently follow Honua tracing sanitization settings.
                        options.RecordException = false;
                    })
                    .AddHttpClientInstrumentation()
                    .AddNpgsql()
                    .AddRedisInstrumentation();

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
    /// Maps the native Prometheus text exposition endpoint.
    /// </summary>
    public static WebApplication MapPrometheusEndpoint(this WebApplication app)
    {
        var options = new PrometheusOptions();
        app.Configuration.GetSection(PrometheusOptions.SectionName).Bind(options);

        if (app.Services.GetService<MeterProvider>() is null)
        {
            var logger = app.Services.GetService<ILoggerFactory>()?.CreateLogger("Honua.ServiceDefaults");
            if (logger is not null)
            {
                LogPrometheusEndpointSkipped(logger, NormalizePrometheusPath(options.Path));
            }
            return app;
        }

        app.MapPrometheusScrapingEndpoint(NormalizePrometheusPath(options.Path))
            .RequireAuthorization("Admin");
        return app;
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
        return true;
    }

    private static string NormalizePrometheusPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/metrics";
        }

        var normalized = path.Trim();
        if (!normalized.StartsWith('/'))
        {
            normalized = "/" + normalized;
        }

        return normalized;
    }

    [LoggerMessage(
        EventId = 4101,
        Level = LogLevel.Warning,
        Message = "Prometheus endpoint '{Path}' was not mapped because no MeterProvider is registered.")]
    private static partial void LogPrometheusEndpointSkipped(ILogger logger, string path);

    private sealed class SpanSanitizingProcessor : BaseProcessor<Activity>
    {
        private static readonly string[] _dbStatementTags =
        [
            "db.statement",
            "db.query.text",
            "db.statement.text"
        ];

        private readonly bool _includeDbStatementText;
        private readonly bool _exportExceptionDetails;
        private readonly bool _includeExceptionStackTraces;
        private readonly int _maxAttributes;
        private readonly int _maxEvents;
        private readonly int _maxExceptionDetailLength;

        public SpanSanitizingProcessor(TracingOptions tracingOptions)
        {
            _includeDbStatementText = tracingOptions.IncludeDbStatementText;
            _exportExceptionDetails = tracingOptions.ExportExceptionDetails;
            _includeExceptionStackTraces = tracingOptions.ExportExceptionDetails && tracingOptions.RecordExceptionStackTraces;
            _maxAttributes = tracingOptions.MaxAttributesPerSpan;
            _maxEvents = tracingOptions.MaxEventsPerSpan;
            _maxExceptionDetailLength = tracingOptions.MaxExceptionDetailLength > 0
                ? tracingOptions.MaxExceptionDetailLength
                : 256;
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

            SanitizeExceptionTags(activity);

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

        private void SanitizeExceptionTags(Activity activity)
        {
            if (!_exportExceptionDetails)
            {
                activity.SetTag(HonuaTelemetry.Tags.ErrorMessage, null);
                activity.SetTag("exception.message", null);
                activity.SetTag("exception.stacktrace", null);

                if (activity.Status == ActivityStatusCode.Error)
                {
                    activity.SetStatus(ActivityStatusCode.Error);
                }

                return;
            }

            SanitizeExceptionTag(activity, HonuaTelemetry.Tags.ErrorMessage, _maxExceptionDetailLength);
            SanitizeExceptionTag(activity, "exception.message", _maxExceptionDetailLength);

            if (_includeExceptionStackTraces)
            {
                SanitizeExceptionTag(activity, "exception.stacktrace", Math.Max(_maxExceptionDetailLength, HonuaTelemetry.MinStackTraceDetailLength));
            }
            else
            {
                activity.SetTag("exception.stacktrace", null);
            }
        }

        private static void SanitizeExceptionTag(Activity activity, string tagName, int maxLength)
        {
            var value = activity.GetTagItem(tagName) as string;
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var sanitized = HonuaTelemetry.SanitizeTelemetryText(value, maxLength);
            activity.SetTag(tagName, sanitized);

            if (tagName == HonuaTelemetry.Tags.ErrorMessage && activity.Status == ActivityStatusCode.Error)
            {
                activity.SetStatus(ActivityStatusCode.Error, sanitized);
            }
        }

        private static void TrimTags(Activity activity, int maxAttributes)
        {
            var excessTags = activity.TagObjects.Skip(maxAttributes).ToList();
            if (excessTags.Count == 0)
            {
                return;
            }

            foreach (var tag in excessTags)
            {
                activity.SetTag(tag.Key, null);
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
            options.SlowRequestThreshold = TimeSpan.FromMilliseconds(DefaultSlowRequestThresholdMs);
            options.MemorySamplingInterval = DefaultMemorySamplingIntervalMs;
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

                    await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancellation is requested
                    break;
                }
                catch (Exception ex)
                {
                    MemoryMonitoringLog.MemoryMonitoringServiceFailed(_logger, ex);
                    await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
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

    /// <summary>
    /// Adds the baseline health checks used by all Honua-hosted services.
    /// </summary>
    /// <param name="builder">The application builder being configured.</param>
    /// <returns>The application builder for chaining.</returns>
    public static IHostApplicationBuilder AddDefaultHealthChecks(this IHostApplicationBuilder builder)
    {
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    /// <summary>
    /// Maps the default infrastructure endpoints exposed by Honua services.
    /// </summary>
    /// <param name="app">The web application being configured.</param>
    /// <returns>The web application for chaining.</returns>
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

        // Register smart sampling rules as singleton
        builder.Services.AddSingleton<ISmartSamplingRules, SmartSamplingRules>();

        // Register adaptive sampler as singleton
        builder.Services.AddSingleton<IAdaptiveSampler, AdaptiveSampler>();

        return builder;
    }
}
