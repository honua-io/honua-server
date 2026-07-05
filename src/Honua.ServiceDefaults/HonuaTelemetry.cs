// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using Honua.Core.Features.Infrastructure.Monitoring;

namespace Honua.ServiceDefaults;

/// <summary>
/// Provides centralized telemetry instrumentation for Honua Server.
/// Contains ActivitySource for distributed tracing and Meter for custom metrics.
/// </summary>
public static class HonuaTelemetry
{
    /// <summary>
    /// The name used for the Honua ActivitySource and Meter.
    /// </summary>
    public const string ServiceName = "Honua";

    /// <summary>
    /// The version of the telemetry instrumentation.
    /// </summary>
    public const string ServiceVersion = "1.0.0";

    /// <summary>
    /// ActivitySource for creating distributed trace spans.
    /// Used to instrument application-level operations like queries, edits, and imports.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(ServiceName, ServiceVersion);

    /// <summary>
    /// Meter for custom application metrics.
    /// Used to record counters, histograms, and gauges for Honua-specific operations.
    /// </summary>
    public static readonly Meter Meter = new(ServiceName, ServiceVersion);

    /// <summary>
    /// Counter for GeoServices/ArcGIS REST error envelopes produced by the server.
    /// GeoServices returns most errors as an <c>{"error":{code,message,details}}</c>
    /// body (often with an HTTP 2xx status), so without this counter the headline
    /// compat surface's error rate is invisible to aggregatable metrics. Tags:
    /// <c>service_type</c>, <c>operation</c>, <c>error_code</c>.
    /// </summary>
    public static readonly Counter<long> GeoServicesErrors = Meter.CreateCounter<long>(
        "honua_geoservices_error_total",
        "errors",
        "Total GeoServices/ArcGIS REST error envelopes produced, tagged by service_type, operation, and error_code.");

    /// <summary>
    /// Generic counter for protocol error envelopes produced across every surface
    /// (GeoServices, OGC, WFS, OData, Admin, generic Problem Details). The
    /// <c>in_band</c> tag is <c>true</c> when the error is delivered with a
    /// non-error HTTP status (a 2xx/3xx body envelope) that is invisible to
    /// load-balancer 5xx metrics and SLO gates. Tags: <c>service_type</c>,
    /// <c>operation</c>, <c>error_code</c>, <c>in_band</c>.
    /// </summary>
    public static readonly Counter<long> RequestErrors = Meter.CreateCounter<long>(
        "honua_request_error_total",
        "errors",
        "Total protocol error envelopes produced across all surfaces, tagged by service_type, operation, error_code, and in_band.");

    /// <summary>
    /// Histogram of serving-plane request latency in milliseconds, tagged with the GIS-aware
    /// <c>protocol</c> and <c>operation</c> dimensions (plus a coarse <c>status_class</c>). The
    /// stock ASP.NET Core <c>http.server.request.duration</c> instrument keys on route/method/status
    /// which is route-shaped rather than protocol-shaped; this instrument lets dashboards compute
    /// serving p95/p99 sliced by the GeoServices/OGC/WFS/OData protocol family and its logical
    /// operation. Cardinality is bounded because both tags come from the fixed
    /// <see cref="Protocols"/> set and the request classifier's finite operation vocabulary.
    /// </summary>
    public static readonly Histogram<double> ServingRequestDuration = Meter.CreateHistogram<double>(
        "honua_serving_request_duration_ms",
        "ms",
        "Serving-plane request latency, tagged by protocol, operation, and status_class.");

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

    /// <summary>
    /// Configures whether sanitized exception details are recorded on spans.
    /// </summary>
    public static void ConfigureExceptionRecording(bool exportDetails, bool includeStackTraces, int maxDetailLength = DefaultMaxExceptionDetailLength)
    {
        _exportExceptionDetails = exportDetails;
        _includeExceptionStackTraces = includeStackTraces;
        _maxExceptionDetailLength = maxDetailLength > 0 ? maxDetailLength : DefaultMaxExceptionDetailLength;
    }

    /// <summary>
    /// Reconfigures the in-process serving-latency rolling-window aggregation. Invoked once at
    /// startup from bound configuration; replacing the aggregator discards prior samples.
    /// </summary>
    /// <param name="window">The rolling window length (falls back to the 5-minute default when non-positive).</param>
    /// <param name="samplesPerProtocol">The per-protocol reservoir capacity (falls back to the default when non-positive).</param>
    public static void ConfigureServingLatency(TimeSpan window, int samplesPerProtocol)
    {
        var effectiveWindow = window > TimeSpan.Zero ? window : TimeSpan.FromSeconds(DefaultServingLatencyWindowSeconds);
        var effectiveSamples = samplesPerProtocol > 0 ? samplesPerProtocol : DefaultServingLatencySamplesPerProtocol;
        _servingLatency = new ServingLatencyAggregator(effectiveWindow, effectiveSamples);
    }

