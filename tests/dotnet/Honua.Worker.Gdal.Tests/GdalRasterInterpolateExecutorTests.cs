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
/// gdal_grid IDW argument projection plus the flagged Kriging path that fails fast
/// with a clear unsupported-dependency message (no silent stub, #2141).
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
            invocation.Arguments.Should().ContainInOrder("-a", "invdist:power=2:smoothing=0");
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
            args.Should().ContainInOrder("-a", "invdist:power=3:smoothing=0.5:radius=100");
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
    public async Task Kriging_IsFlaggedUnsupported_FailsFastWithClearMessage_AndNeverRunsCli()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "should-not-run");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            // Even with valid inputs, kriging must fail fast with the unsupported
            // message rather than silently substituting another algorithm.
            var job = GdalJobFactory.Job(
                GdalRasterInterpolateJobExecutor.KrigingProcessId,
                ("points", Base64("points")),
                ("zField", "elevation"));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Be(GdalRasterInterpolateJobExecutor.KrigingUnsupportedMessage);
            result.ErrorMessage.Should().Contain("not available in this build");
            runner.Invocations.Should().BeEmpty();
            context.Artifacts.Should().BeEmpty();
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
