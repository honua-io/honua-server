// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Crs;
using Honua.Postgres.Features.FeatureStore.Services;
using Honua.TestKit.Attributes;

namespace Honua.Postgres.Tests.Features.FeatureStore;

/// <summary>
/// Unit tests for <see cref="DatumTransformSql"/> (issue #1274). Asserts the shared
/// chokepoint emits the 2-argument PostGIS ST_Transform by default and the explicit
/// 3-argument pipeline form when a datum transformation is selected.
/// </summary>
public sealed class DatumTransformSqlTests
{
    [UnitTest]
    public void BuildTransformExpression_NoSelection_EmitsTwoArgumentForm()
    {
        var sql = DatumTransformSql.BuildTransformExpression("geom", 4326, selection: null);

        sql.Should().Be("ST_Transform(geom, 4326)");
    }

    [UnitTest]
    public void BuildTransformExpression_SelectionWithoutPipeline_EmitsTwoArgumentForm()
    {
        var selection = new DatumTransformationSelection
        {
            Name = "identity",
            FromSrid = 4269,
            ToSrid = 4326,
            ProjPipeline = null
        };

        var sql = DatumTransformSql.BuildTransformExpression("geom", 4326, selection);

        sql.Should().Be("ST_Transform(geom, 4326)");
    }

    [UnitTest]
    public void BuildTransformExpression_SelectionWithPipeline_EmitsThreeArgumentForm()
    {
        var selection = new DatumTransformationSelection
        {
            Name = "NAD_1983_To_WGS_1984_1",
            FromSrid = 4269,
            ToSrid = 4326,
            ProjPipeline = "+proj=pipeline +step +proj=noop"
        };

        var sql = DatumTransformSql.BuildTransformExpression("geom", 4326, selection);

        sql.Should().Be("ST_Transform(geom, '+proj=pipeline +step +proj=noop', 4326)");
    }

    [UnitTest]
    public void BuildTransformExpression_PipelineWithSingleQuote_IsEscaped()
    {
        var selection = new DatumTransformationSelection
        {
            Name = "quoted",
            FromSrid = 4269,
            ToSrid = 4326,
            ProjPipeline = "a'b"
        };

        var sql = DatumTransformSql.BuildTransformExpression("geom", 4326, selection);

        sql.Should().Be("ST_Transform(geom, 'a''b', 4326)");
    }
}
