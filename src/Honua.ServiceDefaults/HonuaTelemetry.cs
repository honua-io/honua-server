// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using Honua.Core.Features.Infrastructure.Monitoring;

namespace Honua.ServiceDefaults;

public static class HonuaTelemetry
{
    public const string ServiceName = "Honua";

    public const string ServiceVersion = "1.0.0";

    public static readonly ActivitySource ActivitySource = new(ServiceName, ServiceVersion);

    public static readonly Meter Meter = new(ServiceName, ServiceVersion);

    // ── SLO metric contract (observability/slo-metric-contract.json) ──────────────────────────────
    //
    // The three instruments below are the platform SLO contract: alert rules (honua-helm
    // PrometheusRule, honua-devops recording rules) and the honua-release observability gate
    // reference their Prometheus series names directly, in a different repository.
    //
    // NONE of them declares an OpenTelemetry `unit`, and that omission is load-bearing. The
    // OTel Prometheus exporter derives the exposed series name from BOTH the instrument name and
    // its unit: it maps the unit through the UCUM table and appends it when the name does not
    // already end in the mapped form. With units declared, these instruments used to be exported
    // as `honua_geoservices_error_total_errors_total`, `honua_request_error_total_errors_total`
    // and `honua_serving_request_duration_ms_milliseconds` — names nothing in the platform
    // referenced, so every SLO ratio divided by an absent series and silently produced no series
    // at all (an empty PromQL vector never fires an alert). Dropping the unit makes the exported
    // name identical to the instrument name, which is the name the whole platform already assumes
    // (see docker/monitoring/grafana/dashboards/honua-serving-overview.json). The unit stays
    // documented in the instrument name (`_ms`) and description.
    //
    // Do not add a `unit` argument to these three instruments without also updating every
    // consumer; SloMetricContractTests scrapes the real /metrics exposition and fails if the
    // exported names drift from the contract file.

    public static readonly Counter<long> GeoServicesErrors = Meter.CreateCounter<long>(
        "honua_geoservices_error_total",
        unit: null,
        "Total GeoServices/ArcGIS REST error envelopes produced, tagged by service_type, operation, and error_code.");

    public static readonly Counter<long> RequestErrors = Meter.CreateCounter<long>(
        "honua_request_error_total",
        unit: null,
        "Total protocol error envelopes produced across all surfaces, tagged by service_type, operation, error_code, and in_band.");

    public static readonly Histogram<double> ServingRequestDuration = Meter.CreateHistogram<double>(
        "honua_serving_request_duration_ms",
        unit: null,
        "Serving-plane request latency in milliseconds, tagged by protocol, operation, and status_class.");

    private const int DefaultMaxExceptionDetailLength = 256;

    // Serving-latency rolling-window aggregation defaults (Part 1, #2463).
    private const int DefaultServingLatencyWindowSeconds = 300; // 5 minutes
    private const int DefaultServingLatencySamplesPerProtocol = 4096;

    // Volatile so a startup ConfigureServingLatency swap is visible to concurrent recorders.
    // Bounded, lock-minimal per-protocol reservoirs; see ServingLatencyAggregator.
    private static volatile ServingLatencyAggregator _servingLatency = new(
        TimeSpan.FromSeconds(DefaultServingLatencyWindowSeconds),
        DefaultServingLatencySamplesPerProtocol);

    // Serving-plane HTTP status boundaries for the coarse status_class tag.
    private const int HttpStatusClientErrorFloor = 400;
    private const int HttpStatusRedirectFloor = 300;
    private const int HttpStatusSuccessFloor = 200;
    private const int HttpStatusServerErrorFloor = 500;

    // Performance categorization thresholds
    private const double FastLatencyThresholdMs = 100.0;
    private const double MediumLatencyThresholdMs = 1000.0;
    private const double SlowLatencyThresholdMs = 5000.0;

    // Memory categorization thresholds
    private const long SmallMemoryThresholdBytes = 1024; // 1KB
    private const long MediumMemoryThresholdBytes = 1024 * 1024; // 1MB
    private const long LargeMemoryThresholdBytes = 10 * 1024 * 1024; // 10MB