    /// <summary>
    /// Returns a point-in-time snapshot of per-protocol serving latency (p50/p95/p99 plus
    /// request/error counts) over the configured rolling window. Consumed by the admin
    /// ops-health snapshot; the underlying histogram remains the source for OTLP/Prometheus.
    /// </summary>
    /// <returns>The current serving latency snapshot.</returns>
    public static ServingLatencySnapshot GetServingLatencySnapshot() => _servingLatency.GetSnapshot();

    /// <summary>
    /// Records telemetry for a single produced error envelope. This is the one
    /// funnel every error-envelope construction path must call so the platform can
    /// aggregate and alert on its own compat error rate (#2243). It increments
    /// <see cref="RequestErrors"/> for every protocol, <see cref="GeoServicesErrors"/>
    /// additionally for GeoServices surfaces, and the Core
    /// <c>PerformanceMetrics.ApplicationErrors</c> counter so protocol error paths
    /// are no longer silent. AOT-safe: no reflection, statically-shaped tag lists.
    /// </summary>
    /// <param name="serviceType">The protocol/service surface (e.g. FeatureServer, OGC, OData).</param>
    /// <param name="operation">The logical operation (e.g. query, addfeatures) or "unknown".</param>
    /// <param name="errorCode">The error code carried in the envelope (HTTP status or GeoServices code).</param>
    /// <param name="isGeoServices">When true, also increments the GeoServices-specific counter.</param>
    public static void RecordErrorEnvelope(string serviceType, string operation, int errorCode, bool isGeoServices)
    {
        serviceType = string.IsNullOrWhiteSpace(serviceType) ? "unknown" : serviceType;
        operation = string.IsNullOrWhiteSpace(operation) ? "unknown" : operation;

        // An error delivered with a non-error HTTP status (2xx/3xx) is "in band":
        // the body carries an {error} envelope but the transport status reads
        // success, so load-balancer/SLO 5xx metrics never see it. This is the
        // keystone signal the audit is blind to today.
        var inBand = errorCode < 400;

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

    /// <summary>
    /// Records a serving-plane request against <see cref="ServingRequestDuration"/> with the
    /// GIS-aware <c>protocol</c> and <c>operation</c> dimensions. Callers pass the classified
    /// protocol/operation (from the shared request classifier) so the metric stays low-cardinality;
    /// requests that do not resolve to a Honua protocol are skipped by the caller. AOT-safe: no
    /// reflection, statically-shaped tag list.
    /// </summary>
    /// <param name="protocol">The GIS protocol family from <see cref="Protocols"/>.</param>
    /// <param name="operation">The logical operation, or "unknown".</param>
    /// <param name="statusCode">The HTTP status code of the completed response.</param>
    /// <param name="durationMs">The elapsed request duration in milliseconds.</param>
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

    /// <summary>
    /// Well-known activity names for consistent tracing.
    /// </summary>
    public static class Activities
    {
        /// <summary>HTTP request processing activity.</summary>
        public const string HttpRequest = "honua.http.request";

        /// <summary>Database query execution activity.</summary>
        public const string DatabaseQuery = "honua.db.query";

        /// <summary>Database connection acquisition activity.</summary>
        public const string DatabaseConnection = "honua.db.connection";

        /// <summary>Feature query operation activity.</summary>
        public const string FeatureQuery = "honua.feature.query";

        /// <summary>Feature edit operation activity.</summary>
        public const string FeatureEdit = "honua.feature.edit";

        /// <summary>Tile generation activity.</summary>
        public const string TileGeneration = "honua.tile.generate";

        /// <summary>Map image rendering activity shared by map protocols.</summary>
        public const string MapRender = "honua.map.render";

        /// <summary>Feature identify operation activity shared by map protocols.</summary>
        public const string FeatureIdentify = "honua.feature.identify";

        /// <summary>File import processing activity.</summary>
        public const string FileImport = "honua.import.file";

        /// <summary>Authentication and authorization activity.</summary>
        public const string Authentication = "honua.auth.validate";

        /// <summary>Cache operation activity.</summary>
        public const string CacheOperation = "honua.cache.operation";

        /// <summary>MapServer export (image rendering) activity.</summary>
        public const string MapServerExport = "honua.mapserver.export";

        /// <summary>MapServer identify (spatial query) activity.</summary>
        public const string MapServerIdentify = "honua.mapserver.identify";

        /// <summary>Static map image rendering activity.</summary>
        public const string StaticMapRender = "honua.staticmap.render";

        /// <summary>Business intelligence calculation activity.</summary>
        public const string BusinessIntelligence = "honua.bi.calculation";

        /// <summary>Security monitoring activity.</summary>
        public const string SecurityMonitoring = "honua.security.check";

        /// <summary>Performance analysis activity.</summary>
        public const string PerformanceAnalysis = "honua.performance.analysis";

        /// <summary>Anomaly detection activity.</summary>
        public const string AnomalyDetection = "honua.ml.anomaly_detection";

        /// <summary>Operator execution admission evaluation activity.</summary>
        public const string ExecutionAdmission = "honua.execution.admission";

        /// <summary>Geoprocessing job dispatch and execution activity.</summary>
        public const string GeoprocessingDispatch = "honua.geoprocessing.dispatch";

        /// <summary>Share export trigger activity.</summary>
        public const string ShareExportTrigger = "honua.share.export.trigger";
    }

    /// <summary>
    /// Well-known tag names for consistent span attributes.
    /// </summary>
    public static class Tags
    {
        /// <summary>The API protocol being used (FeatureServer, OGC, OData).</summary>
        public const string Protocol = "honua.protocol";

        /// <summary>The service identifier.</summary>
        public const string ServiceId = "honua.service.id";

        /// <summary>The layer identifier.</summary>
        public const string LayerId = "honua.layer.id";

        /// <summary>The collection identifier (OGC APIs).</summary>
        public const string CollectionId = "honua.collection.id";

        /// <summary>The GP task or named operation identifier.</summary>
        public const string TaskName = "honua.task.name";

        /// <summary>The durable job identifier.</summary>
        public const string JobId = "honua.job.id";

        /// <summary>The scheduled Share export definition identifier.</summary>
        public const string ShareExportId = "honua.share.export.id";

        /// <summary>The scheduled Share export run identifier.</summary>
        public const string ShareRunId = "honua.share.run.id";

        /// <summary>The scheduled Share export destination type.</summary>
        public const string ShareDestinationType = "honua.share.destination_type";

        /// <summary>The named parameter or result identifier.</summary>
        public const string ParameterName = "honua.parameter.name";

        /// <summary>The operation type (query, edit, delete, etc.).</summary>
        public const string Operation = "honua.operation";

        /// <summary>Number of features affected or returned.</summary>
        public const string FeatureCount = "honua.feature.count";

        /// <summary>The database query type.</summary>
        public const string DbQueryType = "db.query.type";

        /// <summary>The correlation ID for request tracing.</summary>
        public const string CorrelationId = "honua.correlation.id";

        /// <summary>Whether the operation resulted in an error.</summary>
        public const string Error = "error";

        /// <summary>The error message if an error occurred.</summary>
        public const string ErrorMessage = "error.message";

        /// <summary>The tile zoom level.</summary>
        public const string TileZ = "honua.tile.z";

        /// <summary>The tile X coordinate.</summary>
        public const string TileX = "honua.tile.x";

        /// <summary>The tile Y coordinate.</summary>
        public const string TileY = "honua.tile.y";

        /// <summary>User identifier for business intelligence.</summary>
        public const string UserId = "honua.user.id";

        /// <summary>Client identifier for analytics.</summary>
        public const string ClientId = "honua.client.id";

        /// <summary>Geographic region for geo-analytics.</summary>
        public const string GeoRegion = "honua.geo.region";

        /// <summary>Request latency category (fast, medium, slow, timeout).</summary>
        public const string LatencyCategory = "honua.latency.category";

        /// <summary>Security risk level (low, medium, high, critical).</summary>
        public const string SecurityRisk = "honua.security.risk_level";

        /// <summary>Business metric type for analytics.</summary>
        public const string BusinessMetric = "honua.business.metric_type";

        /// <summary>Performance baseline comparison.</summary>
        public const string PerformanceBaseline = "honua.performance.baseline_pct";

        /// <summary>Cache tier (L1, L2, L3) for multilevel caching.</summary>
        public const string CacheTier = "honua.cache.tier";

        /// <summary>SQL query complexity score.</summary>
        public const string QueryComplexity = "honua.query.complexity_score";

        /// <summary>Memory allocation size category.</summary>
        public const string MemoryCategory = "honua.memory.allocation_category";
    }

    /// <summary>
    /// Well-known protocol names for tagging.
    /// </summary>
    public static class Protocols
    {
        /// <summary>gRPC API.</summary>
        public const string Grpc = "Grpc";

        /// <summary>GeoServices FeatureServer REST API.</summary>
        public const string FeatureServer = "FeatureServer";

        /// <summary>OGC API Features.</summary>
        public const string OgcFeatures = "OGC-Features";

        /// <summary>OGC API Tiles.</summary>
        public const string OgcTiles = "OGC-Tiles";

        /// <summary>Durable PMTiles range-proxy surface.</summary>
        public const string PMTiles = "PMTiles";

        /// <summary>Terrain-RGB elevation tile surface.</summary>
        public const string Terrain = "Terrain";

        /// <summary>Elevation query and profile surface.</summary>
        public const string Elevation = "Elevation";

        /// <summary>OGC API Maps.</summary>
        public const string OgcMaps = "OGC-Maps";

        /// <summary>OGC API Coverages.</summary>
        public const string OgcCoverages = "OGC-Coverages";

        /// <summary>OGC API Processes.</summary>
        public const string OgcProcesses = "OGC-Processes";

        /// <summary>OGC API Records.</summary>
        public const string OgcRecords = "OGC-Records";

        /// <summary>GeoServices MapServer REST API.</summary>
        public const string MapServer = "MapServer";

        /// <summary>GeoServices Image Server REST API.</summary>
        public const string ImageServer = "ImageServer";

        /// <summary>GeoServices VectorTileServer REST API.</summary>
        public const string VectorTileServer = "VectorTileServer";

        /// <summary>OData v4 protocol.</summary>
        public const string OData = "OData";

        /// <summary>File import protocol.</summary>
        public const string Import = "Import";

        /// <summary>Admin API.</summary>
        public const string Admin = "Admin";

        /// <summary>Health check endpoints.</summary>
        public const string Health = "Health";

        /// <summary>Monitoring and metrics endpoints.</summary>
        public const string Monitoring = "Monitoring";

        /// <summary>Business intelligence endpoints.</summary>
        public const string BusinessIntelligence = "BI";

        /// <summary>Geometry service operations (buffer, simplify, project).</summary>
        public const string GeometryService = "GeometryService";

        /// <summary>Static map image API.</summary>
        public const string StaticMap = "StaticMap";

        /// <summary>GeoServices Print / Export Web Map Task.</summary>
        public const string PrintingTools = "PrintingTools";

        /// <summary>OGC Web Feature Service 2.0.</summary>
        public const string Wfs20 = "WFS-2.0";

        /// <summary>OGC Web Coverage Service 2.0.1.</summary>
        public const string Wcs20 = "WCS-2.0.1";

        /// <summary>Real-time feature-change streaming (WebSocket/SSE).</summary>
        public const string Streaming = "Streaming";

        /// <summary>GeoServices GPServer REST API.</summary>
        public const string GPServer = "GPServer";

        /// <summary>GeoServices NAServer REST API.</summary>
        public const string NAServer = "NAServer";

        /// <summary>SpatioTemporal Asset Catalog API.</summary>
        public const string Stac = "STAC";

        /// <summary>OGC SensorThings API (STA v1.1).</summary>
        public const string SensorThings = "SensorThings";

        /// <summary>Model Context Protocol operator surface.</summary>
        public const string Mcp = "Mcp";
    }

    /// <summary>
    /// Starts a new activity with the specified name and kind.
    /// </summary>
    /// <param name="name">The activity name.</param>
    /// <param name="kind">The activity kind (default: Internal).</param>
    /// <returns>The started activity, or null if not sampled.</returns>
    public static Activity? StartActivity(string name, ActivityKind kind = ActivityKind.Internal)
    {
        return ActivitySource.StartActivity(name, kind);
    }

    /// <summary>
    /// Starts a new activity for a database operation.
    /// </summary>
    /// <param name="queryType">The type of database query.</param>
    /// <param name="layerId">The layer identifier (optional).</param>
    /// <param name="complexityScore">Query complexity score (optional).</param>
    /// <returns>The started activity, or null if not sampled.</returns>
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

    /// <summary>
    /// Starts a new activity for a feature operation.
    /// </summary>
    /// <param name="operation">The operation type (query, edit, etc.).</param>
    /// <param name="protocol">The API protocol being used.</param>
    /// <param name="layerId">The layer identifier.</param>
    /// <param name="correlationId">The request correlation ID (optional).</param>
    /// <returns>The started activity, or null if not sampled.</returns>
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

    /// <summary>
    /// Starts a new business intelligence activity for analytics tracking.
    /// </summary>
    /// <param name="metricType">The type of business metric being calculated.</param>
    /// <param name="userId">User identifier (optional).</param>
    /// <param name="clientId">Client identifier (optional).</param>
    /// <returns>The started activity, or null if not sampled.</returns>
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

    /// <summary>
    /// Starts a new security monitoring activity.
    /// </summary>
    /// <param name="checkType">The type of security check being performed.</param>
    /// <param name="riskLevel">The assessed risk level.</param>
    /// <returns>The started activity, or null if not sampled.</returns>
    public static Activity? StartSecurityActivity(string checkType, string riskLevel = "low")
    {
        var activity = ActivitySource.StartActivity(Activities.SecurityMonitoring, ActivityKind.Internal);
        activity?.SetTag(Tags.Operation, checkType);
        activity?.SetTag(Tags.SecurityRisk, riskLevel);

        return activity;
    }

    /// <summary>
    /// Records an error on the current activity.
    /// </summary>
    /// <param name="activity">The activity to record the error on.</param>
    /// <param name="exception">The exception that occurred.</param>
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

    /// <summary>
    /// Sets the success status on the activity with the feature count.
    /// </summary>
    /// <param name="activity">The activity to update.</param>
    /// <param name="featureCount">The number of features processed.</param>
    /// <param name="performanceBaseline">Performance compared to baseline (percentage).</param>
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

    /// <summary>
    /// Adds latency categorization to an activity based on duration.
    /// </summary>
    /// <param name="activity">The activity to categorize.</param>
    /// <param name="durationMs">The duration in milliseconds.</param>
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

    /// <summary>
    /// Adds memory allocation categorization to an activity.
    /// </summary>
    /// <param name="activity">The activity to categorize.</param>
    /// <param name="allocationBytes">The memory allocation in bytes.</param>
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

/// <summary>
/// Encapsulates the lifecycle of a telemetry feature activity.
/// </summary>
public sealed class HonuaTelemetryScope : IDisposable
{
    private readonly Activity? _activity;
    private bool _disposed;

    private HonuaTelemetryScope(Activity? activity)
    {
        _activity = activity;
    }

    /// <summary>
    /// Starts a scope for a feature operation.
    /// </summary>
    /// <param name="operation">Operation name (query, edit, export, etc.).</param>
    /// <param name="protocol">Protocol name from <see cref="HonuaTelemetry.Protocols"/>.</param>
    /// <param name="layerId">Layer identifier value.</param>
    /// <param name="correlationId">Optional correlation identifier.</param>
    /// <returns>A telemetry scope that should be disposed when operation completes.</returns>
    public static HonuaTelemetryScope StartFeature(
        string operation,
        string protocol,
        string layerId,
        string? correlationId = null)
    {
        var activity = HonuaTelemetry.StartFeatureActivity(operation, protocol, layerId, correlationId);
        return new HonuaTelemetryScope(activity);
    }

    /// <summary>
    /// Adds a tag to the underlying activity.
    /// </summary>
    /// <param name="key">Tag key.</param>
    /// <param name="value">Tag value.</param>
    /// <returns>The current scope instance.</returns>
    public HonuaTelemetryScope WithTag(string key, object? value)
    {
        _activity?.SetTag(key, value);
        return this;
    }

    /// <summary>
    /// Marks operation success with optional feature count.
    /// </summary>
    /// <param name="featureCount">Optional feature count.</param>
    public void SetSuccess(int featureCount = 0)
    {
        HonuaTelemetry.SetSuccess(_activity, featureCount);
    }

    /// <summary>
    /// Records an exception on the underlying activity.
    /// </summary>
    /// <param name="exception">Exception to record.</param>
    public void RecordException(Exception exception)
    {
        HonuaTelemetry.RecordException(_activity, exception);
    }

    /// <summary>
    /// Categorizes latency on the underlying activity.
    /// </summary>
    /// <param name="durationMs">Duration in milliseconds.</param>
    public void CategorizeLatency(double durationMs)
    {
        HonuaTelemetry.CategorizeLatency(_activity, durationMs);
    }

    /// <inheritdoc />
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
