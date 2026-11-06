using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Configuration;

public class LimitsOptions
{
    public const string SectionName = "Limits";

    [Range(1, 1000000)]
    public int MaxFeatures { get; set; } = 10000;

    [Range(1, 600)]
    public int MaxQueryTimeoutSeconds { get; set; } = 60;

    [Range(1, 1000)]
    public int MaxConcurrentRequests { get; set; } = 100;

    [Range(1, long.MaxValue)]
    public long MaxUploadSizeBytes { get; set; } = 100 * 1024 * 1024; // 100MB

    [Range(1, 10000)]
    public int DefaultPageSize { get; set; } = 100;

    [Range(1, 10000)]
    public int MaxPageSize { get; set; } = 1000;

    public QueryLimits Query { get; set; } = new();

    public GeoParquetLimits GeoParquet { get; set; } = new();

    public GeometryLimits Geometry { get; set; } = new();

    public GeometryValidationOptions Validation { get; set; } = new();

    public EditLimits Edits { get; set; } = new();

    public AttachmentLimits Attachments { get; set; } = new();

    public TileLimits Tiles { get; set; } = new();

    public ConnectionLimits Connections { get; set; } = new();

    public ImportSizeLimits Imports { get; set; } = new();

    public AnalyticsLimits Analytics { get; set; } = new();

    public ElevationLimits Elevation { get; set; } = new();

    public ReplicaLimits Replica { get; set; } = new();
}

public class ReplicaLimits
{
    // A backlog larger than this limit is delivered across change-generation windows, and a single committed generation is never split.
    [Range(100, 1000000)]
    public int MaxChangesPerLayer { get; set; } = 50000;
}

public class ElevationLimits
{
    [Range(2, 2000)]
    public int DefaultSampleCount { get; set; } = 100;

    [Range(2, 2000)]
    public int MaxSampleCount { get; set; } = 500;

    [Range(1, 500000)]
    public double MaxIntervalMeters { get; set; } = 50000;

    [Range(0.01, 1000000)]
    public double MinIntervalMeters { get; set; } = 1.0;
}

public class QueryLimits
{
    [Range(1, 10000)]
    public int MaxComplexity { get; set; } = 1000;

    [Range(1, 100)]
    public int MaxSpatialOperations { get; set; } = 10;

    [Range(1, 20)]
    public int MaxJoins { get; set; } = 5;

    [Range(1, 50)]
    public int MaxFilterDepth { get; set; } = 10;

    [Range(1, 1000)]
    public int MaxFilterConditions { get; set; } = 100;

    [Range(0, 1000000)]
    public int MaxOffset { get; set; } = 1000000;

    [Range(100, 10000)]
    public int MaxRecordCount { get; set; } = 10000;

    [Range(100, 10000)]
    public int DefaultRecordCount { get; set; } = 1000;

    [Range(0.1, 100000000)]
    public double MaxBboxAreaSqKm { get; set; } = 100000;

    [Range(100, 1000000)]
    public int MaxH3CellsPerQuery { get; set; } = 100000;

    public const int DefaultMaxMapLayersPerRequest = 50;

    [Range(1, 1000)]
    public int MaxMapLayersPerRequest { get; set; } = DefaultMaxMapLayersPerRequest;

    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

public class GeometryLimits
{
    [Range(1, 100000)]
    public int MaxVerticesPerGeometry { get; set; } = 50000;

    [Range(1, long.MaxValue)]
    public long MaxGeometrySize { get; set; } = 5242880; // 5MB

    [Range(1, 15)]
    public int MaxCoordinatePrecision { get; set; } = 8;

    public double? SimplifyTolerance { get; set; }
}

public class EditLimits
{
    [Range(1, 10000)]
    public int MaxFeaturesPerEdit { get; set; } = 500;

    [Range(1, 10000)]
    public int MaxEditsPerTransaction { get; set; } = 2500;

    [Range(1, long.MaxValue)]
    public long MaxPayloadSize { get; set; } = 26214400; // 25MB
}

public class AttachmentLimits
{
    [Range(1, long.MaxValue)]
    public long MaxAttachmentSize { get; set; } = 5242880; // 5MB

    [Range(1, 100)]
    public int MaxAttachmentsPerFeature { get; set; } = 5;

    [Range(1, long.MaxValue)]
    public long MaxTotalAttachmentSize { get; set; } = 52428800; // 50MB

    // honua-server#5017: text notes, CSV tallies and JSON payloads are ordinary field attachments.
    // FileUploadSecurity still scans .txt/.csv/.json uploads for script content after this check;
    // that scan is keyed on the file extension, not on the declared MIME type.
    public string AllowedMimeTypes { get; set; } = "image/*,application/pdf,text/plain,text/csv,application/json";
}


public class ConnectionLimits
{
    [Range(1, 1000)]
    public int MaxConcurrentQueries { get; set; } = 200;

    [Range(1, 1000)]
    public int MaxConnectionPoolSize { get; set; } = 200;

    [Range(0, 100)]
    public int MinConnectionPoolSize { get; set; } = 20;

    [Range(1, 3600)]
    public int ConnectionIdleLifetimeSeconds { get; set; } = 600;

    // Npgsql applies this lifetime when a connection leaves the pool, including a frozen Lambda sandbox, and an unset Lambda value stays under the proxy idle-client timeout.
    [Range(0, 86400)]
    public int ConnectionLifetimeSeconds { get; set; } = 3600;

    [Range(1, 3600)]
    public int ConnectionPruningIntervalSeconds { get; set; } = 10;

    [Range(1, 300)]
    public int CommandTimeoutSeconds { get; set; } = 30;

    [Range(1024, 1048576)]
    public int BufferSizeBytes { get; set; } = 32768;

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public string Multiplexing { get; set; } = "false";

    [Range(1, 300)]
    public int ConnectionAcquisitionTimeoutSeconds { get; set; } = 5;

    public bool AdaptiveConcurrencyEnabled { get; set; }

    [Range(1, 1000)]
    public int AdaptiveConcurrencyMinQueries { get; set; } = 1;

    [Range(0, 1000)]
    public int AdaptiveConcurrencyInitialQueries { get; set; } = 0;

    [Range(0, 1000)]
    public int AdaptiveConcurrencyMaxQueries { get; set; } = 0;

    [Range(1, 60000)]
    public int AdaptiveConcurrencyTargetDurationMs { get; set; } = 100;

    [Range(0, 60000)]
    public int AdaptiveConcurrencyUpdateIntervalMs { get; set; } = 1000;

    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan StatementTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan IdleInTransactionTimeout { get; set; } = TimeSpan.FromSeconds(60);
}