    // Stack trace detail threshold
    internal const int MinStackTraceDetailLength = 2048;
    private static readonly Regex EmailPattern = new(
        "\\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\\.[A-Z]{2,}\\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex KeyValuePattern = new(
        "(?i)\\b(password|pwd|secret|token|api[-_]?key|access[-_]?key|client[-_]?secret|private[-_]?key|auth[-_]?token|session[-_]?id|refresh[-_]?token|database|connectionstring|connection[-_]?string)\\b\\s*([:=])\\s*([^;\\s]+)",
        RegexOptions.CultureInvariant);
    private static readonly Regex AuthorizationPattern = new(
        "(?i)\\bauthorization\\b\\s*([:=])\\s*([^;\\s]+)",
        RegexOptions.CultureInvariant);
    private static readonly Regex BearerPattern = new(
        "(?i)\\bbearer\\s+[A-Za-z0-9-._~+/]+=*",
        RegexOptions.CultureInvariant);
    private static readonly Regex JwtPattern = new(
        "\\bey[A-Za-z0-9-_]+\\.[A-Za-z0-9-_]+\\.[A-Za-z0-9-_.+/=]*",
        RegexOptions.CultureInvariant);
    private static readonly Regex ConnectionStringPattern = new(
        "(?i)(server|host|database|uid|user\\s*id|pwd|password|trusted_connection)\\s*=\\s*[^;]+",
        RegexOptions.CultureInvariant);

    private static volatile bool _exportExceptionDetails;
    private static volatile bool _includeExceptionStackTraces;
    private static volatile int _maxExceptionDetailLength = DefaultMaxExceptionDetailLength;

    public static void ConfigureExceptionRecording(bool exportDetails, bool includeStackTraces, int maxDetailLength = DefaultMaxExceptionDetailLength)
    {
        _exportExceptionDetails = exportDetails;
        _includeExceptionStackTraces = includeStackTraces;
        var maxLength = maxDetailLength > 0 ? maxDetailLength : DefaultMaxExceptionDetailLength;
        _maxExceptionDetailLength = maxLength;

        // Span-event attributes are fixed once added, so providers that record exception text on
        // events (retry callbacks) apply this same policy at construction (#5475).
        TelemetryExceptionDetailPolicy.Configure(exportDetails
            ? message => SanitizeTelemetryText(message, maxLength)
            : static _ => null);
    }

    public static void ConfigureServingLatency(TimeSpan window, int samplesPerProtocol)
    {
        var effectiveWindow = window > TimeSpan.Zero ? window : TimeSpan.FromSeconds(DefaultServingLatencyWindowSeconds);
        var effectiveSamples = samplesPerProtocol > 0 ? samplesPerProtocol : DefaultServingLatencySamplesPerProtocol;
        _servingLatency = new ServingLatencyAggregator(effectiveWindow, effectiveSamples);
    }

    public static ServingLatencySnapshot GetServingLatencySnapshot() => _servingLatency.GetSnapshot();

    public static void RecordErrorEnvelope(string serviceType, string operation, int errorCode, bool isGeoServices) =>
        RecordErrorEnvelope(serviceType, operation, errorCode, isGeoServices, errorCode);

    public static void RecordErrorEnvelope(string serviceType, string operation, int errorCode, bool isGeoServices, int httpStatusCode)
    {
        serviceType = string.IsNullOrWhiteSpace(serviceType) ? "unknown" : serviceType;
        operation = string.IsNullOrWhiteSpace(operation) ? "unknown" : operation;

        // An error delivered with a non-error HTTP status (2xx/3xx) is "in band":
        // the body carries an {error} envelope but the transport status reads
        // success, so load-balancer/SLO 5xx metrics never see it. This is the
        // keystone signal the audit is blind to today.
        var inBand = httpStatusCode < 400;

        var requestTags = new TagList
        {
            { "service_type", serviceType },
            { "operation", operation },
            { "error_code", errorCode },
            { "in_band", inBand },
        };
        RequestErrors.Add(1, requestTags);

        if (isGeoServices)
        {
            var geoTags = new TagList
            {
                { "service_type", serviceType },
                { "operation", operation },
                { "error_code", errorCode },
            };
            GeoServicesErrors.Add(1, geoTags);
        }

        var appErrorTags = new TagList
        {
            { "error_type", "protocol_error" },
            { "operation", operation },
            { "service_type", serviceType },
        };
        PerformanceMetrics.ApplicationErrors.Add(1, appErrorTags);
    }

