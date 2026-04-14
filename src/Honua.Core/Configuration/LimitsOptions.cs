using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Configuration;

/// <summary>
/// Configuration options for system limits and quotas.
/// </summary>
public class LimitsOptions
{
    public const string SectionName = "Limits";

    /// <summary>
    /// Maximum number of features that can be returned in a single query.
    /// </summary>
    [Range(1, 1000000)]
    public int MaxFeatures { get; set; } = 10000;

    /// <summary>
    /// Maximum query timeout in seconds.
    /// </summary>
    [Range(1, 600)]
    public int MaxQueryTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum concurrent requests per client.
    /// </summary>
    [Range(1, 1000)]
    public int MaxConcurrentRequests { get; set; } = 100;

    /// <summary>
    /// Maximum file size for uploads in bytes.
    /// </summary>
    [Range(1, long.MaxValue)]
    public long MaxUploadSizeBytes { get; set; } = 100 * 1024 * 1024; // 100MB

    /// <summary>
    /// Default page size for paginated responses.
    /// </summary>
    [Range(1, 10000)]
    public int DefaultPageSize { get; set; } = 100;

    /// <summary>
    /// Maximum page size for paginated responses.
    /// </summary>
    [Range(1, 10000)]
    public int MaxPageSize { get; set; } = 1000;

    /// <summary>
    /// Query limits for different types of operations.
    /// </summary>
    public QueryLimits Query { get; set; } = new();

    /// <summary>
    /// Geometry limits for spatial operations.
    /// </summary>
    public GeometryLimits Geometry { get; set; } = new();

    /// <summary>
    /// Geometry validation behavior and safety limits.
    /// </summary>
    public GeometryValidationOptions Validation { get; set; } = new();

    /// <summary>
    /// Edit operation limits.
    /// </summary>
    public EditLimits Edits { get; set; } = new();

    /// <summary>
    /// Attachment operation limits.
    /// </summary>
    public AttachmentLimits Attachments { get; set; } = new();

    /// <summary>
    /// Tile operation limits.
    /// </summary>
    public TileLimits Tiles { get; set; } = new();

    /// <summary>
    /// Database connection limits.
    /// </summary>
    public ConnectionLimits Connections { get; set; } = new();

    /// <summary>
    /// File import limits.
    /// </summary>
    public ImportLimits Imports { get; set; } = new();

    /// <summary>
    /// Spatial analytics limits.
    /// </summary>
    public AnalyticsLimits Analytics { get; set; } = new();
}

/// <summary>
/// Limits specific to query operations.
/// </summary>
public class QueryLimits
{
    /// <summary>
    /// Maximum complexity score for queries.
    /// </summary>
    [Range(1, 10000)]
    public int MaxComplexity { get; set; } = 1000;

    /// <summary>
    /// Maximum number of spatial operations per query.
    /// </summary>
    [Range(1, 100)]
    public int MaxSpatialOperations { get; set; } = 10;

    /// <summary>
    /// Maximum number of joins per query.
    /// </summary>
    [Range(1, 20)]
    public int MaxJoins { get; set; } = 5;

    /// <summary>
    /// Maximum nesting depth for filters.
    /// </summary>
    [Range(1, 50)]
    public int MaxFilterDepth { get; set; } = 10;

    /// <summary>
    /// Maximum number of filter conditions.
    /// </summary>
    [Range(1, 1000)]
    public int MaxFilterConditions { get; set; } = 100;

    /// <summary>
    /// Maximum offset for pagination.
    /// </summary>
    [Range(0, 1000000)]
    public int MaxOffset { get; set; } = 1000000;

    /// <summary>
    /// Maximum record count per query.
    /// </summary>
    [Range(100, 10000)]
    public int MaxRecordCount { get; set; } = 10000;

    /// <summary>
    /// Default record count for queries when not specified.
    /// </summary>
    [Range(100, 10000)]
    public int DefaultRecordCount { get; set; } = 1000;

    /// <summary>
    /// Maximum area for bounding box queries in square kilometers.
    /// </summary>
    [Range(0.1, 100000000)]
    public double MaxBboxAreaSqKm { get; set; } = 100000;

    /// <summary>
    /// Maximum H3 cells returned by H3 aggregation queries.
    /// </summary>
    [Range(100, 1000000)]
    public int MaxH3CellsPerQuery { get; set; } = 100000;

