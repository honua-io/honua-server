// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

public sealed class DatumTransformOutputSridTests
{
    [UnitTheory]
    [InlineData("geometry", 4326, 3857)]
    [InlineData("geometry", 3857, 4326)]
    [InlineData("geometry", 4326, 2263)]
    [InlineData("geometry::geometry", 26918, 4326)]
    [InlineData("ST_SetSRID(ST_GeomFromEWKB(geometry), 4269)", 4269, 900913)]
    [InlineData("point_geom", 4326, 102100)]
    public void TransformToOutputSrid_PlainReprojection_MatchesTheBuiltExpression(string operand, int fromSrid, int toSrid)
    {
        var expected = DatumTransformSql.BuildTransformExpression(operand, toSrid, selection: null, fromSrid);

        var query = new FeatureQuery { SpatialReferenceSrid = fromSrid, OutputSrid = toSrid };

        DatumTransformSql.TransformToOutputSrid(operand, query).Should().Be(expected);
        DatumTransformSql.TransformToOutputSrid(operand, query).Should().Be(expected);
    }

    [UnitTest]
    public void TransformToOutputSrid_UnknownSourceSrid_MatchesTheBuiltExpression()
    {
        var expected = DatumTransformSql.BuildTransformExpression("geometry", 3857, selection: null, fromSrid: null);

        DatumTransformSql.TransformToOutputSrid("geometry", new FeatureQuery { OutputSrid = 3857 }).Should().Be(expected);
    }

    [UnitTest]
    public void TransformToOutputSrid_DatumSelection_BuildsTheSelectedPipeline()
    {
        var selection = new DatumTransformationSelection
        {
            Name = "NAD27 to NAD83 (noop)",
            FromSrid = 4267,
            ToSrid = 4269,
            ProjPipeline = "+proj=noop",
        };

        var query = new FeatureQuery { SpatialReferenceSrid = 4267, OutputSrid = 4269, OutputDatumTransformation = selection };

        var actual = DatumTransformSql.TransformToOutputSrid("geometry", query);

        actual.Should().Be(DatumTransformSql.BuildTransformExpression("geometry", 4269, selection, 4267));
        actual.Should().Be("ST_SetSRID(geometry, 4269)");
    }
}