    public static void RecordServingRequest(string protocol, string? operation, int statusCode, double durationMs)
    {
        if (string.IsNullOrWhiteSpace(protocol) || durationMs < 0)
        {
            return;
        }

        var tags = new TagList
        {
            { Tags.Protocol, protocol },
            { Tags.Operation, string.IsNullOrWhiteSpace(operation) ? "unknown" : operation },
            { "status_class", ClassifyStatus(statusCode) },
        };
        ServingRequestDuration.Record(durationMs, tags);

        // Also feed the in-process rolling-window aggregation that backs the admin
        // ops-health snapshot (per-protocol p50/p95/p99 + error rate). Allocation-free
        // hot path after the first sample per protocol; see ServingLatencyAggregator.
        _servingLatency.Record(protocol, durationMs, statusCode);
    }

    private static string ClassifyStatus(int statusCode) => statusCode switch
    {
        >= HttpStatusServerErrorFloor => "5xx",
        >= HttpStatusClientErrorFloor => "4xx",
        >= HttpStatusRedirectFloor => "3xx",
        >= HttpStatusSuccessFloor => "2xx",
        _ => "1xx"
    };

    public static class Activities
    {
        public const string HttpRequest = "honua.http.request";

        public const string DatabaseQuery = "honua.db.query";

        public const string DatabaseConnection = "honua.db.connection";

        public const string FeatureQuery = "honua.feature.query";

        public const string FeatureEdit = "honua.feature.edit";

        public const string TileGeneration = "honua.tile.generate";

        public const string MapRender = "honua.map.render";

        public const string FeatureIdentify = "honua.feature.identify";

        public const string FileImport = "honua.import.file";

        public const string Authentication = "honua.auth.validate";

        public const string CacheOperation = "honua.cache.operation";

        public const string MapServerExport = "honua.mapserver.export";

        public const string MapServerIdentify = "honua.mapserver.identify";

        public const string StaticMapRender = "honua.staticmap.render";

        public const string BusinessIntelligence = "honua.bi.calculation";

        public const string SecurityMonitoring = "honua.security.check";

        public const string PerformanceAnalysis = "honua.performance.analysis";

        public const string AnomalyDetection = "honua.ml.anomaly_detection";

        public const string ExecutionAdmission = "honua.execution.admission";

        public const string GeoprocessingDispatch = "honua.geoprocessing.dispatch";

        public const string ShareExportTrigger = "honua.share.export.trigger";
    }

    public static class Tags
    {
        public const string Protocol = "honua.protocol";

        public const string ServiceId = "honua.service.id";

        public const string LayerId = "honua.layer.id";

        public const string CollectionId = "honua.collection.id";

        public const string TaskName = "honua.task.name";

        public const string JobId = "honua.job.id";

        public const string ShareExportId = "honua.share.export.id";

        public const string ShareRunId = "honua.share.run.id";

        public const string ShareDestinationType = "honua.share.destination_type";

        public const string ParameterName = "honua.parameter.name";

        public const string Operation = "honua.operation";

        public const string FeatureCount = "honua.feature.count";

        public const string DbQueryType = "db.query.type";

        public const string CorrelationId = "honua.correlation.id";

        public const string Error = "error";

        public const string ErrorMessage = "error.message";

        public const string TileZ = "honua.tile.z";

        public const string TileX = "honua.tile.x";

        public const string TileY = "honua.tile.y";

        public const string UserId = "honua.user.id";

        public const string ClientId = "honua.client.id";

        public const string GeoRegion = "honua.geo.region";

        public const string LatencyCategory = "honua.latency.category";

        public const string SecurityRisk = "honua.security.risk_level";

        public const string BusinessMetric = "honua.business.metric_type";

        public const string PerformanceBaseline = "honua.performance.baseline_pct";

        public const string CacheTier = "honua.cache.tier";

        public const string QueryComplexity = "honua.query.complexity_score";

        public const string RowsScanned = "honua.catalog.rows_scanned";

        public const string RowsReturned = "honua.catalog.rows_returned";

        public const string ProviderDurationMs = "honua.provider.duration_ms";

        public const string MemoryCategory = "honua.memory.allocation_category";
    }

    public static class Protocols
    {
        public const string Grpc = "Grpc";

        public const string FeatureServer = "FeatureServer";

        public const string OgcFeatures = "OGC-Features";

        public const string OgcTiles = "OGC-Tiles";

        public const string PMTiles = "PMTiles";

        public const string Terrain = "Terrain";

        public const string Elevation = "Elevation";

        public const string OgcMaps = "OGC-Maps";

