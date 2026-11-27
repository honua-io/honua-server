// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Worker.Gdal.Tests;

public sealed class GdalRasterReprojectGuardTests
{
    private const string ScratchSuite = "honua-gdal-reproject-guard-test";

    [UnitTest]
    public async Task RasterReproject_PolarGeographicToWebMercator_RejectsBeforeInvokingGdalwarp()
    {
        var runner = new FakeGdalCommandRunner((_, _, _) =>
            throw new InvalidOperationException("gdalwarp must not run for an unbounded polar WebMercator warp"));
        var scratch = GdalCli.NewScratch(ScratchSuite);
        try
        {
            var executor = new GdalRasterReprojectJobExecutor(
                runner, GdalJobFactory.Options(scratch), NullLogger<GdalRasterReprojectJobExecutor>.Instance);
            var job = BuildReprojectJob(PolarGeographicTiff());

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("WebMercator").And.Contain("unbounded");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            GdalCli.CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task RasterReproject_MidLatitudeGeographicToWebMercator_RunsGdalwarp()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("warped-tif"));
        var scratch = GdalCli.NewScratch(ScratchSuite);
        try
        {
            var executor = new GdalRasterReprojectJobExecutor(
                runner, GdalJobFactory.Options(scratch), NullLogger<GdalRasterReprojectJobExecutor>.Instance);
            var job = BuildReprojectJob(MidLatitudeGeographicTiff());

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            runner.Invocations.Should().ContainSingle().Which.Tool.Should().Be("gdalwarp");
        }
        finally
        {
            GdalCli.CleanupScratch(scratch);
        }
    }

    private static ExecutionJobRecord BuildReprojectJob(byte[] tiffBytes)
        => GdalJobFactory.Job(
            GdalRasterReprojectJobExecutor.HandledProcessId,
            ("source", Convert.ToBase64String(tiffBytes)),
            ("sourceSrs", "EPSG:4326"),
            ("targetSrs", "EPSG:3857"));

    private static byte[] PolarGeographicTiff()
        => TiffHeaderBuilder.ClassicGeoTiff(new TiffHeaderBuilder.GeoTiffHeaderSpec
        {
            Width = 100,
            Height = 100,
            OriginX = -10,
            OriginY = 90,
            ScaleX = 0.1,
            ScaleY = 0.1,
            EpsgCode = 4326,
        });

    private static byte[] MidLatitudeGeographicTiff()
        => TiffHeaderBuilder.ClassicGeoTiff(new TiffHeaderBuilder.GeoTiffHeaderSpec
        {
            Width = 128,
            Height = 128,
            OriginX = -122.5,
            OriginY = 38.0,
            ScaleX = 0.001,
            ScaleY = 0.001,
            EpsgCode = 4326,
        });
}
