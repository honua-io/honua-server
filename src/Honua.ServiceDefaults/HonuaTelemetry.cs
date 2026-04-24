// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;

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

    private const int DefaultMaxExceptionDetailLength = 256;

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

        /// <summary>OGC API Maps.</summary>
        public const string OgcMaps = "OGC-Maps";

        /// <summary>OGC API Processes.</summary>
        public const string OgcProcesses = "OGC-Processes";

        /// <summary>GeoServices MapServer REST API.</summary>
        public const string MapServer = "MapServer";

        /// <summary>GeoServices Image Server REST API.</summary>
        public const string ImageServer = "ImageServer";

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

        /// <summary>Real-time feature-change streaming (WebSocket/SSE).</summary>
        public const string Streaming = "Streaming";

        /// <summary>GeoServices GPServer REST API.</summary>
        public const string GPServer = "GPServer";

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

        var sanitizedMessage = SanitizeTelemetryText(exception.Message, _maxExceptionDetailLength);

        if (_exportExceptionDetails && !string.IsNullOrWhiteSpace(sanitizedMessage))
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

        if (_exportExceptionDetails && !string.IsNullOrWhiteSpace(sanitizedMessage))
        {
            tags.Add("exception.message", sanitizedMessage);
        }

        if (_exportExceptionDetails &&
            _includeExceptionStackTraces &&
            !string.IsNullOrWhiteSpace(exception.StackTrace))
        {
            tags.Add("exception.stacktrace", SanitizeTelemetryText(exception.StackTrace, Math.Max(_maxExceptionDetailLength, MinStackTraceDetailLength)));
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
