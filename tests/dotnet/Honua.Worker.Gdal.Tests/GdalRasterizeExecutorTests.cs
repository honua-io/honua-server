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
/// Fake-runner coverage for <see cref="GdalRasterizeJobExecutor"/>: gdal_rasterize
/// burn/grid argument projection, the burn/grid XOR guards, and the GeoTIFF artifact
/// contract (#2240).
/// </summary>
public sealed class GdalRasterizeExecutorTests
{
    private const string ScratchSuite = "honua-gdal-rasterize-test";

    private static string Base64(string text) => GdalCli.Base64(text);

    [UnitTest]
    public void GdalRasterizeExecutor_DeclaresNativeRuntimeProfile()
    {
        var executor = NewExecutor(FakeGdalCommandRunner.Failing(1, "n/a"), out _);

        executor.Kind.Should().Be(ExecutionJobKind.Geoprocessing);
        executor.AcceptedRuntimeProfiles.Should().ContainSingle().Which.Should().Be(RuntimeProfiles.Native);
        executor.ProcessIds.Should().ContainSingle().Which.Should().Be("conversion.rasterize");
    }

    [UnitTest]
    public async Task Rasterize_BurnValueAndCellSize_ProjectsArguments_AndPublishesGeoTiff()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("rasterized"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterizeJobExecutor.HandledProcessId,
                ("source", Base64("{\"type\":\"FeatureCollection\",\"features\":[]}")),
                ("burnValue", "1"),
                ("cellSize", "0.001"));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            context.Artifacts.Single().Should().StartWith("data:image/tiff");

            var invocation = runner.Invocations.Single();
            invocation.Tool.Should().Be("gdal_rasterize");
            invocation.Arguments.Should().ContainInOrder("-burn", "1");
            invocation.Arguments.Should().ContainInOrder("-tr", "0.001", "0.001");
            invocation.Arguments[^1].Should().EndWith("output.tif");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Rasterize_AttributeAndSize_ProjectsArguments()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("ok"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterizeJobExecutor.HandledProcessId,
                ("source", Base64("{\"type\":\"FeatureCollection\",\"features\":[]}")),
                ("attribute", "pop"),
                ("width", "256"),
                ("height", "128"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            var args = runner.Invocations.Single().Arguments;
            args.Should().ContainInOrder("-a", "pop");
            args.Should().ContainInOrder("-ts", "256", "128");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Rasterize_BothBurnAndAttribute_Fails()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterizeJobExecutor.HandledProcessId,
                ("source", Base64("fc")),
                ("burnValue", "1"),
                ("attribute", "pop"),
                ("cellSize", "1"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("exactly one");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Rasterize_NoGrid_Fails()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterizeJobExecutor.HandledProcessId,
                ("source", Base64("fc")),
                ("burnValue", "1"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("grid");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Rasterize_OverCapOutputSize_FailsBeforeReachingTheCli()
    {
        // A tiny vector input requesting an enormous output grid must be refused before
        // gdal_rasterize allocates the width×height raster (#2782).
        var runner = new FakeGdalCommandRunner((_, _, _) =>
            throw new InvalidOperationException("gdal_rasterize must not run for an over-cap output grid"));
        var scratch = GdalCli.NewScratch(ScratchSuite);
        var executor = new GdalRasterizeJobExecutor(
            runner,
            GdalJobFactory.Options(scratch, maxRasterWidth: 4096, maxRasterHeight: 4096, maxRasterPixels: 4096L * 4096L),
            NullLogger<GdalRasterizeJobExecutor>.Instance);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterizeJobExecutor.HandledProcessId,
                ("source", Base64("{\"type\":\"FeatureCollection\",\"features\":[]}")),
                ("burnValue", "1"),
                ("width", "1000000"),
                ("height", "1000000"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("output grid").And.Contain("exceeds configured");
            runner.Invocations.Should().BeEmpty("the over-cap output grid must be refused before gdal_rasterize runs");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    private static GdalRasterizeJobExecutor NewExecutor(IGdalCommandRunner runner, out string scratch)
    {
        scratch = GdalCli.NewScratch(ScratchSuite);
        return new GdalRasterizeJobExecutor(
            runner, GdalJobFactory.Options(scratch), NullLogger<GdalRasterizeJobExecutor>.Instance);
    }

    private static void CleanupScratch(string scratch) => GdalCli.CleanupScratch(scratch);
}
