// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Postgres.Features.Raster;
using Honua.TestKit.Attributes;

namespace Honua.Postgres.Tests.Features.Raster;

[Collection("Unit")]
public sealed class PostgresRasterStoreOverviewTests
{
    [UnitTest]
    public void BuildOverviewSourceExpression_AtNativeZoom_IsNoOpBareRaster()
    {
        // At/above the native-resolution threshold the source must be read unchanged so
        // high-zoom tiles keep resampling the full-resolution raster column.
        var expr = PostgresRasterStore.BuildOverviewSourceExpression(PostgresRasterStore.OverviewMaxZoom);

        expr.Should().Be("raster");
    }

    [UnitTest]
    public void BuildOverviewSourceExpression_AboveNativeZoom_IsNoOpBareRaster()
    {
        var expr = PostgresRasterStore.BuildOverviewSourceExpression(PostgresRasterStore.OverviewMaxZoom + 5);

        expr.Should().Be("raster");
    }

    [UnitTest]
    public void BuildOverviewSourceExpression_AtLowZoom_ReducesResolution()
    {
        var expr = PostgresRasterStore.BuildOverviewSourceExpression(0);

        // A low-zoom tile reduces the source to a coarser grid before the precise resample.
        expr.Should().NotBe("raster");
        expr.Should().StartWith("ST_Rescale(");
        expr.Should().Contain("ST_Transform(raster, 3857)");
        // Guarded so the rescale can only coarsen, never upsample, per-source.
        expr.Should().Contain("GREATEST(abs(ST_ScaleX(");
        expr.Should().Contain("GREATEST(abs(ST_ScaleY(");
        expr.Should().Contain("'NearestNeighbor'");
    }

    [UnitTest]
    public void BuildOverviewSourceExpression_LowerZoom_TargetsCoarserResolution()
    {
        // Tile ground resolution doubles each zoom-out, so a lower zoom must target a
        // numerically larger metres-per-pixel value than a higher (still-low) zoom.
        var z2 = PostgresRasterStore.BuildOverviewSourceExpression(2);
        var z6 = PostgresRasterStore.BuildOverviewSourceExpression(6);

        // z=2 mpp = 40075016.686 / (256 * 4) ≈ 39135.76
        z2.Should().Contain("39135.7");
        // z=6 mpp = 40075016.686 / (256 * 64) ≈ 2445.98
        z6.Should().Contain("2445.9");
    }

    [UnitTest]
    public void BuildOverviewSourceExpression_HonoursCustomToken()
    {
        var expr = PostgresRasterStore.BuildOverviewSourceExpression(3, "rast");

        expr.Should().Contain("ST_Transform(rast, 3857)");
        expr.Should().NotContain("raster");
    }
}
