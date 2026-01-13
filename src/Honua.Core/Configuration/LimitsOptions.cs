// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using Honua.Core.Features.Shared.Models;

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

    /// <summary>
    /// File import operation limits.
    /// </summary>
    public ImportLimits Imports { get; init; } = new();

    /// <summary>
    /// Geometry validation options for security and data quality enforcement.
    /// </summary>
    public GeometryValidationOptions Validation { get; init; } = new();
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
    [Range(100, 10000, ErrorMessage = ErrorMessages.RangeValidation.MaxRecordCount)]
    public int MaxRecordCount { get; init; } = 2000;

    /// <summary>
    /// Default number of features when not specified by client.
    /// Must be less than or equal to MaxRecordCount. Range: 100-MaxRecordCount.
    /// </summary>
    [Range(100, int.MaxValue, ErrorMessage = ErrorMessages.RangeValidation.DefaultRecordCount)]
    public int DefaultRecordCount { get; init; } = 1000;

    /// <summary>
    /// Maximum pagination offset to prevent deep pagination issues.
    /// Range: 1,000-1,000,000.
    /// </summary>
    [Range(1000, 1000000, ErrorMessage = ErrorMessages.RangeValidation.MaxOffset)]
    public int MaxOffset { get; init; } = 1000000;

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
    public TimeSpan QueryTimeout { get; init; } = TimeConstants.ThirtySecondsTimeSpan;
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
    [Range(1000, 1000000, ErrorMessage = ErrorMessages.RangeValidation.MaxVerticesPerGeometry)]
    public int MaxVerticesPerGeometry { get; init; } = 100000;

    /// <summary>
    /// Maximum size of serialized geometry in bytes (e.g., GeoJSON, WKT).
    /// Range: 1MB to 100MB.
    /// </summary>
    [Range(FileSizeConstants.OneMB, FileSizeConstants.OneHundredMB, ErrorMessage = ErrorMessages.RangeValidation.MaxGeometrySize)]
    public long MaxGeometrySize { get; init; } = FileSizeConstants.TenMB;

    /// <summary>
    /// Maximum number of decimal places for coordinate precision in output.
    /// Controls output size and precision. Range: 1-15 decimal places.
    /// </summary>
    [Range(1, 15, ErrorMessage = ErrorMessages.RangeValidation.MaxCoordinatePrecision)]
    public int MaxCoordinatePrecision { get; init; } = 8;

    /// <summary>
    /// Auto-simplification tolerance for large geometries in meters.
    /// Null disables auto-simplification. Range: 0-1000 meters.
    /// </summary>
    [Range(0.0, 1000.0, ErrorMessage = ErrorMessages.RangeValidation.SimplifyTolerance)]
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
    [Range(1, 10000, ErrorMessage = ErrorMessages.RangeValidation.MaxFeaturesPerEdit)]
    public int MaxFeaturesPerEdit { get; init; } = 1000;

    /// <summary>
    /// Maximum total number of edit operations in a single transaction.
    /// Range: 100-50,000.
    /// </summary>
    [Range(100, 50000, ErrorMessage = ErrorMessages.RangeValidation.MaxEditsPerTransaction)]
    public int MaxEditsPerTransaction { get; init; } = 5000;

    /// <summary>
    /// Maximum HTTP request body size for edit operations in bytes.
    /// Range: 1MB to 500MB.
    /// </summary>
    [Range(FileSizeConstants.OneMB, FileSizeConstants.FiveHundredMB, ErrorMessage = ErrorMessages.RangeValidation.MaxPayloadSize)]
    public long MaxPayloadSize { get; init; } = FileSizeConstants.FiftyMB;
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
    [Range(FileSizeConstants.OneMB, FileSizeConstants.OneHundredMB, ErrorMessage = ErrorMessages.RangeValidation.MaxAttachmentSize)]
    public long MaxAttachmentSize { get; init; } = FileSizeConstants.TenMB;

    /// <summary>
    /// Maximum number of attachments allowed per feature.
    /// Range: 1-100.
    /// </summary>
    [Range(1, 100, ErrorMessage = ErrorMessages.RangeValidation.MaxAttachmentsPerFeature)]
    public int MaxAttachmentsPerFeature { get; init; } = 10;

    /// <summary>
    /// Maximum total size of all attachments for a single feature in bytes.
    /// Range: 10MB to 1GB.
    /// </summary>
    [Range(FileSizeConstants.TenMB, FileSizeConstants.OneGB, ErrorMessage = ErrorMessages.RangeValidation.MaxTotalAttachmentSize)]
    public long MaxTotalAttachmentSize { get; init; } = FileSizeConstants.OneHundredMB;

    /// <summary>
    /// Allowed MIME types for attachments as a comma-separated string.
    /// Default allows images and PDF files.
    /// </summary>
    public string AllowedMimeTypes { get; init; } = "image/*,application/pdf,text/plain";
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
    [Range(1, 24, ErrorMessage = ErrorMessages.RangeValidation.MaxTileZoom)]
    public int MaxTileZoom { get; init; } = 22;

    /// <summary>
    /// Minimum zoom level for tile generation.
    /// Range: 0-10.
    /// </summary>
    [Range(0, 10, ErrorMessage = ErrorMessages.RangeValidation.MinTileZoom)]
    public int MinTileZoom { get; init; } = 0;

    /// <summary>
    /// Maximum number of features per tile before auto-simplification.
    /// Range: 1,000-1,000,000.
    /// </summary>
    [Range(1000, 1000000, ErrorMessage = ErrorMessages.RangeValidation.MaxFeaturesPerTile)]
    public int MaxFeaturesPerTile { get; init; } = 10000;

    /// <summary>
    /// Maximum time allowed for tile generation.
    /// Range: 1 second to 1 minute.
    /// </summary>
    public TimeSpan TileTimeout { get; init; } = TimeConstants.TenSecondsTimeSpan;

    /// <summary>
    /// Maximum compressed tile size in bytes.
    /// Range: 100KB to 5MB.
    /// </summary>
    [Range(FileSizeConstants.OneHundredKB, FileSizeConstants.FiveMB, ErrorMessage = ErrorMessages.RangeValidation.MaxTileSize)]
    public long MaxTileSize { get; init; } = FileSizeConstants.FiveHundredKB;
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
    [Range(10, 1000, ErrorMessage = ErrorMessages.RangeValidation.MaxConcurrentQueries)]
    public int MaxConcurrentQueries { get; init; } = 100;

    /// <summary>
    /// Maximum size of the database connection pool.
    /// Range: 10-500.
    /// </summary>
    [Range(10, 500, ErrorMessage = ErrorMessages.RangeValidation.MaxConnectionPoolSize)]
    public int MaxConnectionPoolSize { get; init; } = 100;

    /// <summary>
    /// Overall timeout for HTTP requests including database operations.
    /// Range: 10 seconds to 10 minutes.
    /// </summary>
    public TimeSpan RequestTimeout { get; init; } = TimeConstants.TwoMinutesTimeSpan;
}