    /// <summary>
    /// Query timeout duration.
    /// </summary>
    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Limits specific to geometry operations.
/// </summary>
public class GeometryLimits
{
    /// <summary>
    /// Maximum vertices per geometry.
    /// </summary>
    [Range(1, 100000)]
    public int MaxVerticesPerGeometry { get; set; } = 50000;

    /// <summary>
    /// Maximum geometry size in bytes.
    /// </summary>
    [Range(1, long.MaxValue)]
    public long MaxGeometrySize { get; set; } = 5242880; // 5MB

    /// <summary>
    /// Maximum coordinate precision.
    /// </summary>
    [Range(1, 15)]
    public int MaxCoordinatePrecision { get; set; } = 8;

    /// <summary>
    /// Simplification tolerance.
    /// </summary>
    public double? SimplifyTolerance { get; set; }
}

/// <summary>
/// Limits specific to edit operations.
/// </summary>
public class EditLimits
{
    /// <summary>
    /// Maximum features per edit operation.
    /// </summary>
    [Range(1, 10000)]
    public int MaxFeaturesPerEdit { get; set; } = 500;

    /// <summary>
    /// Maximum edits per transaction.
    /// </summary>
    [Range(1, 10000)]
    public int MaxEditsPerTransaction { get; set; } = 2500;

    /// <summary>
    /// Maximum payload size for edit operations in bytes.
    /// </summary>
    [Range(1, long.MaxValue)]
    public long MaxPayloadSize { get; set; } = 26214400; // 25MB
}

/// <summary>
/// Limits specific to attachment operations.
/// </summary>
public class AttachmentLimits
{
    /// <summary>
    /// Maximum attachment size in bytes.
    /// </summary>
    [Range(1, long.MaxValue)]
    public long MaxAttachmentSize { get; set; } = 5242880; // 5MB

    /// <summary>
    /// Maximum attachments per feature.
    /// </summary>
    [Range(1, 100)]
    public int MaxAttachmentsPerFeature { get; set; } = 5;

    /// <summary>
    /// Maximum total attachment size per feature in bytes.
    /// </summary>
    [Range(1, long.MaxValue)]
    public long MaxTotalAttachmentSize { get; set; } = 52428800; // 50MB

    /// <summary>
    /// Allowed MIME types for attachments.
    /// </summary>
    public string AllowedMimeTypes { get; set; } = "image/*,application/pdf";
}


/// <summary>
/// Limits specific to database connections.
/// </summary>
public class ConnectionLimits
{
    /// <summary>
    /// Maximum concurrent queries.
    /// </summary>
    [Range(1, 1000)]
    public int MaxConcurrentQueries { get; set; } = 200;

    /// <summary>
    /// Maximum connection pool size.
    /// </summary>
    [Range(1, 1000)]
    public int MaxConnectionPoolSize { get; set; } = 200;

    /// <summary>
    /// Minimum connection pool size.
    /// </summary>
    [Range(0, 100)]
    public int MinConnectionPoolSize { get; set; } = 20;

    /// <summary>
    /// Connection idle lifetime in seconds.
    /// </summary>
    [Range(1, 3600)]
    public int ConnectionIdleLifetimeSeconds { get; set; } = 600;

    /// <summary>
    /// Connection pruning interval in seconds.
    /// </summary>
    [Range(1, 3600)]
    public int ConnectionPruningIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// Command timeout in seconds.
    /// </summary>
    [Range(1, 300)]
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Buffer size in bytes.
    /// </summary>
    [Range(1024, 1048576)]
    public int BufferSizeBytes { get; set; } = 32768;

    /// <summary>
    /// Request timeout duration.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Connection multiplexing mode.
    /// </summary>
    public string Multiplexing { get; set; } = "false";

    /// <summary>
    /// Connection acquisition timeout in seconds.
    /// </summary>
    [Range(1, 300)]
    public int ConnectionAcquisitionTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Lock timeout.
    /// </summary>
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Statement timeout.
    /// </summary>
    public TimeSpan StatementTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Idle in transaction timeout.
    /// </summary>
    public TimeSpan IdleInTransactionTimeout { get; set; } = TimeSpan.FromSeconds(60);
}
