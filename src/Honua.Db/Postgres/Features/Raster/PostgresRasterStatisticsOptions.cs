// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Db.Postgres.Features.Raster;

/// <summary>Bounds cold, dense full-catalog statistics without changing persisted statistics.</summary>
public sealed class PostgresRasterStatisticsOptions
{
    /// <summary>The configuration section for the statistics allocation limit.</summary>
    public const string SectionName = "Database:RasterStatistics";

    /// <summary>The maximum supported number of dense pixel-band slots.</summary>
    public const int MaximumDenseSampleSlots = 16_777_216;

    /// <summary>
    /// Dense width times height times bands admitted for a cold mosaic computation.
    /// Operators may lower this ceiling. It is not a total PostgreSQL memory limit.
    /// </summary>
    [Range(1, MaximumDenseSampleSlots)]
    public int MaxDenseSampleSlots { get; set; } = MaximumDenseSampleSlots;
}
