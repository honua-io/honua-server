// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Worker.Gdal.Tests;

/// <summary>
/// Fake-runner coverage for <see cref="GdalRasterResampleJobExecutor"/>: gdalwarp
/// -tr argument projection, resampling enum mapping, cell-size guards, and the
/// canonical data-URI artifact contract.
/// </summary>
public sealed class GdalRasterResampleExecutorTests
{
    private const string ScratchSuite = "honua-gdal-resample-test";

    private static string Base64(string text) => GdalCli.Base64(text);

    [UnitTest]
    public void GdalRasterResampleExecutor_DeclaresNativeRuntimeProfile()
    {
        var executor = NewExecutor(FakeGdalCommandRunner.Failing(1, "n/a"), out _);

        executor.Kind.Should().Be(ExecutionJobKind.Geoprocessing);
        executor.AcceptedRuntimeProfiles.Should().ContainSingle().Which.Should().Be(RuntimeProfiles.Native);
        executor.ProcessIds.Should().ContainSingle().Which.Should().Be("raster.resample");
    }

    [UnitTest]
    public async Task Resample_Default_RunsGdalwarpWithTargetResolution_AndPublishesGeoTiff()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("resampled-tif"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterResampleJobExecutor.HandledProcessId,
                ("source", Base64("fake-raster")),
                ("cellSize", "30"));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            context.Artifacts.Should().ContainSingle();
            context.Artifacts[0].Should().StartWith("data:image/tiff");

            var invocation = runner.Invocations.Single();
            invocation.Tool.Should().Be("gdalwarp");
            invocation.Arguments.Should().ContainInOrder("-tr", "30", "30");
            invocation.Arguments.Should().ContainInOrder("-r", "bilinear");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Resample_DistinctCellSizeY_PassesBothAxes()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("ok"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterResampleJobExecutor.HandledProcessId,
                ("source", Base64("fake-raster")),
                ("cellSize", "30"),
                ("cellSizeY", "20"),
                ("resampling", "nearestneighbor"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            var args = runner.Invocations.Single().Arguments;
            args.Should().ContainInOrder("-tr", "30", "20");
            args.Should().ContainInOrder("-r", "near");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Resample_MissingCellSize_FailsBeforeReachingTheCli()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterResampleJobExecutor.HandledProcessId,
                ("source", Base64("fake-raster")));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("cellSize");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Resample_UnknownResampling_FailsBeforeReachingTheCli()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterResampleJobExecutor.HandledProcessId,
                ("source", Base64("fake-raster")),
                ("cellSize", "30"),
                ("resampling", "spline"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("resampling");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Resample_ToolFailure_IsReportedAsJobFailure()
    {
        var runner = FakeGdalCommandRunner.Failing(2, "ERROR: warp failed");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterResampleJobExecutor.HandledProcessId,
                ("source", Base64("fake-raster")),
                ("cellSize", "30"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("gdalwarp exited with code 2");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Resample_TinyCellSizeOverInputExtent_FailsBeforeReachingTheCli()
    {
        // A modest input raster (1000×1000 px @ 30-unit ModelPixelScale → 30 000-unit
        // ground extent) resampled to a 0.1-unit cell would derive a 300 000 px output
        // grid — past the width cap. The -tr output must be refused before gdalwarp runs.
        var runner = new FakeGdalCommandRunner((_, _, _) =>
            throw new InvalidOperationException("gdalwarp must not run for an over-cap -tr output grid"));
        var scratch = GdalCli.NewScratch(ScratchSuite);
        var executor = new GdalRasterResampleJobExecutor(
            runner,
            GdalJobFactory.Options(scratch, maxRasterWidth: 4096, maxRasterHeight: 4096),
            NullLogger<GdalRasterResampleJobExecutor>.Instance);
        try
        {
            var tiff = TiffHeaderBuilder.ClassicWithPixelScale(width: 1000, height: 1000, scaleX: 30.0, scaleY: 30.0);
            var job = GdalJobFactory.Job(
                GdalRasterResampleJobExecutor.HandledProcessId,
                ("source", Convert.ToBase64String(tiff)),
                ("cellSize", "0.1"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("output grid").And.Contain("exceeds configured");
            runner.Invocations.Should().BeEmpty("the over-cap -tr output grid must be refused before gdalwarp runs");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Resample_CellSizeYieldingBoundedGrid_RunsGdalwarp()
    {
        // 1000×1000 px @ 30-unit scale → 30 000-unit extent; a 30-unit target cell derives
        // a 1000×1000 output grid — within caps, so the legitimate resample proceeds.
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("resampled"));
        var scratch = GdalCli.NewScratch(ScratchSuite);
        var executor = new GdalRasterResampleJobExecutor(
            runner,
            GdalJobFactory.Options(scratch, maxRasterWidth: 4096, maxRasterHeight: 4096),
            NullLogger<GdalRasterResampleJobExecutor>.Instance);
        try
        {
            var tiff = TiffHeaderBuilder.ClassicWithPixelScale(width: 1000, height: 1000, scaleX: 30.0, scaleY: 30.0);
            var job = GdalJobFactory.Job(
                GdalRasterResampleJobExecutor.HandledProcessId,
                ("source", Convert.ToBase64String(tiff)),
                ("cellSize", "30"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            runner.Invocations.Single().Arguments.Should().ContainInOrder("-tr", "30", "30");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    private static GdalRasterResampleJobExecutor NewExecutor(IGdalCommandRunner runner, out string scratch)
    {
        scratch = GdalCli.NewScratch(ScratchSuite);
        return new GdalRasterResampleJobExecutor(
            runner, GdalJobFactory.Options(scratch), NullLogger<GdalRasterResampleJobExecutor>.Instance);
    }

    private static void CleanupScratch(string scratch) => GdalCli.CleanupScratch(scratch);
}