/// <summary>
/// File import operation limits.
/// Applied to geospatial file import endpoints.
/// </summary>
public sealed class ImportLimits
{
    /// <summary>
    /// Maximum file size for preview operations in bytes.
    /// Range: 1MB to 50MB.
    /// </summary>
    [Range(FileSizeConstants.OneMB, FileSizeConstants.FiftyMB, ErrorMessage = ErrorMessages.RangeValidation.MaxPreviewSize)]
    public long MaxPreviewSize { get; init; } = FileSizeConstants.TenMB;

    /// <summary>
    /// Maximum file size for synchronous import operations in bytes.
    /// Files larger than this trigger background job processing.
    /// Range: 10MB to 500MB.
    /// </summary>
    [Range(FileSizeConstants.TenMB, FileSizeConstants.FiveHundredMB, ErrorMessage = ErrorMessages.RangeValidation.MaxSyncImportSize)]
    public long MaxSyncImportSize { get; init; } = FileSizeConstants.FiftyMB;

    /// <summary>
    /// Maximum file size for any import operation in bytes.
    /// Range: 50MB to 5GB.
    /// </summary>
    [Range(FileSizeConstants.FiftyMB, FileSizeConstants.FiveGB, ErrorMessage = ErrorMessages.RangeValidation.MaxImportSize)]
    public long MaxImportSize { get; init; } = FileSizeConstants.FiveHundredMB;

    /// <summary>
    /// Maximum number of features to return in a preview.
    /// Range: 10-1,000.
    /// </summary>
    [Range(10, 1000, ErrorMessage = ErrorMessages.RangeValidation.MaxPreviewFeatures)]
    public int MaxPreviewFeatures { get; init; } = 100;

