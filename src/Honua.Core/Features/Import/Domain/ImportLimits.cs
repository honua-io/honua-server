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
    public int BatchSize { get; init; } = 1000;

    /// <summary>
    /// Maximum memory usage target in bytes during import.
    /// Default: 100MB (104857600 bytes).
    /// </summary>
    public long MaxMemoryBytes { get; init; } = FileSizeConstants.OneHundredMB;

    /// <summary>
    /// File size threshold in bytes above which imports are queued for background processing.
    /// Default: 100MB (104857600 bytes).
    /// </summary>
    public long BackgroundJobThresholdBytes { get; init; } = FileSizeConstants.OneHundredMB;

    /// <summary>
    /// Maximum file size allowed for preview operations in bytes.
    /// Default: 10MB (10485760 bytes).
    /// </summary>
    public long MaxPreviewSizeBytes { get; init; } = FileSizeConstants.TenMB;

    /// <summary>
    /// Maximum number of features to include in a preview.
    /// Default: 100 features.
    /// </summary>
    public int MaxPreviewFeatures { get; init; } = 100;

    /// <summary>
    /// Buffer size for streaming reads in bytes.
    /// Default: 64KB (65536 bytes).
    /// </summary>
    public int StreamBufferSize { get; init; } = 64 * (int)FileSizeConstants.OneKB;

    /// <summary>
    /// Whether to use transactions for batch inserts.
    /// When true, each batch is wrapped in a transaction.
    /// Default: true.
    /// </summary>
    public bool UseTransactions { get; init; } = true;

    /// <summary>
    /// Whether to continue processing after encountering invalid features.
    /// When true, invalid features are skipped; when false, the import fails on first error.
    /// Default: true.
    /// </summary>
    public bool ContinueOnError { get; init; } = true;

    /// <summary>
    /// Maximum number of features to import from a single file.
    /// 0 means no limit.
    /// Default: 0 (no limit).
    /// </summary>
    public int MaxFeaturesPerFile { get; init; }

    /// <summary>
    /// Whether to validate geometry during import.
    /// Default: true.
    /// </summary>
    public bool ValidateGeometry { get; init; } = true;

    /// <summary>
    /// Maximum number of vertices allowed per geometry.
    /// Default: 10000 vertices.
    /// </summary>
    public int MaxVertices { get; init; } = 10000;

    /// <summary>
    /// Maximum number of rings allowed per polygon geometry.
    /// Default: 100 rings.
    /// </summary>
    public int MaxRings { get; init; } = 100;

    /// <summary>
    /// Maximum WKB size in bytes per geometry.
    /// Default: 1MB (1048576 bytes).
    /// </summary>
    public long MaxWkbSize { get; init; } = FileSizeConstants.OneMB;

    /// <summary>
    /// Maximum uncompressed bytes extracted from a single archive entry during ZIP/KMZ import.
    /// Default: 500MB (524288000 bytes).
    /// </summary>
    public long MaxArchiveEntryBytes { get; init; } = FileSizeConstants.FiveHundredMB;

    /// <summary>
    /// Maximum total uncompressed bytes extracted from a ZIP/KMZ archive during import.
    /// Default: 1GB (1073741824 bytes).
    /// </summary>
    public long MaxArchiveExtractedBytes { get; init; } = FileSizeConstants.OneGB;

    /// <summary>
    /// Maximum allowed compression ratio for ZIP/KMZ entries (uncompressed/compressed).
    /// Default: 200.
    /// </summary>
    public double MaxArchiveCompressionRatio { get; init; } = 200;

    /// <summary>
    /// Whether to skip invalid geometries or fail the import.
    /// When true, invalid geometries are skipped; when false, import fails.
    /// Default: true (skip invalid).
    /// </summary>
    public bool SkipInvalidGeometry { get; init; } = true;

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
