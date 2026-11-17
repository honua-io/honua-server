// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Configuration;

public sealed class GeoParquetLimits
{
    /// <summary>GeoParquet row-group ceiling. Export artifacts must stay at or under this.</summary>
    public const int SpecMaxRowsPerRowGroup = 150_000;

    /// <summary>Export row-group target inside the 10_000–20_000 band, independent of <see cref="MaxRowsPerBatch"/>.</summary>
    public const int DefaultArtifactRowsPerRowGroup = 16_384;

    [Range(1, 10000)]
    public int MaxRowsPerBatch { get; set; } = 1024;

    [Range(1, int.MaxValue)]
    public long MaxEstimatedBatchBytes { get; set; } = 8 * 1024 * 1024;

    [Range(1, int.MaxValue)]
    public long MaxEstimatedInputBytes { get; set; } = 128 * 1024 * 1024;

    /// <summary>Cap for an HTTP GeoParquet response. Stored artifacts use <see cref="MaxArtifactBytes"/> instead.</summary>
    [Range(1, int.MaxValue)]
    public int MaxResponseBytes { get; set; } = 64 * 1024 * 1024;

    /// <summary>
    /// Rows in one Parquet row group. Null keeps one row group per response batch.
    /// Set for artifacts; never above <see cref="SpecMaxRowsPerRowGroup"/>.
    /// </summary>
    public int? RowsPerRowGroup { get; set; }

    /// <summary>Byte cap for a stored artifact. Null keeps <see cref="MaxResponseBytes"/>.</summary>
    public long? MaxArtifactBytes { get; set; }

    /// <summary>Sort artifact rows by the Hilbert index of each geometry bbox centre.</summary>
    public bool OrderByBboxHilbert { get; set; }

    /// <summary>Compress artifact columns with Zstandard. Query responses stay uncompressed.</summary>
    public bool CompressWithZstd { get; set; }

    public static GeoParquetLimits ForArtifact(long maxArtifactBytes) => new()
    {
        RowsPerRowGroup = DefaultArtifactRowsPerRowGroup,
        MaxArtifactBytes = maxArtifactBytes,
        OrderByBboxHilbert = true,
        CompressWithZstd = true,
    };
}
