// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Configuration;

public sealed class GeoParquetLimits
{
    public const int SpecMaxRowsPerRowGroup = 150_000;

    public const int DefaultArtifactRowsPerRowGroup = 16_384;

    [Range(1, 10000)]
    public int MaxRowsPerBatch { get; set; } = 1024;

    [Range(1, int.MaxValue)]
    public long MaxEstimatedBatchBytes { get; set; } = 8 * 1024 * 1024;

    [Range(1, int.MaxValue)]
    public long MaxEstimatedInputBytes { get; set; } = 128 * 1024 * 1024;

    [Range(1, int.MaxValue)]
    public int MaxResponseBytes { get; set; } = 64 * 1024 * 1024;

    public int? RowsPerRowGroup { get; set; }

    public long? MaxArtifactBytes { get; set; }

    public bool OrderByBboxHilbert { get; set; }

    public bool CompressWithZstd { get; set; }

    public GeoParquetLimits ForQuery() => new()
    {
        MaxRowsPerBatch = MaxRowsPerBatch,
        MaxEstimatedBatchBytes = MaxEstimatedBatchBytes,
        MaxEstimatedInputBytes = MaxEstimatedInputBytes,
        MaxResponseBytes = MaxResponseBytes,
    };

    public static GeoParquetLimits ForArtifact(long maxArtifactBytes) => new()
    {
        RowsPerRowGroup = DefaultArtifactRowsPerRowGroup,
        MaxArtifactBytes = maxArtifactBytes,
        OrderByBboxHilbert = true,
        CompressWithZstd = true,
    };

    public static GeoParquetLimits ForArtifact(GeoParquetLimits configured)
    {
        ArgumentNullException.ThrowIfNull(configured);
        return new()
        {
            MaxRowsPerBatch = configured.MaxRowsPerBatch,
            MaxEstimatedBatchBytes = configured.MaxEstimatedBatchBytes,
            MaxEstimatedInputBytes = configured.MaxEstimatedInputBytes,
            MaxResponseBytes = configured.MaxResponseBytes,
            RowsPerRowGroup = configured.RowsPerRowGroup ?? DefaultArtifactRowsPerRowGroup,
            // MaxResponseBytes may be int.MaxValue. The artifact is materialized as a byte[],
            // so the copied cap stops at Array.MaxLength instead of failing the capture.
            MaxArtifactBytes = configured.MaxArtifactBytes ?? Math.Min(configured.MaxResponseBytes, Array.MaxLength),
            OrderByBboxHilbert = true,
            CompressWithZstd = true,
        };
    }
}