        public const string OgcCoverages = "OGC-Coverages";

        public const string OgcProcesses = "OGC-Processes";

        public const string OgcRecords = "OGC-Records";

        public const string MapServer = "MapServer";

        public const string ImageServer = "ImageServer";

        public const string VectorTileServer = "VectorTileServer";

        public const string OData = "OData";

        public const string Import = "Import";

        public const string Admin = "Admin";

        public const string Health = "Health";

        public const string Monitoring = "Monitoring";

        public const string BusinessIntelligence = "BI";

        public const string GeometryService = "GeometryService";

        public const string StaticMap = "StaticMap";

        public const string PrintingTools = "PrintingTools";

        public const string Wfs20 = "WFS-2.0";

        public const string Wcs10 = "WCS-1.0.0";

        public const string Wcs20 = "WCS-2.0.1";

        public const string Wps20 = "WPS-2.0.2";

        public const string Streaming = "Streaming";

        public const string GPServer = "GPServer";

        public const string NAServer = "NAServer";

        public const string Stac = "STAC";

        public const string SensorThings = "SensorThings";

        public const string Mcp = "Mcp";

        // Probe and scrape traffic is left out of the serving histogram, because counting it dilutes the error-budget denominator toward zero.
        public static readonly string[] NonServingPlane = [Health, Monitoring];

        public static readonly string[] GeoServicesFamily =
        [
            FeatureServer,
            MapServer,
            ImageServer,
            VectorTileServer,
            GPServer,
            NAServer,
            GeometryService,
            PrintingTools,
            StaticMap
        ];
    }

    public static Activity? StartActivity(string name, ActivityKind kind = ActivityKind.Internal)
    {
        return ActivitySource.StartActivity(name, kind);
    }

    public static Activity? StartDatabaseActivity(string queryType, string? layerId = null, int? complexityScore = null)
    {
        var activity = ActivitySource.StartActivity(Activities.DatabaseQuery, ActivityKind.Client);
        activity?.SetTag(Tags.DbQueryType, queryType);

        if (layerId != null)
        {
            activity?.SetTag(Tags.LayerId, layerId);
        }

        if (complexityScore.HasValue)
        {
            activity?.SetTag(Tags.QueryComplexity, complexityScore.Value);
        }

        return activity;
    }

    public static Activity? StartFeatureActivity(string operation, string protocol, string layerId, string? correlationId = null)
    {
        var activityName = operation switch
        {
            "query" => Activities.FeatureQuery,
            "edit" or "add" or "update" or "delete" => Activities.FeatureEdit,
            _ => $"honua.feature.{operation}"
        };

        var activity = ActivitySource.StartActivity(activityName, ActivityKind.Internal);
        activity?.SetTag(Tags.Operation, operation);
        activity?.SetTag(Tags.Protocol, protocol);
        activity?.SetTag(Tags.LayerId, layerId);

        if (correlationId != null)
        {
            activity?.SetTag(Tags.CorrelationId, correlationId);
        }

        return activity;
    }

    public static Activity? StartBusinessIntelligenceActivity(string metricType, string? userId = null, string? clientId = null)
    {
        var activity = ActivitySource.StartActivity(Activities.BusinessIntelligence, ActivityKind.Internal);
        activity?.SetTag(Tags.BusinessMetric, metricType);

        if (userId != null)
        {
            activity?.SetTag(Tags.UserId, userId);
        }

        if (clientId != null)
        {
            activity?.SetTag(Tags.ClientId, clientId);
        }

        return activity;
    }

    public static Activity? StartSecurityActivity(string checkType, string riskLevel = "low")
    {
        var activity = ActivitySource.StartActivity(Activities.SecurityMonitoring, ActivityKind.Internal);
        activity?.SetTag(Tags.Operation, checkType);
        activity?.SetTag(Tags.SecurityRisk, riskLevel);

        return activity;
    }

