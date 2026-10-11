// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Db.Postgres.Features.Raster;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Raster;

public sealed class RasterWebMercatorSqlTests
{
    [UnitTest]
    public void BuildEnvelopeExpression_GeographicRaster_ClampsBeforeTransform()
    {
        var sql = RasterWebMercatorSql.BuildEnvelopeExpression("ST_Envelope(r.raster)", 4326);

        sql.Should().StartWith("ST_Transform(CASE ST_GeometryType(ST_Envelope(r.raster))");
        sql.Should().Contain("LEAST(GREATEST(ST_Y(ST_Envelope(r.raster)), -85.0511287798066), 85.0511287798066)");
        sql.Should().Contain(
            "ELSE ST_ClipByBox2D(ST_Envelope(r.raster), ST_MakeEnvelope(-180, -85.0511287798066, 180, 85.0511287798066, 4326)) END, 3857)");
    }

    [UnitTest]
    public void BuildEnvelopeExpression_ProjectedRaster_DoesNotClamp()
    {
        var sql = RasterWebMercatorSql.BuildEnvelopeExpression("ST_Envelope(r.raster)", 32610);

        sql.Should().Be("ST_Transform(ST_Envelope(r.raster), 3857)");
        sql.Should().NotContain("ST_ClipByBox2D");
        sql.Should().NotContain("85.0511287798066");
    }

    [UnitTest]
    public void BuildRasterExpression_GeographicRasterOutsideBand_ClipsBeforeTransform()
    {
        var sql = RasterWebMercatorSql.BuildRasterExpression("r.raster", 4326);

        sql.Should().StartWith("ST_Transform(CASE");
        sql.Should().Contain("ST_Clip(r.raster, ST_MakeEnvelope(-180, -85.0511287798066, 180, 85.0511287798066, 4326), TRUE)");
        sql.Should().Contain("ST_YMax(ST_Envelope(r.raster)) > 85.0511287798066");
        sql.Should().Contain("ST_YMin(ST_Envelope(r.raster)) < -85.0511287798066");
        sql.Trim().Should().EndWith("END, 3857)");
    }

    [UnitTest]
    public void BuildRasterExpression_ProjectedRaster_DoesNotClamp()
    {
        var sql = RasterWebMercatorSql.BuildRasterExpression("r.raster", 3857);

        sql.Should().Be("ST_Transform(r.raster, 3857)");
        sql.Should().NotContain("ST_Clip");
    }
}
