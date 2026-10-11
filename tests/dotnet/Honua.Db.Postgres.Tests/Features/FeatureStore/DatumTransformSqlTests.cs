// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Crs;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

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
    public void BuildTransformExpression_SelectionWithPipeline_EmitsOperationPipelineForm()
    {
        var selection = new DatumTransformationSelection
        {
            Name = "NAD_1983_To_WGS_1984_1",
            FromSrid = 4269,
            ToSrid = 4326,
            ProjPipeline = "+proj=pipeline +step +proj=noop"
        };

        var sql = DatumTransformSql.BuildTransformExpression("geom", 4326, selection);

        sql.Should().Be("ST_TransformPipeline(geom, '+proj=pipeline +step +proj=noop', 4326)");
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
            "ST_TransformPipeline(geom, '+proj=pipeline " +
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

        sql.Should().Be("ST_TransformPipeline(geom, '+proj=pipeline +step +proj=hgridshift +grids=us_noaa_conus.tif', 4269)");
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

        sql.Should().Be("ST_SetSRID(geom, 4269)");
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

        sql.Should().Be("ST_TransformPipeline(geom, 'a''b', 4326)");
    }

    [UnitTheory]
    [InlineData(3857)]
    [InlineData(102100)]
    [InlineData(900913)]
    [InlineData(3785)]
    public void BuildTransformExpression_GeographicSourceToWebMercator_ClampsBeforeTransform(int toSrid)
    {
        var sql = DatumTransformSql.BuildTransformExpression("geom", toSrid, selection: null, fromSrid: 4326);

        AssertPointClamp(sql, "geom", 4326, toSrid);
    }

    [UnitTest]
    public void BuildTransformExpression_OtherGeographicSourceToWebMercator_UsesSourceSridOnClip()
    {
        var sql = DatumTransformSql.BuildTransformExpression("geom", 102100, selection: null, fromSrid: 4269);

        AssertPointClamp(sql, "geom", 4269, 102100);
    }

    [UnitTest]
    public void BuildTransformExpression_GeographicSourceToNonMercator_DoesNotClamp()
    {
        var sql = DatumTransformSql.BuildTransformExpression("geom", 4269, selection: null, fromSrid: 4326);

        sql.Should().Be("ST_Transform(geom, 4269)");
        sql.Should().NotContain("ST_ClipByBox2D");
        sql.Should().NotContain("85.0511287798066");
    }

    [UnitTest]
    public void BuildTransformExpression_ProjectedSourceToWebMercator_DoesNotClamp()
    {
        var sql = DatumTransformSql.BuildTransformExpression("geom", 3857, selection: null, fromSrid: 26911);

        sql.Should().Be("ST_Transform(geom, 3857)");
        sql.Should().NotContain("ST_ClipByBox2D");
    }

    [UnitTest]
    public void BuildTransformExpression_UnknownSourceToWebMercator_DoesNotClamp()
    {
        var sql = DatumTransformSql.BuildTransformExpression("geom", 3857, selection: null);

        sql.Should().Be("ST_Transform(geom, 3857)");
        sql.Should().NotContain("ST_ClipByBox2D");
    }

    [UnitTest]
    public void BuildTransformExpression_GeographicToWebMercator_PipelineClampsBeforeTransform()
    {
        var selection = new DatumTransformationSelection
        {
            Name = "WGS_1984_To_WebMercator",
            FromSrid = 4326,
            ToSrid = 3857,
            ProjPipeline = "+proj=pipeline +step +proj=merc"
        };

        var sql = DatumTransformSql.BuildTransformExpression("geom", 3857, selection, fromSrid: 4326);

        sql.Should().StartWith("ST_TransformPipeline(CASE ST_GeometryType(geom)");
        sql.Should().Contain("LEAST(GREATEST(ST_Y(geom), -85.0511287798066), 85.0511287798066)");
        sql.Should().Contain("'+proj=pipeline +step +proj=merc', 3857)");
    }

    [UnitTest]
    public void BuildTransformExpression_NoopToWebMercator_DoesNotClamp()
    {
        var selection = new DatumTransformationSelection
        {
            Name = "identity",
            FromSrid = 4326,
            ToSrid = 3857,
            ProjPipeline = "+proj=noop"
        };

        var sql = DatumTransformSql.BuildTransformExpression("geom", 3857, selection, fromSrid: 4326);

        sql.Should().Be("ST_SetSRID(geom, 3857)");
        sql.Should().NotContain("ST_ClipByBox2D");
    }

    private static void AssertPointClamp(string sql, string operand, int fromSrid, int toSrid)
    {
        sql.Should().StartWith("ST_Transform(CASE ST_GeometryType(" + operand + ")");
        sql.Should().Contain("WHEN 'ST_Point' THEN CASE ST_Zmflag(" + operand + ")");
        sql.Should().Contain("LEAST(GREATEST(ST_Y(" + operand + "), -85.0511287798066), 85.0511287798066)");
        sql.Should().Contain("WHEN 'ST_MultiPoint' THEN (SELECT ST_Multi(ST_Collect(");
        sql.Should().Contain("FROM ST_Dump(" + operand + ") AS pt)");
        sql.Should().Contain(
            "ELSE ST_ClipByBox2D(" + operand + ", ST_MakeEnvelope(-180, -85.0511287798066, 180, 85.0511287798066, " + fromSrid + ")) END, " + toSrid + ")");
    }
}
