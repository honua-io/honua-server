// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Configuration;

/// <summary>
/// Centralized limits configuration enforced consistently across all protocols.
/// Controls resource usage to prevent system overload and ensure predictable behavior.
/// </summary>
public sealed class LimitsOptions
{
    /// <summary>
    /// Configuration section name for binding from environment variables.
    /// Maps to HONUA__LIMITS__* environment variables per ADR-0008.
    /// </summary>
    public const string SectionName = "Limits";

    /// <summary>
    /// Query operation limits applied to all protocols (GeoServices REST, OGC API Features, OData).
    /// </summary>
    public QueryLimits Query { get; init; } = new();

    /// <summary>
    /// Geometry processing limits for input validation and output control.
    /// </summary>
    public GeometryLimits Geometry { get; init; } = new();

    /// <summary>
    /// Edit operation limits for applyEdits and CRUD operations.
    /// </summary>
    public EditLimits Edits { get; init; } = new();

    /// <summary>
    /// Attachment handling limits for file uploads and storage.
    /// </summary>
    public AttachmentLimits Attachments { get; init; } = new();

    /// <summary>
    /// Map tile generation and caching limits.
    /// </summary>
    public TileLimits Tiles { get; init; } = new();

    /// <summary>
    /// Database connection and concurrency limits.
    /// </summary>
    public ConnectionLimits Connections { get; init; } = new();
}

/// <summary>
/// Query operation limits applied consistently across all protocols.
/// Prevents resource exhaustion from unbounded requests.
/// </summary>
public sealed class QueryLimits
{
    /// <summary>
    /// Maximum number of features returned in a single query response.
    /// Applied before pagination. Range: 100-10,000.
    /// </summary>
    [Range(100, 10000, ErrorMessage = "MaxRecordCount must be between 100 and 10,000")]
    public int MaxRecordCount { get; init; } = 2000;

    /// <summary>
    /// Default number of features when not specified by client.
    /// Must be less than or equal to MaxRecordCount. Range: 100-MaxRecordCount.
    /// </summary>
    [Range(100, int.MaxValue, ErrorMessage = "DefaultRecordCount must be at least 100")]
    public int DefaultRecordCount { get; init; } = 1000;

    /// <summary>
    /// Maximum pagination offset to prevent deep pagination issues.
    /// Range: 1,000-1,000,000.
    /// </summary>
    [Range(1000, 1000000, ErrorMessage = "MaxOffset must be between 1,000 and 1,000,000")]
    public int MaxOffset { get; init; } = 100000;

    /// <summary>
    /// Maximum bounding box area in square kilometers.
    /// Prevents full-table scans on large datasets. Null disables limit.
    /// </summary>
    [Range(0.1, double.MaxValue, ErrorMessage = "MaxBboxAreaSqKm must be greater than 0.1")]
    public double? MaxBboxAreaSqKm { get; init; } = 1000;

    /// <summary>
    /// Maximum time allowed for a single query operation.
    /// Range: 5 seconds to 2 minutes.
    /// </summary>
    public TimeSpan QueryTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Geometry processing and validation limits.
/// Applied to both input geometries and output formatting.
/// </summary>
public sealed class GeometryLimits
{
    /// <summary>
    /// Maximum number of vertices allowed in a single input geometry.
    /// Prevents memory exhaustion from complex geometries. Range: 1,000-1,000,000.
    /// </summary>
    [Range(1000, 1000000, ErrorMessage = "MaxVerticesPerGeometry must be between 1,000 and 1,000,000")]
    public int MaxVerticesPerGeometry { get; init; } = 100000;

    /// <summary>
    /// Maximum size of serialized geometry in bytes (e.g., GeoJSON, WKT).
    /// Range: 1MB to 100MB.
    /// </summary>
    [Range(1048576, 104857600, ErrorMessage = "MaxGeometrySize must be between 1MB and 100MB")]
    public long MaxGeometrySize { get; init; } = 10485760; // 10MB

    /// <summary>
    /// Maximum number of decimal places for coordinate precision in output.
    /// Controls output size and precision. Range: 1-15 decimal places.
    /// </summary>
    [Range(1, 15, ErrorMessage = "MaxCoordinatePrecision must be between 1 and 15")]
    public int MaxCoordinatePrecision { get; init; } = 8;

    /// <summary>
    /// Auto-simplification tolerance for large geometries in meters.
    /// Null disables auto-simplification. Range: 0-1000 meters.
    /// </summary>
    [Range(0.0, 1000.0, ErrorMessage = "SimplifyTolerance must be between 0 and 1000 meters")]
    public double? SimplifyTolerance { get; init; } = null;
}

/// <summary>
/// Edit operation limits for CRUD operations and batch processing.
/// Applied to applyEdits, OGC Transactions, and OData modifications.
/// </summary>
public sealed class EditLimits
{
    /// <summary>
    /// Maximum number of features in a single edit operation (insert/update/delete).
    /// Range: 1-10,000.
    /// </summary>
    [Range(1, 10000, ErrorMessage = "MaxFeaturesPerEdit must be between 1 and 10,000")]
    public int MaxFeaturesPerEdit { get; init; } = 1000;

