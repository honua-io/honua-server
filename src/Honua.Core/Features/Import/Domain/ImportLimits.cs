// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Configuration limits for file import operations.
/// Controls batch sizes, memory usage, and background job thresholds.
/// </summary>
public sealed record ImportLimits
{
    /// <summary>
    /// Number of features to batch together for database insertion.
    /// Default: 1000 features per batch.
    /// </summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>
    /// Maximum memory usage target in bytes during import.
    /// Default: 100MB (104857600 bytes).
    /// </summary>
    public long MaxMemoryBytes { get; set; } = FileSizeConstants.OneHundredMB;

    /// <summary>
    /// File size threshold in bytes above which imports are queued for background processing.
    /// Default: 100MB (104857600 bytes).
    /// </summary>
    public long BackgroundJobThresholdBytes { get; set; } = FileSizeConstants.OneHundredMB;

    /// <summary>
    /// Maximum file size allowed for preview operations in bytes.
    /// Default: 10MB (10485760 bytes).
    /// </summary>
    public long MaxPreviewSizeBytes { get; set; } = FileSizeConstants.TenMB;

    /// <summary>
    /// Maximum number of features to include in a preview.
    /// Default: 100 features.
    /// </summary>
    public int MaxPreviewFeatures { get; set; } = 100;

    /// <summary>
    /// Maximum number of features to stream when counting features for preview
    /// in formats whose header does not declare a feature count (e.g. FlatGeobuf
    /// with FeaturesCount=0). Prevents unbounded scans of very large files.
    /// Default: 100,000 features.
    /// </summary>
    public int MaxPreviewCountScan { get; set; } = 100_000;

    /// <summary>
    /// Buffer size for streaming reads in bytes.
    /// Default: 64KB (65536 bytes).
    /// </summary>
    public int StreamBufferSize { get; set; } = 64 * (int)FileSizeConstants.OneKB;

    /// <summary>
    /// Whether to use transactions for batch inserts.
    /// When true, each batch is wrapped in a transaction.
    /// Default: true.
    /// </summary>
    public bool UseTransactions { get; set; } = true;

    /// <summary>
    /// Whether to continue processing after encountering invalid features.
    /// When true, invalid features are skipped; when false, the import fails on first error.
    /// Default: true.
    /// </summary>
    public bool ContinueOnError { get; set; } = true;

    /// <summary>
    /// Maximum number of features to import from a single file.
    /// 0 means no limit.
    /// Default: 0 (no limit).
    /// </summary>
    public int MaxFeaturesPerFile { get; set; }

    /// <summary>
    /// Whether to validate geometry during import.
    /// Default: true.
    /// </summary>
    public bool ValidateGeometry { get; set; } = true;

    /// <summary>
    /// How topologically-invalid geometry (e.g. self-intersecting polygons) is handled
    /// during import, mirroring the shared edit-path
    /// <see cref="Honua.Core.Configuration.GeometryValidationOptions"/> semantics so every
    /// import reader applies the same validity gate:
    /// <list type="bullet">
    /// <item><see cref="ValidationMode.Accept"/> — store geometry as-is (legacy behavior).</item>
    /// <item><see cref="ValidationMode.Strict"/> — reject invalid geometry (skipped when
    /// <see cref="SkipInvalidGeometry"/>, otherwise fails the import).</item>
    /// <item><see cref="ValidationMode.Repair"/> — repair invalid geometry with the managed
    /// NetTopologySuite <c>GeometryFixer</c> (the GEOS-free analogue of PostGIS
    /// <c>ST_MakeValid</c>) and count the repair.</item>
    /// </list>
    /// Default: <see cref="ValidationMode.Repair"/> — matches the edit paths and prevents
    /// later overlay queries from failing with GEOS topology exceptions.
    /// </summary>
    public ValidationMode GeometryValidityMode { get; set; } = ValidationMode.Repair;

    /// <summary>
    /// Maximum number of vertices allowed per geometry.
    /// Default: 10000 vertices.
    /// </summary>
    public int MaxVertices { get; set; } = 10000;

    /// <summary>
    /// Maximum number of rings allowed per polygon geometry.
    /// Default: 100 rings.
    /// </summary>
    public int MaxRings { get; set; } = 100;

    /// <summary>
    /// Maximum WKB size in bytes per geometry.
    /// Default: 1MB (1048576 bytes).
    /// </summary>
    public long MaxWkbSize { get; set; } = FileSizeConstants.OneMB;

    /// <summary>
    /// Maximum number of bytes that may be buffered in memory for a single,
    /// not-yet-closed feature (or any pre-feature leftover) while streaming a
    /// feature collection. Caps unbounded in-memory accumulation when a reader
    /// streams at feature granularity but receives one pathologically large
    /// feature object (or an array-start that never closes); the reader fails
    /// fast once the leftover buffer would exceed this budget.
    /// Default: 64MB (67108864 bytes).
    /// </summary>
    public long MaxSingleFeatureBytes { get; set; } = 64 * FileSizeConstants.OneMB;

    /// <summary>
    /// Maximum uncompressed bytes extracted from a single archive entry during ZIP/KMZ import.
    /// Default: 500MB (524288000 bytes).
    /// </summary>
    public long MaxArchiveEntryBytes { get; set; } = FileSizeConstants.FiveHundredMB;

    /// <summary>
    /// Maximum total uncompressed bytes extracted from a ZIP/KMZ archive during import.
    /// Default: 1GB (1073741824 bytes).
    /// </summary>
    public long MaxArchiveExtractedBytes { get; set; } = FileSizeConstants.OneGB;

    /// <summary>
    /// Maximum allowed compression ratio for ZIP/KMZ entries (uncompressed/compressed).
    /// Default: 200.
    /// </summary>
    public double MaxArchiveCompressionRatio { get; set; } = 200;

    /// <summary>
    /// Whether to skip invalid geometries or fail the import.
    /// When true, invalid geometries are skipped; when false, import fails.
    /// Default: true (skip invalid).
    /// </summary>
    public bool SkipInvalidGeometry { get; set; } = true;

    /// <summary>
    /// Default configuration for standard import operations.
    /// </summary>
    public static ImportLimits Default => new();

    /// <summary>
    /// Configuration for memory-constrained environments (serverless, containers).
    /// Uses smaller batch sizes and stricter memory limits.
    /// </summary>
    public static ImportLimits MemoryConstrained => new()
    {
        BatchSize = 500,
        MaxMemoryBytes = FileSizeConstants.FiftyMB,
        BackgroundJobThresholdBytes = FileSizeConstants.FiftyMB,
        StreamBufferSize = 32 * (int)FileSizeConstants.OneKB
    };

    /// <summary>
    /// Configuration for high-throughput environments with more resources.
    /// Uses larger batch sizes for better performance.
    /// </summary>
    public static ImportLimits HighThroughput => new()
    {
        BatchSize = 5000,
        MaxMemoryBytes = FileSizeConstants.FiveHundredMB,
        BackgroundJobThresholdBytes = FileSizeConstants.FiveHundredMB,
        StreamBufferSize = 256 * (int)FileSizeConstants.OneKB
    };
}
