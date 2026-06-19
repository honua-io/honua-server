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
}
