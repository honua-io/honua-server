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
            "src.rast", "tile_ref.rast", RasterFormat.PNG, stretchBounds: null,
            preserveAlphaExpression: "src.second_band_is_alpha");

        var alphaGuard = sql.IndexOf("i.preserve_alpha", StringComparison.Ordinal);
        var pixelTypeGuard = sql.IndexOf("ST_BandPixelType(i.rast, 1) IN ('8BUI', '16BUI')", StringComparison.Ordinal);
        var numericBranch = sql.IndexOf("ST_NumBands(i.rast) IN (1, 2)", StringComparison.Ordinal);
        alphaGuard.Should().BeGreaterThanOrEqualTo(0);
        pixelTypeGuard.Should().BeGreaterThan(alphaGuard);
        numericBranch.Should().BeGreaterThan(pixelTypeGuard);
        sql.Should().Contain("src.second_band_is_alpha");
        sql.Should().Contain("ST_BandPixelType(i.rast, 2) = ST_BandPixelType(i.rast, 1)");
        sql.Should().Contain("LEAST(255.0, GREATEST(0.0, [rast.val] / 257.0))");
        sql.Should().Contain("'255'");
    }

    [UnitTest]
    public void PngDeclaresGrayAlpha_RequiresIhdrColorTypeFour()
    {
        var grayAlpha = new byte[26];
        grayAlpha[0] = 0x89;
        grayAlpha[1] = 0x50;
        grayAlpha[2] = 0x4E;
        grayAlpha[3] = 0x47;
        grayAlpha[4] = 0x0D;
        grayAlpha[5] = 0x0A;
        grayAlpha[6] = 0x1A;
        grayAlpha[7] = 0x0A;
        grayAlpha[12] = (byte)'I';
        grayAlpha[13] = (byte)'H';
        grayAlpha[14] = (byte)'D';
        grayAlpha[15] = (byte)'R';
        grayAlpha[25] = 4;
        PostgresRasterImportService.PngDeclaresGrayAlpha(grayAlpha).Should().BeTrue();

        grayAlpha[25] = 0;
        PostgresRasterImportService.PngDeclaresGrayAlpha(grayAlpha).Should().BeFalse();
        PostgresRasterImportService.PngDeclaresGrayAlpha([0x49, 0x49, 0x2A, 0x00]).Should().BeFalse();
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
