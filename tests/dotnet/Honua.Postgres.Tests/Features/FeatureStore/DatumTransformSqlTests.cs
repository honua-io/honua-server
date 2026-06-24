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
    public void BuildTransformExpression_ReverseSelection_InvertsPipeline()
    {
        // A reverse selection (e.g. NAD83 -> NAD27) carries the forward NADCON pipeline verbatim
        // with TransformForward=false. Emitting it as-is would shift coordinates the wrong way —
        // roughly twice the intended NADCON correction (#2069). The emitted pipeline must invert
        // the direction-sensitive hgridshift step (+inv) and reverse the step order.
        var selection = new DatumTransformationSelection
        {
            Name = "NAD_1927_To_NAD_1983_NADCON",
            FromSrid = 4326,
            ToSrid = 4269,
            ProjPipeline = "+proj=pipeline +step +proj=unitconvert +xy_in=deg +xy_out=rad +step +proj=hgridshift +grids=us_noaa_conus.tif +step +proj=unitconvert +xy_in=rad +xy_out=deg",
            TransformForward = false
        };

        var sql = DatumTransformSql.BuildTransformExpression("geom", 4269, selection);

        sql.Should().Be(
            "ST_Transform(geom, '+proj=pipeline " +
            "+step +proj=unitconvert +xy_in=rad +xy_out=deg +inv " +
            "+step +proj=hgridshift +grids=us_noaa_conus.tif +inv " +
            "+step +proj=unitconvert +xy_in=deg +xy_out=rad +inv', 4269)");
    }

    [UnitTest]
    public void BuildTransformExpression_ForwardSelection_KeepsPipelineVerbatim()
    {
        // The forward direction must be emitted exactly as catalogued (no +inv).
        var selection = new DatumTransformationSelection
        {
            Name = "NAD_1927_To_NAD_1983_NADCON",
            FromSrid = 4267,
            ToSrid = 4269,
            ProjPipeline = "+proj=pipeline +step +proj=hgridshift +grids=us_noaa_conus.tif",
            TransformForward = true
        };

        var sql = DatumTransformSql.BuildTransformExpression("geom", 4269, selection);

        sql.Should().Be("ST_Transform(geom, '+proj=pipeline +step +proj=hgridshift +grids=us_noaa_conus.tif', 4269)");
    }

    [UnitTest]
    public void BuildTransformExpression_ReverseNoopSelection_StaysNoop()
    {
        // +proj=noop is self-inverse; a reverse identity selection must stay identity.
        var selection = new DatumTransformationSelection
        {
            Name = "NAD_1983_To_WGS_1984_1",
            FromSrid = 4326,
            ToSrid = 4269,
            ProjPipeline = "+proj=noop",
            TransformForward = false
        };

        var sql = DatumTransformSql.BuildTransformExpression("geom", 4269, selection);

        sql.Should().Be("ST_Transform(geom, '+proj=noop', 4269)");
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
