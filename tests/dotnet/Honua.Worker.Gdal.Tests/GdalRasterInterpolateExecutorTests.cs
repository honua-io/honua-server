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
/// Fake-runner coverage for <see cref="GdalRasterInterpolateJobExecutor"/>: the
/// gdal_grid IDW argument projection plus the kriging routing, which never reaches
/// gdal_grid (stock GDAL has no kriging algorithm) and instead invokes the bundled
/// NumPy solver script (#3932). Burned-value correctness lives in
/// <c>RasterExecutionProofTests.Kriging.cs</c> against real GDAL.
/// </summary>
public sealed class GdalRasterInterpolateExecutorTests
{
    private const string ScratchSuite = "honua-gdal-interpolate-test";

    private static string Base64(string text) => GdalCli.Base64(text);

    [UnitTest]
    public void GdalRasterInterpolateExecutor_DeclaresNativeRuntimeProfile_AndAdvertisesBothIds()
    {
        var executor = NewExecutor(FakeGdalCommandRunner.Failing(1, "n/a"), out _);

        executor.Kind.Should().Be(ExecutionJobKind.Geoprocessing);
        executor.AcceptedRuntimeProfiles.Should().ContainSingle().Which.Should().Be(RuntimeProfiles.Native);
        GdalRasterInterpolateJobExecutor.SupportedProcessIds.Should().BeEquivalentTo(new[]
        {
            "raster.interpolate-idw",
            "raster.interpolate-kriging",
        });
    }

    [UnitTest]
    public async Task Idw_Default_RunsGdalGridInvdist_AndPublishesGeoTiff()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("idw-tif"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterInterpolateJobExecutor.IdwProcessId,
                ("points", Base64("{\"type\":\"FeatureCollection\",\"features\":[]}")),
                ("zField", "elevation"));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            context.Artifacts.Should().ContainSingle();
            context.Artifacts[0].Should().StartWith("data:image/tiff");