    /// <summary>
    /// Batch size for feature insertion during import.
    /// Range: 100-10,000.
    /// </summary>
    [Range(100, 10000, ErrorMessage = ErrorMessages.RangeValidation.BatchSize)]
    public int BatchSize { get; init; } = 1000;
}

/// <summary>
/// Geometry validation options for security and data quality enforcement.
/// Controls three-layer validation: input format, WKB structure, and topology.
/// </summary>
public sealed class GeometryValidationOptions
{
    /// <summary>
    /// Validation strictness level for geometry processing.
    /// </summary>
    public ValidationMode Mode { get; init; } = ValidationMode.Repair;

    /// <summary>
    /// Maximum number of vertices allowed in a single geometry.
    /// Prevents DoS attacks from complex geometries. Range: 1,000-100,000.
    /// </summary>
    [Range(1000, 100000, ErrorMessage = ErrorMessages.RangeValidation.MaxVertices)]
    public int MaxVertices { get; init; } = 10000;

    /// <summary>
    /// Maximum number of rings allowed in a polygon geometry.
    /// Range: 10-1,000.
    /// </summary>
    [Range(10, 1000, ErrorMessage = ErrorMessages.RangeValidation.MaxRings)]
    public int MaxRings { get; init; } = 100;

    /// <summary>
    /// Maximum decimal places for coordinate precision.
    /// Controls output precision and storage efficiency. Range: 1-15.
    /// </summary>
    [Range(1, 15, ErrorMessage = ErrorMessages.RangeValidation.CoordinatePrecision)]
    public int CoordinatePrecision { get; init; } = 6;

    /// <summary>
    /// Tolerance used to treat polygon rings as closed (units depend on CRS).
    /// </summary>
    [Range(1e-12, 1, ErrorMessage = "Ring closure tolerance must be between 1e-12 and 1.")]
    public double RingClosureTolerance { get; init; } = 1e-6;

    /// <summary>
    /// Maximum WKB size in bytes.
    /// Prevents memory exhaustion from large geometries. Range: 100KB-10MB.
    /// </summary>
    [Range(FileSizeConstants.OneHundredKB, FileSizeConstants.TenMB, ErrorMessage = ErrorMessages.RangeValidation.MaxWkbSize)]
    public long MaxWkbSize { get; init; } = FileSizeConstants.OneMB;

    /// <summary>
    /// Timeout for geometry validation operations.
    /// Range: 1-30 seconds.
    /// </summary>
    public TimeSpan ValidationTimeout { get; init; } = TimeConstants.FiveSecondsTimeSpan;

    /// <summary>
    /// Whether to allow null geometries in features.
    /// When true, features can have null geometry (attribute-only features).
    /// </summary>
    public bool AllowNullGeometry { get; init; } = true;

    /// <summary>
    /// Whether to allow null attribute values.
    /// When true, attribute values can be null.
    /// </summary>
    public bool AllowNullAttributes { get; init; } = true;

    /// <summary>
    /// Maximum length of string attribute values.
    /// Prevents memory issues from very long strings. Range: 1,000-1,000,000.
    /// </summary>
    [Range(1000, 1000000, ErrorMessage = ErrorMessages.RangeValidation.MaxAttributeLength)]
    public int MaxAttributeLength { get; init; } = 100000;

    /// <summary>
    /// Whether to enable PostGIS topology validation using ST_IsValid().
    /// </summary>
    public bool EnableTopologyValidation { get; init; } = true;

    /// <summary>
    /// Whether to automatically repair invalid geometries using ST_MakeValid().
    /// Only applies when Mode is Repair.
    /// </summary>
    public bool EnableAutoRepair { get; init; } = true;
}

/// <summary>
/// Validation strictness mode for geometry processing.
/// </summary>
public enum ValidationMode
{
    /// <summary>
    /// Reject any invalid geometry with an error response.
    /// Most strict mode - no corrections applied.
    /// </summary>
    Strict,

    /// <summary>
    /// Attempt to repair invalid geometries automatically.
    /// Uses ST_MakeValid() for topology issues.
    /// Default mode balancing quality and usability.
    /// </summary>
    Repair,

    /// <summary>
    /// Accept geometries with minimal validation.
    /// Only basic structural validation is performed.
    /// Least strict mode for maximum compatibility.
    /// </summary>
    Accept
}
