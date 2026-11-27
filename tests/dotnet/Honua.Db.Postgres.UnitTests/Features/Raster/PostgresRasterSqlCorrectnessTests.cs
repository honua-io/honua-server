// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Db.Postgres.Features.Raster;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Raster;

[Collection("Unit")]
public sealed class PostgresRasterSqlCorrectnessTests
{
    [UnitTheory]
    [InlineData("NearestNeighbor")]
    [InlineData("Bilinear")]
    [InlineData("Cubic")]
    [InlineData("CubicSpline")]
    [InlineData("Lanczos")]
    public void BuildRescaleExpression_PreservesSourceScaleSignsAndAlgorithm(string algorithm)
    {
        const string input = "ST_Clip(raster, @clip)";

        var sql = PostgresRasterStore.BuildRescaleExpression(input, algorithm);

        sql.Should().Contain($"FROM (SELECT {input} AS rast OFFSET 0) rescale_source");
        sql.Should().Contain("CASE WHEN ST_ScaleX(rescale_source.rast) < 0 THEN -abs(@pixelW) ELSE abs(@pixelW) END");
        sql.Should().Contain("CASE WHEN ST_ScaleY(rescale_source.rast) > 0 THEN abs(@pixelH) ELSE -abs(@pixelH) END");
        sql.Should().Contain($"'{algorithm}'");
    }

    [UnitTest]
    public void BuildRescaleExpression_RejectsUnknownResamplingAlgorithm()
    {
        var act = () => PostgresRasterStore.BuildRescaleExpression("raster", "NearestNeighbor'); DROP TABLE raster_data;--");

        act.Should().Throw<ArgumentException>();
    }

    [UnitTheory]
    [InlineData("Bilinear")]
    [InlineData("Cubic")]
    [InlineData("CubicSpline")]
    [InlineData("Lanczos")]
    public void RasterProjectionSql_ThreadsValidatedResamplingAlgorithm(string algorithm)
    {
        var transformSql = RasterProjectionSql.TransformIfNeeded("rast", "@outputSrid", algorithm);
        var resizeSql = RasterProjectionSql.ResizePreservingGrid("rast", ("@outputWidth", "@outputHeight"), algorithm);

        transformSql.Should().Contain($"ST_Transform(projection_source.rast, @outputSrid, '{algorithm}')");
        resizeSql.Should().Contain($"@outputWidth, @outputHeight, '{algorithm}')");
    }

    [UnitTest]
    public void RasterProjectionSql_RejectsInvalidResamplingAlgorithm()
    {
        var transformAct = () => RasterProjectionSql.TransformIfNeeded("rast", "@outputSrid", "InvalidAlg");
        var resizeAct = () => RasterProjectionSql.ResizePreservingGrid("rast", ("@w", "@h"), "InvalidAlg");

        transformAct.Should().Throw<ArgumentException>();
        resizeAct.Should().Throw<ArgumentException>();
    }

    [UnitTheory]
    [InlineData("NearestNeighbor")]
    [InlineData("Bilinear")]
    [InlineData("Cubic")]
    [InlineData("CubicSpline")]
    [InlineData("Lanczos")]
    public void RasterMosaicSql_ThreadsValidatedResamplingAlgorithmIntoAlignment(string algorithm)
    {
        var sql = RasterMosaicSql.CreateMosaicAggregateExpression(
            RasterMergeStrategy.Newest,
            new RasterMosaicExpressionOptions
            {
                AlignToSourceCte = "source",
                ResamplingAlgorithm = algorithm
            });

        sql.Should().Contain($"'{algorithm}')");
        sql.Should().Contain("ST_Resample(prepared.rast,");
    }

    [UnitTest]
    public void RasterMosaicSql_RejectsInvalidResamplingAlgorithm()
    {
        var act = () => RasterMosaicSql.CreateAlignedRasterExpression("source", "Bogus");

        act.Should().Throw<ArgumentException>();
    }

    [UnitTest]
    public void BuildClipExtentFrameCtes_RestoresBandNoDataValuesPerBand()
    {
        var clip = new RasterClipRegion { Geometry = [1, 2, 3], Srid = 4326 };

        var sql = PostgresRasterStore.BuildClipExtentFrameCtes("frame_source", clip, 4326, "Bilinear");

        sql.Should().Contain("ST_Transform(s.rast, g.rast, 'Bilinear')");
        sql.Should().Contain("ST_SameAlignment(i.rast, i.grid)");
        sql.Should().Contain("ST_SetBandNoDataValue(ST_Band(c.rast, n), 1, ST_BandNoDataValue(i.rast, n))");
    }

    [UnitTest]
    public void RasterSamplingMigration008_PreservesFrozenNativeWindowFramingForInPlaceUpgrades()
    {
        var boundMigration = RasterSamplingSql.ReadBoundMigration().Replace("\r\n", "\n", StringComparison.Ordinal);

        // Migration 008 is already journaled on upgraded databases and checked verbatim against
        // pg_proc.prosrc by RasterSamplingSql.VerifyFunctionsAsync. Direct callers may use the
        // ST_SameAlignment short-circuit in FrameAlignedRaster, but 008's native_window_v1 must
        // keep FrameAlignedRasterV1 so existing deployments upgrade without schema-floor drift.
        boundMigration.Should().NotContain("ST_SameAlignment(i.rast, i.grid)");
        RasterNativeWindowSql.Definition("p_raster", "p_grid", preserveCoverageMask: false)
            .Should().Contain(RasterGridFrameSql.FrameAlignedRasterV1("w.rast", "w.grid"));
        RasterNativeWindowSql.Definition("p_raster", "p_grid", preserveCoverageMask: true)
            .Should().Contain(RasterGridFrameSql.FrameAlignedRasterV1("w.rast", "w.grid"));
    }

    [UnitTest]
    public void PostgresRasterMapRenderer_BuildRenderSql_FramesBboxAndPreservesLayerOrder()
    {
        var request = new MapRenderRequest
        {
            BoundingBox = [0d, 0d, 2d, 1d],
            BoundingBoxCrs = 4326,
            Crs = 4326,
            Width = 64,
            Height = 32,
            Format = RasterFormat.PNG
        };

        var sql = PostgresRasterMapRenderer.BuildRenderSql("public.raster_data", [20, 10], request);

        sql.CommandText.Should().Contain("unnest(@layerIds) WITH ORDINALITY AS u(layer_id, layer_order)");
        sql.CommandText.Should().Contain("ORDER BY layer_order ASC, effective_acquisition ASC, created_at ASC, id ASC");
        sql.CommandText.Should().Contain("frame_grid AS (");
        sql.CommandText.Should().Contain("ST_SetBandNoDataValue(ST_Band(c.rast, n), 1, ST_BandNoDataValue(i.rast, n))");
        sql.CommandText.Should().Contain("ST_XMin(ST_Envelope(rast)) AS xmin");
        sql.Parameters.Should().Contain(p => p.Name == "@layerIds" && ((int[])p.Value).SequenceEqual(new[] { 20, 10 }));
    }
}
