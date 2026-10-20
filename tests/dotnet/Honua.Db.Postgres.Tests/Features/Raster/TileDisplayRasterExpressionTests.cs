// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Db.Postgres.Features.Raster;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Raster;

/// <summary>
/// SQL shape for two-band PNG display: unsigned gray-plus-alpha keeps band two,
/// and every other one- or two-band raster still synthesizes opacity from band one.
/// </summary>
public sealed class TileDisplayRasterExpressionTests
{
    [UnitTest]
    public void BuildTileDisplayRasterExpression_Png_KeepsUnsignedTwoBandAlphaAndNumericMask()
    {
        var sql = PostgresRasterStore.BuildTileDisplayRasterExpression(
            "src.rast", "tile_ref.rast", RasterFormat.PNG, stretchBounds: null);

        var alphaGuard = sql.IndexOf("ST_BandPixelType(i.rast, 1) IN ('8BUI', '16BUI')", StringComparison.Ordinal);
        var numericBranch = sql.IndexOf("ST_NumBands(i.rast) IN (1, 2)", StringComparison.Ordinal);
        alphaGuard.Should().BeGreaterThanOrEqualTo(0);
        numericBranch.Should().BeGreaterThan(alphaGuard);
        sql.Should().Contain("ST_BandPixelType(i.rast, 2) = ST_BandPixelType(i.rast, 1)");
        sql.Should().Contain("LEAST(255.0, GREATEST(0.0, [rast.val] / 257.0))");
        sql.Should().Contain("'255'");
    }

    [UnitTest]
    public void BuildTileDisplayRasterExpression_NonPng_DoesNotSynthesizeAlpha()
    {
        var sql = PostgresRasterStore.BuildTileDisplayRasterExpression(
            "src.rast", "tile_ref.rast", RasterFormat.TIFF, stretchBounds: null);

        sql.Should().NotContain("ST_BandPixelType");
        sql.Should().NotContain("numeric_input");
    }
}