    /// <summary>
    /// Maximum total number of edit operations in a single transaction.
    /// Range: 100-50,000.
    /// </summary>
    [Range(100, 50000, ErrorMessage = "MaxEditsPerTransaction must be between 100 and 50,000")]
    public int MaxEditsPerTransaction { get; init; } = 5000;

    /// <summary>
    /// Maximum HTTP request body size for edit operations in bytes.
    /// Range: 1MB to 500MB.
    /// </summary>
    [Range(1048576, 524288000, ErrorMessage = "MaxPayloadSize must be between 1MB and 500MB")]
    public long MaxPayloadSize { get; init; } = 52428800; // 50MB
}

/// <summary>
/// File attachment limits for feature attachments and uploads.
/// Applied to all attachment operations across protocols.
/// </summary>
public sealed class AttachmentLimits
{
    /// <summary>
    /// Maximum size of a single attachment file in bytes.
    /// Range: 1MB to 100MB.
    /// </summary>
    [Range(1048576, 104857600, ErrorMessage = "MaxAttachmentSize must be between 1MB and 100MB")]
    public long MaxAttachmentSize { get; init; } = 10485760; // 10MB

    /// <summary>
    /// Maximum number of attachments allowed per feature.
    /// Range: 1-100.
    /// </summary>
    [Range(1, 100, ErrorMessage = "MaxAttachmentsPerFeature must be between 1 and 100")]
    public int MaxAttachmentsPerFeature { get; init; } = 10;

    /// <summary>
    /// Maximum total size of all attachments for a single feature in bytes.
    /// Range: 10MB to 1GB.
    /// </summary>
    [Range(10485760, 1073741824, ErrorMessage = "MaxTotalAttachmentSize must be between 10MB and 1GB")]
    public long MaxTotalAttachmentSize { get; init; } = 104857600; // 100MB

    /// <summary>
    /// Allowed MIME types for attachments as a comma-separated string.
    /// Default allows images and PDF files.
    /// </summary>
    public string AllowedMimeTypes { get; init; } = "image/*,application/pdf";
}

/// <summary>
/// Map tile generation and serving limits.
/// Applied to MVT (Mapbox Vector Tiles) and raster tile endpoints.
/// </summary>
public sealed class TileLimits
{
    /// <summary>
    /// Maximum zoom level for tile generation.
    /// Range: 1-24.
    /// </summary>
    [Range(1, 24, ErrorMessage = "MaxTileZoom must be between 1 and 24")]
    public int MaxTileZoom { get; init; } = 22;

    /// <summary>
    /// Minimum zoom level for tile generation.
    /// Range: 0-10.
    /// </summary>
    [Range(0, 10, ErrorMessage = "MinTileZoom must be between 0 and 10")]
    public int MinTileZoom { get; init; } = 0;

    /// <summary>
    /// Maximum number of features per tile before auto-simplification.
    /// Range: 1,000-1,000,000.
    /// </summary>
    [Range(1000, 1000000, ErrorMessage = "MaxFeaturesPerTile must be between 1,000 and 1,000,000")]
    public int MaxFeaturesPerTile { get; init; } = 100000;

    /// <summary>
    /// Maximum time allowed for tile generation.
    /// Range: 1 second to 1 minute.
    /// </summary>
    public TimeSpan TileTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Maximum compressed tile size in bytes.
    /// Range: 100KB to 5MB.
    /// </summary>
    [Range(102400, 5242880, ErrorMessage = "MaxTileSize must be between 100KB and 5MB")]
    public long MaxTileSize { get; init; } = 512000; // 500KB
}

/// <summary>
/// Database connection and concurrency limits.
/// Applied system-wide to prevent resource exhaustion.
/// </summary>
public sealed class ConnectionLimits
{
    /// <summary>
    /// Maximum number of concurrent query operations per instance.
    /// Range: 10-1,000.
    /// </summary>
    [Range(10, 1000, ErrorMessage = "MaxConcurrentQueries must be between 10 and 1,000")]
    public int MaxConcurrentQueries { get; init; } = 100;

    /// <summary>
    /// Maximum size of the database connection pool.
    /// Range: 10-500.
    /// </summary>
    [Range(10, 500, ErrorMessage = "MaxConnectionPoolSize must be between 10 and 500")]
    public int MaxConnectionPoolSize { get; init; } = 100;

    /// <summary>
    /// Overall timeout for HTTP requests including database operations.
    /// Range: 10 seconds to 10 minutes.
    /// </summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(120);
}
