// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Xunit;

namespace Honua.Worker.Gdal.Tests;

/// <summary>
/// Executor-level coverage that the pixel-dimension / zone-count admission control
/// (#2766) rejects a decompression-bomb raster and an oversized zone set BEFORE any
/// GDAL CLI runs (asserted via an empty <c>runner.Invocations</c> — the reject
/// happens from the cheap header read, not after a full-raster allocation), and
/// that a normal in-bounds raster + normal zone set still succeed (regression).
/// </summary>
public sealed class GdalRasterAdmissionExecutorTests
{
    private const string ScratchSuite = "honua-gdal-admission-test";

    private static readonly GeometryFactory Factory = new();

    // --- raster pixel-dimension admission (map-algebra path) --------------------

    [UnitTest]
    public async Task MapAlgebra_RasterOverPixelCap_RejectedBeforeCli()
    {
        // A tiny (~60-byte) GeoTIFF header that DECLARES a 1,000,000×1,000,000
        // raster: the decompression-bomb shape. Admission must reject it from the
        // header alone, before gdal_calc.py (or the NoData gdalinfo) ever runs.
        var bomb = Convert.ToBase64String(TiffHeaderBuilder.Classic(1_000_000, 1_000_000));
        var runner = FakeGdalCommandRunner.Failing(1, "must-not-run");
        var scratch = GdalCli.NewScratch(ScratchSuite);
        var executor = new GdalRasterMapAlgebraJobExecutor(
            runner, GdalJobFactory.Options(scratch), NullLogger<GdalRasterMapAlgebraJobExecutor>.Instance);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterMapAlgebraJobExecutor.HandledProcessId,
                ("sources", bomb),
                ("expression", "A*2"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("exceeds configured");
            runner.Invocations.Should().BeEmpty("admission must reject before any GDAL tool is spawned");
        }
        finally
        {
            GdalCli.CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task MapAlgebra_RasterOverBandCap_RejectedBeforeCli()
    {
        var overBands = Convert.ToBase64String(TiffHeaderBuilder.Classic(16, 16, bands: 4096));
        var runner = FakeGdalCommandRunner.Failing(1, "must-not-run");
        var scratch = GdalCli.NewScratch(ScratchSuite);
        var executor = new GdalRasterMapAlgebraJobExecutor(
            runner, GdalJobFactory.Options(scratch), NullLogger<GdalRasterMapAlgebraJobExecutor>.Instance);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterMapAlgebraJobExecutor.HandledProcessId,
                ("sources", overBands),
                ("expression", "A*2"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("MaxRasterBands");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            GdalCli.CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task MapAlgebra_NormalRaster_Succeeds()
    {
        var normal = Convert.ToBase64String(TiffHeaderBuilder.Classic(512, 512, bands: 3));
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("calc-tif"));
        var scratch = GdalCli.NewScratch(ScratchSuite);
        var executor = new GdalRasterMapAlgebraJobExecutor(
            runner, GdalJobFactory.Options(scratch), NullLogger<GdalRasterMapAlgebraJobExecutor>.Instance);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterMapAlgebraJobExecutor.HandledProcessId,
                ("sources", normal),
                ("expression", "A*2"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            runner.Invocations.Should().Contain(i => i.Tool == "gdal_calc.py");
        }
        finally
        {
            GdalCli.CleanupScratch(scratch);
        }
    }

    // --- zonal-statistics zone-count admission ---------------------------------

    [UnitTest]
    public async Task Zonal_ZoneCountOverCap_RejectedBeforeCli()
    {
        var zones = PolygonZonesBase64(count: 4);
        var runner = FakeGdalCommandRunner.Failing(1, "must-not-run");
        var scratch = GdalCli.NewScratch(ScratchSuite);
        var executor = new GdalRasterZonalStatisticsJobExecutor(
            runner,
            GdalJobFactory.Options(scratch, maxZoneCount: 2),
            NullLogger<GdalRasterZonalStatisticsJobExecutor>.Instance);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterZonalStatisticsJobExecutor.HandledProcessId,
                ("source", Convert.ToBase64String(TiffHeaderBuilder.Classic(64, 64))),
                ("zones", zones));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("MaxZoneCount");
            runner.Invocations.Should().BeEmpty("the per-zone gdalwarp/gdalinfo loop must never start");
        }
        finally
        {
            GdalCli.CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Zonal_NormalZones_Succeeds()
    {
        var zones = PolygonZonesBase64(count: 2);
        const string StatsJson =
            "{\"bands\":[{\"band\":1,\"minimum\":0,\"maximum\":10,\"mean\":5,\"stdDev\":2,\"validCount\":100}]}";

        var runner = new FakeGdalCommandRunner((tool, args, _) =>
        {
            if (tool == "gdalwarp")
            {
                // gdalwarp writes the clipped raster as its last argument.
                File.WriteAllBytes(args[^1], Encoding.UTF8.GetBytes("clipped"));
                return new GdalCommandResult { ExitCode = 0 };
            }

            // gdalinfo -stats returns the per-band JSON the executor projects.
            return new GdalCommandResult { ExitCode = 0, StandardOutput = StatsJson };
        });

        var scratch = GdalCli.NewScratch(ScratchSuite);
        var executor = new GdalRasterZonalStatisticsJobExecutor(
            runner, GdalJobFactory.Options(scratch), NullLogger<GdalRasterZonalStatisticsJobExecutor>.Instance);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterZonalStatisticsJobExecutor.HandledProcessId,
                ("source", Convert.ToBase64String(TiffHeaderBuilder.Classic(64, 64))),
                ("zones", zones));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
        }
        finally
        {
            GdalCli.CleanupScratch(scratch);
        }
    }

    private static string PolygonZonesBase64(int count)
    {
        var fc = new FeatureCollection();
        for (var i = 0; i < count; i++)
        {
            var x = i * 2.0;
            var ring = Factory.CreateLinearRing(
            [
                new Coordinate(x, 0),
                new Coordinate(x + 1, 0),
                new Coordinate(x + 1, 1),
                new Coordinate(x, 1),
                new Coordinate(x, 0),
            ]);
            var polygon = Factory.CreatePolygon(ring);
            var attrs = new AttributesTable();
            attrs.Add("id", i);
            fc.Add(new Feature(polygon, attrs));
        }

        var geoJson = new GeoJsonWriter().Write(fc);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(geoJson));
    }
}
