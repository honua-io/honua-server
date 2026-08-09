// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Postgres.Features.Raster;
using Honua.TestKit.Attributes;

namespace Honua.Postgres.Tests.Features.Raster;

[Collection("Unit")]
public sealed class PostgresRasterStoreBandArithmeticTests
{
    [UnitTest]
    public void BuildBandArithmeticExpression_Ndvi_EmitsTwoRasterMapAlgebraWithConstantFormula()
    {
        var ba = new RasterBandArithmetic
        {
            VisibleBand = 3,
            InfraredBand = 4,
            Method = RasterBandArithmeticMethod.Ndvi,
        };

        var sql = PostgresRasterStore.BuildBandArithmeticExpression("raster", ba);

        // Two-raster ST_MapAlgebra: nir band first (rast1), visible band second (rast2).
        sql.Should().Be(
            "ST_MapAlgebra(raster, 4, raster, 3, " +
            "'([rast1.val] - [rast2.val]) / NULLIF(([rast1.val] + [rast2.val]), 0)', " +
            "'32BF', 'INTERSECTION', NULL, NULL)");
    }

    [UnitTest]
    public void BuildBandArithmeticExpression_PreservesBaseExpression()
    {
        var ba = new RasterBandArithmetic
        {
            VisibleBand = 1,
            InfraredBand = 2,
            Method = RasterBandArithmeticMethod.Ndvi,
        };

        var sql = PostgresRasterStore.BuildBandArithmeticExpression("ST_Band(raster, @bands)", ba);

        // The selected-band expression is threaded into both raster operands.
        sql.Should().StartWith("ST_MapAlgebra(ST_Band(raster, @bands), 2, ST_Band(raster, @bands), 1,");
    }

    [UnitTest]
    public void BuildBandArithmeticExpression_VisibleBandNotPositive_Throws()
    {
        var ba = new RasterBandArithmetic
        {
            VisibleBand = 0,
            InfraredBand = 2,
            Method = RasterBandArithmeticMethod.Ndvi,
        };

        var act = () => PostgresRasterStore.BuildBandArithmeticExpression("raster", ba);

        act.Should().Throw<ArgumentException>();
    }

    [UnitTest]
    public void BuildBandArithmeticExpression_InfraredBandNotPositive_Throws()
    {
        var ba = new RasterBandArithmetic
        {
            VisibleBand = 1,
            InfraredBand = -1,
            Method = RasterBandArithmeticMethod.Ndvi,
        };

        var act = () => PostgresRasterStore.BuildBandArithmeticExpression("raster", ba);

        act.Should().Throw<ArgumentException>();
    }

    [UnitTest]
    public void BuildBandArithmeticExpression_Ndwi_EmitsNormalizedDifferenceFormula()
    {
        var ba = new RasterBandArithmetic
        {
            InfraredBand = 2, // GREEN -> rast1
            VisibleBand = 4,  // NIR   -> rast2
            Method = RasterBandArithmeticMethod.Ndwi,
        };

        var sql = PostgresRasterStore.BuildBandArithmeticExpression("raster", ba);

        sql.Should().Be(
            "ST_MapAlgebra(raster, 2, raster, 4, " +
            "'([rast1.val] - [rast2.val]) / NULLIF(([rast1.val] + [rast2.val]), 0)', " +
            "'32BF', 'INTERSECTION', NULL, NULL)");
    }

    [UnitTest]
    public void BuildBandArithmeticExpression_Savi_EmitsSoilAdjustedFormulaWithLFactor()
    {
        var ba = new RasterBandArithmetic
        {
            InfraredBand = 4, // NIR -> rast1
            VisibleBand = 3,  // VIS -> rast2
            Method = RasterBandArithmeticMethod.Savi,
        };

        var sql = PostgresRasterStore.BuildBandArithmeticExpression("raster", ba);

        sql.Should().Be(
            "ST_MapAlgebra(raster, 4, raster, 3, " +
            "'(([rast1.val] - [rast2.val]) * 1.5) / NULLIF(([rast1.val] + [rast2.val] + 0.5), 0)', " +
            "'32BF', 'INTERSECTION', NULL, NULL)");
    }

    [UnitTest]
    public void BuildTerrainExpression_HillshadeDefaults_EmitsStHillShade()
    {
        var terrain = new RasterTerrainFunction { Method = RasterTerrainMethod.Hillshade };

        var sql = PostgresRasterStore.BuildTerrainExpression("raster", terrain);

        // Defaults: band 1, azimuth 315, altitude 45, z-factor 1.
        sql.Should().Be("ST_HillShade(raster, 1, '32BF', 315, 45, 255, 1, FALSE)");
    }

    [UnitTest]
    public void BuildTerrainExpression_Slope_EmitsStSlopeInDegrees()
    {
        var terrain = new RasterTerrainFunction { Method = RasterTerrainMethod.Slope, Band = 2 };

        var sql = PostgresRasterStore.BuildTerrainExpression("raster", terrain);

        sql.Should().Be("ST_Slope(raster, 2, '32BF', 'DEGREES', 1, FALSE)");
    }

    [UnitTest]
    public void BuildTerrainExpression_Aspect_EmitsStAspectInDegrees()
    {
        var terrain = new RasterTerrainFunction { Method = RasterTerrainMethod.Aspect };

        var sql = PostgresRasterStore.BuildTerrainExpression("raster", terrain);

        sql.Should().Be("ST_Aspect(raster, 1, '32BF', 'DEGREES', FALSE)");
    }

    [UnitTest]
    public void BuildTerrainExpression_NonPositiveBand_Throws()
    {
        var terrain = new RasterTerrainFunction { Method = RasterTerrainMethod.Slope, Band = 0 };

        var act = () => PostgresRasterStore.BuildTerrainExpression("raster", terrain);

        act.Should().Throw<ArgumentException>();
    }

    [UnitTest]
    public void BuildTerrainExpression_NonPositiveZFactor_Throws()
    {
        var terrain = new RasterTerrainFunction { Method = RasterTerrainMethod.Hillshade, ZFactor = 0 };

        var act = () => PostgresRasterStore.BuildTerrainExpression("raster", terrain);

        act.Should().Throw<ArgumentException>();
    }
}
