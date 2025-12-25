// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
    public long MaxMemoryBytes { get; init; } = 100 * 1024 * 1024;

    /// <summary>
    /// File size threshold in bytes above which imports are queued for background processing.
    /// Default: 100MB (104857600 bytes).
    /// </summary>
    public long BackgroundJobThresholdBytes { get; init; } = 100 * 1024 * 1024;

    /// <summary>
    /// Maximum file size allowed for preview operations in bytes.
    /// Default: 10MB (10485760 bytes).
    /// </summary>
    public long MaxPreviewSizeBytes { get; init; } = 10 * 1024 * 1024;

    /// <summary>
    /// Maximum number of features to include in a preview.
    /// Default: 100 features.
    /// </summary>
    public int MaxPreviewFeatures { get; init; } = 100;

    /// <summary>
    /// Buffer size for streaming reads in bytes.
    /// Default: 64KB (65536 bytes).
    /// </summary>
    public int StreamBufferSize { get; init; } = 64 * 1024;

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
    public int MaxFeaturesPerFile { get; init; } = 0;

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
        MaxMemoryBytes = 50 * 1024 * 1024,
        BackgroundJobThresholdBytes = 50 * 1024 * 1024,
        StreamBufferSize = 32 * 1024
    };

    /// <summary>
    /// Configuration for high-throughput environments with more resources.
    /// Uses larger batch sizes for better performance.
    /// </summary>
    public static ImportLimits HighThroughput => new()
    {
        BatchSize = 5000,
        MaxMemoryBytes = 500 * 1024 * 1024,
        BackgroundJobThresholdBytes = 500 * 1024 * 1024,
        StreamBufferSize = 256 * 1024
    };
}