            var invocation = runner.Invocations.Single();
            invocation.Tool.Should().Be("gdal_grid");
            invocation.Arguments.Should().ContainInOrder("-a", "invdist:power=2:smoothing=0:nodata=nan");
            invocation.Arguments.Should().ContainInOrder("-zfield", "elevation");
            invocation.Arguments.Should().Contain("-l").And.Contain("points");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Idw_WithTuningAndOutsize_ProjectsAllArguments()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("ok"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterInterpolateJobExecutor.IdwProcessId,
                ("points", Base64("points")),
                ("power", "3"),
                ("smoothing", "0.5"),
                ("radius", "100"),
                ("width", "256"),
                ("height", "128"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            var args = runner.Invocations.Single().Arguments;
            args.Should().ContainInOrder("-a", "invdist:power=3:smoothing=0.5:nodata=nan:radius=100");
            args.Should().ContainInOrder("-outsize", "256", "128");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Idw_WidthWithoutHeight_FailsBeforeReachingTheCli()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterInterpolateJobExecutor.IdwProcessId,
                ("points", Base64("points")),
                ("width", "256"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("width").And.Contain("height");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Idw_InvalidZField_FailsBeforeReachingTheCli()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterInterpolateJobExecutor.IdwProcessId,
                ("points", Base64("points")),
                ("zField", "elev; rm -rf /"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("zField");
            runner.Invocations.Should().BeEmpty("an invalid attribute name must never reach the CLI");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Idw_MissingPoints_FailsWithClearMessage()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(GdalRasterInterpolateJobExecutor.IdwProcessId);

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("points");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Kriging_RoutesToTheBundledNumPySolver_AndNeverToGdalGrid()
    {
        var runner = new FakeGdalCommandRunner((_, args, _) =>
        {
            var outputIndex = Array.IndexOf(args.ToArray(), "--output");
            outputIndex.Should().BeGreaterThanOrEqualTo(0);
            File.WriteAllBytes(args[outputIndex + 1], Encoding.UTF8.GetBytes("kriged-tif"));
            return new GdalCommandResult { ExitCode = 0 };
        });
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterInterpolateJobExecutor.KrigingProcessId,
                ("points", Base64("points")),
                ("zField", "elevation"),
                ("width", "8"),
                ("height", "6"),
                ("variogramModel", "exponential"),
                ("nugget", "0.25"),
                ("sill", "2"),
                ("range", "5"));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            context.Artifacts.Should().ContainSingle();
            context.Artifacts[0].Should().StartWith("data:image/tiff");

            var invocation = runner.Invocations.Single();
            invocation.Tool.Should().Be("python3", "stock gdal_grid has no kriging algorithm");
            invocation.Arguments[0].Should().EndWith(GdalRasterInterpolateJobExecutor.KrigingScript);
            invocation.Arguments.Should().ContainInOrder("--model", "exponential");
            invocation.Arguments.Should().ContainInOrder("--nugget", "0.25");
            invocation.Arguments.Should().ContainInOrder("--sill", "2");
            invocation.Arguments.Should().ContainInOrder("--range", "5");
            invocation.Arguments.Should().ContainInOrder("--width", "8");
            invocation.Arguments.Should().ContainInOrder("--height", "6");
            invocation.Arguments.Should().ContainInOrder("--z-field", "elevation");
            invocation.Arguments.Should().ContainInOrder("--max-samples", "2000");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Kriging_WithoutAGridOrARange_IsRejectedBeforeTheSolverRuns()
    {
        var runner = new FakeGdalCommandRunner((_, _, _) =>
            throw new InvalidOperationException("the kriging solver must not run for an incomplete request"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            // Kriging solves per target cell, so there is no implicit default grid,
            // and no defensible default correlation length.
            var noGrid = GdalJobFactory.Job(
                GdalRasterInterpolateJobExecutor.KrigingProcessId,
                ("points", Base64("points")),
                ("range", "5"));
            var noGridResult = await executor.ExecuteAsync(
                noGrid, new RecordingJobExecutionContext(noGrid.OperationId), default);
            noGridResult.Status.Should().Be(ExecutionJobStatus.Failed);
            noGridResult.ErrorMessage.Should().Contain("'width' and 'height' are required");

            var noRange = GdalJobFactory.Job(
                GdalRasterInterpolateJobExecutor.KrigingProcessId,
                ("points", Base64("points")),
                ("width", "8"),
                ("height", "6"));
            var noRangeResult = await executor.ExecuteAsync(
                noRange, new RecordingJobExecutionContext(noRange.OperationId), default);
            noRangeResult.Status.Should().Be(ExecutionJobStatus.Failed);
            noRangeResult.ErrorMessage.Should().Contain("'range' is required");

            var illPosed = GdalJobFactory.Job(
                GdalRasterInterpolateJobExecutor.KrigingProcessId,
                ("points", Base64("points")),
                ("width", "8"),
                ("height", "6"),
                ("range", "5"),
                ("nugget", "2"),
                ("sill", "2"));
            var illPosedResult = await executor.ExecuteAsync(
                illPosed, new RecordingJobExecutionContext(illPosed.OperationId), default);
            illPosedResult.Status.Should().Be(ExecutionJobStatus.Failed);
            illPosedResult.ErrorMessage.Should().Contain("nugget < sill");

            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Idw_OverCapOutputSize_FailsBeforeReachingTheCli()
    {
        // A tiny point input requesting an enormous -outsize grid must be refused before
        // gdal_grid allocates the width×height surface (#2782).
        var runner = new FakeGdalCommandRunner((_, _, _) =>
            throw new InvalidOperationException("gdal_grid must not run for an over-cap output grid"));
        var scratch = GdalCli.NewScratch(ScratchSuite);
        var executor = new GdalRasterInterpolateJobExecutor(
            runner,
            GdalJobFactory.Options(scratch, maxRasterWidth: 4096, maxRasterHeight: 4096, maxRasterPixels: 4096L * 4096L),
            NullLogger<GdalRasterInterpolateJobExecutor>.Instance);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterInterpolateJobExecutor.IdwProcessId,
                ("points", Base64("points")),
                ("width", "1000000"),
                ("height", "1000000"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("output grid").And.Contain("exceeds configured");
            runner.Invocations.Should().BeEmpty("the over-cap output grid must be refused before gdal_grid runs");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    private static GdalRasterInterpolateJobExecutor NewExecutor(IGdalCommandRunner runner, out string scratch)
    {
        scratch = GdalCli.NewScratch(ScratchSuite);
        return new GdalRasterInterpolateJobExecutor(
            runner, GdalJobFactory.Options(scratch), NullLogger<GdalRasterInterpolateJobExecutor>.Instance);
    }

    private static void CleanupScratch(string scratch) => GdalCli.CleanupScratch(scratch);
}