    public static void RecordException(Activity? activity, Exception exception)
    {
        if (activity == null)
        {
            return;
        }

        var exportExceptionDetails = _exportExceptionDetails;
        var includeExceptionStackTraces = _includeExceptionStackTraces;
        var maxExceptionDetailLength = _maxExceptionDetailLength;
        var sanitizedMessage = SanitizeTelemetryText(exception.Message, maxExceptionDetailLength);

        if (exportExceptionDetails && !string.IsNullOrWhiteSpace(sanitizedMessage))
        {
            activity.SetStatus(ActivityStatusCode.Error, sanitizedMessage);
            activity.SetTag(Tags.ErrorMessage, sanitizedMessage);
        }
        else
        {
            activity.SetStatus(ActivityStatusCode.Error);
            activity.SetTag(Tags.ErrorMessage, null);
        }

        activity.SetTag(Tags.Error, true);
        var tags = new ActivityTagsCollection
        {
            { "exception.type", exception.GetType().FullName }
        };

        if (exportExceptionDetails && !string.IsNullOrWhiteSpace(sanitizedMessage))
        {
            tags.Add("exception.message", sanitizedMessage);
        }

        if (exportExceptionDetails &&
            includeExceptionStackTraces &&
            !string.IsNullOrWhiteSpace(exception.StackTrace))
        {
            tags.Add("exception.stacktrace", SanitizeTelemetryText(exception.StackTrace, Math.Max(maxExceptionDetailLength, MinStackTraceDetailLength)));
        }

        activity.AddEvent(new ActivityEvent("exception", tags: tags));
    }

    internal static string SanitizeTelemetryText(string? value, int maxLength = DefaultMaxExceptionDetailLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sanitized = value.Trim();
        sanitized = EmailPattern.Replace(sanitized, "***");
        sanitized = KeyValuePattern.Replace(sanitized, "$1$2***");
        sanitized = AuthorizationPattern.Replace(sanitized, "Authorization$1***");
        sanitized = BearerPattern.Replace(sanitized, "Bearer ***");
        sanitized = JwtPattern.Replace(sanitized, "eyJ***");
        sanitized = ConnectionStringPattern.Replace(sanitized, "$1=***");

        if (maxLength > 0 && sanitized.Length > maxLength)
        {
            sanitized = sanitized[..maxLength] + "...";
        }

        return sanitized;
    }

    public static void SetSuccess(Activity? activity, int featureCount = 0, int? performanceBaseline = null)
    {
        if (activity == null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Ok);
        if (featureCount > 0)
        {
            activity.SetTag(Tags.FeatureCount, featureCount);
        }

        if (performanceBaseline.HasValue)
        {
            activity.SetTag(Tags.PerformanceBaseline, performanceBaseline.Value);
        }
    }

    public static void SetError(Activity? activity)
    {
        if (activity == null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error);
        activity.SetTag(Tags.Error, true);
    }

    public static void CategorizeLatency(Activity? activity, double durationMs)
    {
        if (activity == null)
        {
            return;
        }

        var category = durationMs switch
        {
            < FastLatencyThresholdMs => "fast",
            < MediumLatencyThresholdMs => "medium",
            < SlowLatencyThresholdMs => "slow",
            _ => "timeout"
        };

        activity.SetTag(Tags.LatencyCategory, category);
    }

    public static void CategorizeMemoryAllocation(Activity? activity, long allocationBytes)
    {
        if (activity == null)
        {
            return;
        }

        var category = allocationBytes switch
        {
            <= SmallMemoryThresholdBytes => "small",
            < MediumMemoryThresholdBytes => "medium",
            < LargeMemoryThresholdBytes => "large",
            _ => "xlarge"
        };

        activity.SetTag(Tags.MemoryCategory, category);
    }
}

public sealed class HonuaTelemetryScope : IDisposable
{
    private readonly Activity? _activity;
    private bool _disposed;

    private HonuaTelemetryScope(Activity? activity)
    {
        _activity = activity;
    }

    public static HonuaTelemetryScope StartFeature(
        string operation,
        string protocol,
        string layerId,
        string? correlationId = null)
    {
        var activity = HonuaTelemetry.StartFeatureActivity(operation, protocol, layerId, correlationId);
        return new HonuaTelemetryScope(activity);
    }

    public HonuaTelemetryScope WithTag(string key, object? value)
    {
        _activity?.SetTag(key, value);
        return this;
    }

    public void SetSuccess(int featureCount = 0)
    {
        HonuaTelemetry.SetSuccess(_activity, featureCount);
    }

    public void SetError()
    {
        HonuaTelemetry.SetError(_activity);
    }

    public void RecordException(Exception exception)
    {
        HonuaTelemetry.RecordException(_activity, exception);
    }

    public void CategorizeLatency(double durationMs)
    {
        HonuaTelemetry.CategorizeLatency(_activity, durationMs);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _activity?.Dispose();
        _disposed = true;
    }
}
