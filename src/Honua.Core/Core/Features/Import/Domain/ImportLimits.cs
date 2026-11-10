// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;

namespace Honua.Core.Features.Import.Domain;

public sealed record ImportLimits
{
    public int BatchSize { get; init; } = 1000;

    public long MaxMemoryBytes { get; init; } = FileSizeConstants.OneHundredMB;

    public long BackgroundJobThresholdBytes { get; init; } = FileSizeConstants.OneHundredMB;

    // An object above this limit is refused before any byte is read.
    public long MaxObjectStoreImportBytes { get; init; } = 50 * FileSizeConstants.OneGB;

    public long MaxPreviewSizeBytes { get; init; } = FileSizeConstants.TenMB;

    public int MaxPreviewFeatures { get; init; } = 100;

    public int MaxPreviewCountScan { get; init; } = 100_000;

    public int StreamBufferSize { get; init; } = 64 * (int)FileSizeConstants.OneKB;

    public bool UseTransactions { get; init; } = true;

    public bool ContinueOnError { get; init; } = true;

    public int MaxFeaturesPerFile { get; init; }

    public bool ValidateGeometry { get; init; } = true;

    public ValidationMode GeometryValidityMode { get; init; } = ValidationMode.Repair;

    public int MaxVertices { get; init; } = 10000;

    public int MaxRings { get; init; } = 100;

    public long MaxWkbSize { get; init; } = FileSizeConstants.OneMB;

    // A single feature that would grow the leftover buffer past this cap fails the stream instead of accumulating without bound.
    public long MaxSingleFeatureBytes { get; init; } = 64 * FileSizeConstants.OneMB;

    public long MaxArchiveEntryBytes { get; init; } = FileSizeConstants.FiveHundredMB;

    public long MaxArchiveExtractedBytes { get; init; } = FileSizeConstants.OneGB;

    public double MaxArchiveCompressionRatio { get; init; } = 200;

    public int MaxArchiveEntries { get; init; } = 10_000;

    public int MaxConcurrentBackgroundJobs { get; init; } = 2;

    public int MaxBackgroundJobQueueDepth { get; init; } = 16;

    public bool SkipInvalidGeometry { get; init; } = true;

    public static ImportLimits Default => new();

    public static ImportLimits MemoryConstrained => new()
    {
        BatchSize = 500,
        MaxMemoryBytes = FileSizeConstants.FiftyMB,
        BackgroundJobThresholdBytes = FileSizeConstants.FiftyMB,
        StreamBufferSize = 32 * (int)FileSizeConstants.OneKB
    };

    public static ImportLimits HighThroughput => new()
    {
        BatchSize = 5000,
        MaxMemoryBytes = FileSizeConstants.FiveHundredMB,
        BackgroundJobThresholdBytes = FileSizeConstants.FiveHundredMB,
        StreamBufferSize = 256 * (int)FileSizeConstants.OneKB
    };
}
