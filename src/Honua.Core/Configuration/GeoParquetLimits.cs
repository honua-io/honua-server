// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Configuration;

/// <summary>Budgets for encoding materialized query results as GeoParquet.</summary>
public sealed class GeoParquetLimits
{
    /// <summary>Maximum rows in an Arrow batch and Parquet row group.</summary>
    [Range(1, 10000)]
    public int MaxRowsPerBatch { get; set; } = 1024;

    /// <summary>Maximum estimated input/encoding bytes admitted to one batch, including a single row.</summary>
    [Range(1, int.MaxValue)]
    public long MaxEstimatedBatchBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>Maximum estimated bytes admitted for the already-materialized query result.</summary>
    [Range(1, int.MaxValue)]
    public long MaxEstimatedInputBytes { get; set; } = 128 * 1024 * 1024;

    /// <summary>Maximum encoded payload bytes, including the Parquet footer.</summary>
    [Range(1, int.MaxValue)]
    public int MaxResponseBytes { get; set; } = 64 * 1024 * 1024;
}
