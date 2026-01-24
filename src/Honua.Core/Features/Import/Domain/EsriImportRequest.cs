// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Request to discover services and layers from an ArcGIS Server URL.
/// </summary>
public sealed record EsriDiscoveryRequest
{
    /// <summary>
    /// The base URL of the ArcGIS Server service.
    /// Examples:
    /// - https://services.arcgis.com/xxx/arcgis/rest/services/MyService/FeatureServer
    /// - https://server.example.com/arcgis/rest/services/MyFolder/MyService/MapServer
    /// </summary>
    public required string ServiceUrl { get; init; }

    /// <summary>
    /// Optional timeout for the discovery request in seconds.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 30;
}

/// <summary>
/// Request to import a layer from an ArcGIS Server into PostGIS.
/// </summary>
public sealed record EsriImportRequest
{
    /// <summary>
    /// Optional job identifier for progress tracking.
    /// </summary>
    public string? JobId { get; init; }

    /// <summary>
    /// The base URL of the ArcGIS Server service.
    /// </summary>
    public required string ServiceUrl { get; init; }

    /// <summary>
    /// The ID of the layer to import.
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// Target table name in PostgreSQL.
    /// </summary>
    public required string TableName { get; init; }

    /// <summary>
    /// Target coordinate reference system ID (for transformation).
    /// Default is 4326 (WGS84).
    /// </summary>
    public int TargetSrid { get; init; } = 4326;

    /// <summary>
    /// Whether to overwrite an existing table.
    /// </summary>
    public bool OverwriteExisting { get; init; }

    /// <summary>
    /// Optional WHERE clause to filter features during import.
    /// Uses Esri SQL syntax.
    /// </summary>
    public string? WhereClause { get; init; }

    /// <summary>
    /// Optional output fields to import (null imports all fields).
    /// </summary>
    public string[]? OutputFields { get; init; }

    /// <summary>
    /// Batch size for paginated feature retrieval.
    /// Default is determined by service's maxRecordCount.
    /// </summary>
    public int? BatchSize { get; init; }

    /// <summary>
    /// Request timeout in seconds for each batch request.
    /// </summary>
    public int RequestTimeoutSeconds { get; init; } = 120;

    /// <summary>
    /// Maximum number of retry attempts for failed requests.
    /// </summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// Whether to automatically publish the imported layer.
    /// </summary>
    public bool AutoPublish { get; init; } = true;
}

/// <summary>
/// Result of an Esri import operation.
/// </summary>
public sealed record EsriImportResult
{
    /// <summary>
    /// Whether the import was successful.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Number of features imported.
    /// </summary>
    public int FeatureCount { get; init; }

    /// <summary>
    /// Number of features that failed to import.
    /// </summary>
    public int FailedFeatures { get; init; }

    /// <summary>
    /// Table name created/updated.
    /// </summary>
    public required string TableName { get; init; }

    /// <summary>
    /// The ID of the published layer (if auto-publish was enabled).
    /// </summary>
    public int? PublishedLayerId { get; init; }

    /// <summary>
    /// The service URL that was imported from.
    /// </summary>
    public required string SourceServiceUrl { get; init; }

    /// <summary>
    /// The layer ID that was imported.
    /// </summary>
    public int SourceLayerId { get; init; }

    /// <summary>
    /// Error message if import failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Import duration.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Warnings encountered during import.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Create successful import result.
    /// </summary>
    public static EsriImportResult CreateSuccess(
        string tableName,
        string sourceServiceUrl,
        int sourceLayerId,
        int featureCount,
        int failedFeatures = 0,
        int? publishedLayerId = null,
        TimeSpan duration = default,
        IReadOnlyList<string>? warnings = null) =>
        new()
        {
            Success = true,
            TableName = tableName,
            SourceServiceUrl = sourceServiceUrl,
            SourceLayerId = sourceLayerId,
            FeatureCount = featureCount,
            FailedFeatures = failedFeatures,
            PublishedLayerId = publishedLayerId,
            Duration = duration,
            Warnings = warnings ?? []
        };

    /// <summary>
    /// Create failed import result.
    /// </summary>
    public static EsriImportResult CreateFailure(
        string tableName,
        string sourceServiceUrl,
        int sourceLayerId,
        string errorMessage,
        TimeSpan duration = default) =>
        new()
        {
            Success = false,
            TableName = tableName,
            SourceServiceUrl = sourceServiceUrl,
            SourceLayerId = sourceLayerId,
            ErrorMessage = errorMessage,
            Duration = duration
        };
}
