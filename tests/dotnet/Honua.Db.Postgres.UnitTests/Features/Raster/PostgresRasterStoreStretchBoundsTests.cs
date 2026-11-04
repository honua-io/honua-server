// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Db.Postgres.Features.Raster;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Raster;

[Collection("Unit")]
public sealed class PostgresRasterStoreStretchBoundsTests
{
    private static readonly RasterStretch AutoStretch = new() { StretchType = RasterStretchType.MinMax };
    private static readonly RasterStretch ExplicitStretch = new() { StretchType = RasterStretchType.MinMax, StatisticsMin = [0], StatisticsMax = [255] };
    private static readonly RasterBandArithmetic Ndvi = new() { VisibleBand = 3, InfraredBand = 4, Method = RasterBandArithmeticMethod.Ndvi };
    private static readonly RasterTerrainFunction Hillshade = new() { Method = RasterTerrainMethod.Hillshade };

    [UnitTest]
    public void CanResolveStretchBounds_SourceBands_DerivesFromStoredStatistics()
        => PostgresRasterStore.CanResolveStretchBounds(AutoStretch, bandArithmetic: null, terrain: null).Should().BeTrue();

    [UnitTest]
    public void CanResolveStretchBounds_BandArithmeticWithoutExplicitRange_IsSkipped()
        => PostgresRasterStore.CanResolveStretchBounds(AutoStretch, Ndvi, terrain: null).Should().BeFalse();

    [UnitTest]
    public void CanResolveStretchBounds_TerrainWithoutExplicitRange_IsSkipped()
        => PostgresRasterStore.CanResolveStretchBounds(AutoStretch, bandArithmetic: null, Hillshade).Should().BeFalse(
            "elevation statistics do not describe hillshade output");

    [UnitTest]
    public void CanResolveStretchBounds_TerrainWithExplicitRange_IsHonoured()
        => PostgresRasterStore.CanResolveStretchBounds(ExplicitStretch, bandArithmetic: null, Hillshade).Should().BeTrue();
}
