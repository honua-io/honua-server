// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Diagnostics.Metrics;

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

        /// <summary>File import processing activity.</summary>
        public const string FileImport = "honua.import.file";
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
    }

    /// <summary>
    /// Well-known protocol names for tagging.
    /// </summary>
    public static class Protocols
    {
        /// <summary>Esri FeatureServer REST API.</summary>
        public const string FeatureServer = "FeatureServer";

        /// <summary>OGC API Features.</summary>
        public const string OgcFeatures = "OGC-Features";

        /// <summary>OData v4 protocol.</summary>
        public const string OData = "OData";

        /// <summary>File import protocol.</summary>
        public const string Import = "Import";

        /// <summary>Admin API.</summary>
        public const string Admin = "Admin";

        /// <summary>Health check endpoints.</summary>
        public const string Health = "Health";
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
    /// <returns>The started activity, or null if not sampled.</returns>
    public static Activity? StartDatabaseActivity(string queryType, string? layerId = null)
    {
        var activity = ActivitySource.StartActivity(Activities.DatabaseQuery, ActivityKind.Client);
        activity?.SetTag(Tags.DbQueryType, queryType);

        if (layerId != null)
        {
            activity?.SetTag(Tags.LayerId, layerId);
        }

        return activity;
    }

    /// <summary>
    /// Starts a new activity for a feature operation.
    /// </summary>
    /// <param name="operation">The operation type (query, edit, etc.).</param>
    /// <param name="protocol">The API protocol being used.</param>
    /// <param name="layerId">The layer identifier.</param>
    /// <returns>The started activity, or null if not sampled.</returns>
    public static Activity? StartFeatureActivity(string operation, string protocol, string layerId)
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

        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        activity.SetTag(Tags.Error, true);
        activity.SetTag(Tags.ErrorMessage, exception.Message);
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            { "exception.type", exception.GetType().FullName },
            { "exception.message", exception.Message },
            { "exception.stacktrace", exception.StackTrace }
        }));
    }

    /// <summary>
    /// Sets the success status on the activity with the feature count.
    /// </summary>
    /// <param name="activity">The activity to update.</param>
    /// <param name="featureCount">The number of features processed.</param>
    public static void SetSuccess(Activity? activity, int featureCount = 0)
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
    }
}
