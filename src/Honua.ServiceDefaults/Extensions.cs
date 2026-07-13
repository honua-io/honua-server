// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Honua.Core.Configuration;
using Honua.Core.Features.Federation.Services;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.Studio.Services;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Extensions.AWS.Trace;
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
    private static readonly string[] _meterNames =
    [
        HonuaTelemetry.ServiceName,
        "Honua.Wfs20.Transactions",
        "Honua.Database.ConnectionPool",
        "Honua.Production.Metrics",
        FederationMetrics.MeterName,
        LambdaTelemetry.MeterName,
        // PA-112: the SensorThings observation-stream slow-consumer drop counter was
        // tracked in-memory but never exported as an OTel metric.
        "Honua.SensorThings"
    ];
    private static readonly string[] _activitySourceNames =
    [
        HonuaTelemetry.ServiceName,
        "Honua.Server.Export",
        "Honua.Wfs20.Transactions",
        "Honua.Core.Metadata",
        "Honua.MySql.FeatureDataAccess",
        StudioPackageLifecycleService.ActivitySourceName,
        "Honua.PackageReview",
        "Honua.Routing",
        "Honua.Geocoding",
        // PA-014 / PA-063: declared ActivitySources that were never registered with the
        // OTel TracerProvider, so every span they emit was silently dropped.
        "Honua.GeoServer.Import",
        "Honua.Spec.Parse",
        "Honua.Spec.Validation",
        "Honua.Server.Reporting",
        // PA-160: provider data-access ActivitySources (spans created but dropped).
        "Honua.SqlServer.FeatureStore",
        "Honua.Snowflake.FeatureStore",
        "Honua.Redshift.FeatureStore",
        "Honua.Oracle.FeatureStore",
        // PA-016 / PA-017 / PA-018 / PA-019 / PA-020: hot-path ActivitySources added by the
        // 2026-07 observability audit (#2401) that previously had no span coverage at all.
        "Honua.Import.Jobs",
        "Honua.Federation",
        "Honua.Core.Edit",
        "Honua.Core.FeatureStore",
        // PA-108: federated ArcGIS REST provider query span (paging loop).
        "Honua.ArcGisRest"
    ];

    /// <summary>
    /// Adds the standard Honua service defaults for telemetry, health checks, and service discovery.
    /// </summary>
    /// <param name="builder">The application builder being configured.</param>
    /// <returns>The application builder for chaining.</returns>
    [RequiresUnreferencedCode("Calls AddAdaptiveSampling which binds configuration via Configure<TOptions>(IConfiguration).")]
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
    [RequiresUnreferencedCode("Calls AddAdaptiveSampling which binds configuration via Configure<TOptions>(IConfiguration).")]
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
        HonuaTelemetry.ConfigureServingLatency(
            TimeSpan.FromSeconds(tracingOptions.ServingLatencyWindowSeconds),
            tracingOptions.ServingLatencySamplesPerProtocol);

        // Bind Prometheus scraping options
        builder.Services.Configure<PrometheusOptions>(builder.Configuration.GetSection(PrometheusOptions.SectionName));

        var otlpEndpoint = ResolveOtlpEndpoint(builder.Configuration, tracingOptions);
        var otlpHeaders = ResolveOtlpHeaders(builder.Configuration, tracingOptions);
        var useOtlp = !string.IsNullOrWhiteSpace(otlpEndpoint);
        var useXRay = ResolveXRayEnabled(builder.Configuration, tracingOptions);

        // Record the Lambda cold start once during process initialization so the
        // counter/init-duration histogram are emitted through the shared meter.
        // No-op off Lambda.
        LambdaTelemetry.RecordColdStart();

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
                .AddProcessInstrumentation()
                .AddMeter(_meterNames)
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

                // AWS X-Ray-compatible trace IDs + propagation. Gated so non-AWS
                // runs are unaffected. Spans still export through the existing
                // OTLP exporter (pointed at an ADOT/X-Ray collector); this only
                // changes ID generation and context propagation so HTTP -> DB ->
                // render spans nest into the Lambda/API Gateway X-Ray trace.
                if (useXRay)
                {
                    tracing.AddXRayTraceId();
                    Sdk.SetDefaultTextMapPropagator(new AWSXRayPropagator());
                }

                var resourceBuilder = ResourceBuilder.CreateDefault()
                    .AddService(
                        serviceName: HonuaTelemetry.ServiceName,
                        serviceVersion: HonuaTelemetry.ServiceVersion,
                        serviceInstanceId: Environment.MachineName);

                if (useXRay)
                {
                    // Surface FaaS/cloud resource attributes so the X-Ray service
                    // map is correctly attributed to the Lambda function.
                    resourceBuilder.AddAttributes(BuildAwsResourceAttributes());
                }

                tracing
                    .AddSource(_activitySourceNames)
                    .SetResourceBuilder(resourceBuilder)
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

    /// <summary>
    /// Resolves whether AWS X-Ray tracing should be enabled. Precedence:
    /// explicit <c>Tracing:XRay:Enabled</c> configuration first, then the
    /// AWS-provided environment signals (<c>AWS_XRAY_TRACING_ENABLED</c>, or an
    /// <c>AWS_XRAY_DAEMON_ADDRESS</c> present alongside a Lambda runtime).
    /// </summary>
    private static bool ResolveXRayEnabled(IConfiguration configuration, TracingOptions tracingOptions)
    {
        if (tracingOptions.XRay.Enabled)
        {
            return true;
        }

        // Explicit opt-out via configuration wins over environment inference.
        var configured = configuration["Tracing:XRay:Enabled"];
        if (bool.TryParse(configured, out var configuredEnabled))
        {
            return configuredEnabled;
        }

        var envFlag = configuration["AWS_XRAY_TRACING_ENABLED"];
        if (bool.TryParse(envFlag, out var envEnabled))
        {
            return envEnabled;
        }

        // The Lambda/ECS runtime injects AWS_XRAY_DAEMON_ADDRESS only when
        // active tracing is turned on, so treat its presence on Lambda as opt-in.
        return LambdaTelemetry.IsRunningOnLambda
            && !string.IsNullOrEmpty(configuration["AWS_XRAY_DAEMON_ADDRESS"]);
    }

    private static KeyValuePair<string, object>[] BuildAwsResourceAttributes()
    {
        var attributes = new List<KeyValuePair<string, object>>
        {
            new("cloud.provider", "aws"),
        };

        if (LambdaTelemetry.FunctionName is { Length: > 0 } functionName)
        {
            attributes.Add(new KeyValuePair<string, object>("cloud.platform", "aws_lambda"));
            attributes.Add(new KeyValuePair<string, object>("faas.name", functionName));

            if (LambdaTelemetry.FunctionVersion is { Length: > 0 } functionVersion)
            {
                attributes.Add(new KeyValuePair<string, object>("faas.version", functionVersion));
            }

            if (LambdaTelemetry.MemoryLimitMib > 0)
            {
                attributes.Add(new KeyValuePair<string, object>(
                    "faas.max_memory",
                    LambdaTelemetry.MemoryLimitMib * 1024L * 1024L));
            }
        }

        var region = Environment.GetEnvironmentVariable("AWS_REGION");
        if (!string.IsNullOrEmpty(region))
        {
            attributes.Add(new KeyValuePair<string, object>("cloud.region", region));
        }

        return [.. attributes];
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
                // Intentionally generic: this is a long-running background monitoring
                // loop. A single failed iteration (e.g. transient GC-info read failure)
                // must not kill the host's background service; log and keep polling.
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
