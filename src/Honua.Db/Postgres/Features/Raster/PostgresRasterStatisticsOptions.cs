// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Db.Postgres.Features.Raster;

internal sealed class PostgresRasterStatisticsOptions
{
    public const string SectionName = "Database:RasterStatistics";

    public const int MaximumDenseSampleSlots = 16_777_216;

    [Range(1, MaximumDenseSampleSlots)]
    public int MaxDenseSampleSlots { get; set; } = MaximumDenseSampleSlots;
}
