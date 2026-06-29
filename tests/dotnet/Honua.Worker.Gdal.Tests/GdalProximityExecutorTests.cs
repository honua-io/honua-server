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
/// Fake-runner coverage for <see cref="GdalProximityJobExecutor"/>: the
/// gdal_proximity.py distance argument projection (#2240) plus the
/// nearest-source allocation path that invokes the custom
/// gdal_euclidean_allocation.py worker step via python3 (#2255).
/// </summary>
public sealed class GdalProximityExecutorTests
{
    private const string ScratchSuite = "honua-gdal-proximity-test";

    private static string Base64(string text) => GdalCli.Base64(text);

    [UnitTest]
    public void GdalProximityExecutor_DeclaresNativeRuntimeProfile_AndAdvertisesBothIds()
    {
        var executor = NewExecutor(FakeGdalCommandRunner.Failing(1, "n/a"), out _);

        executor.Kind.Should().Be(ExecutionJobKind.Geoprocessing);
        executor.AcceptedRuntimeProfiles.Should().ContainSingle().Which.Should().Be(RuntimeProfiles.Native);
        GdalProximityJobExecutor.SupportedProcessIds.Should().BeEquivalentTo(new[]
        {
            "proximity.euclidean-distance",
            "proximity.euclidean-allocation",
        });
    }

    [UnitTest]
    public async Task Distance_Default_RunsGdalProximity_AndPublishesGeoTiff()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("dist-tif"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalProximityJobExecutor.DistanceProcessId,
                ("source", Base64("fake-raster")),
                ("maxDistance", "500"),
                ("distUnits", "GEO"),
                ("values", "1,2"));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            context.Artifacts.Single().Should().StartWith("data:image/tiff");

            var invocation = runner.Invocations.Single();
            invocation.Tool.Should().Be("gdal_proximity.py");
            invocation.Arguments.Should().ContainInOrder("-distunits", "GEO");
            invocation.Arguments.Should().ContainInOrder("-maxdist", "500");
            invocation.Arguments.Should().ContainInOrder("-values", "1,2");
            invocation.Arguments[^1].Should().EndWith("output.tif");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Distance_InvalidUnits_FailsBeforeReachingTheCli()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalProximityJobExecutor.DistanceProcessId,
                ("source", Base64("fake-raster")),
                ("distUnits", "MILES"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("distUnits");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Allocation_Default_RunsPythonStep_AndPublishesGeoTiff()
    {
        var runner = SucceedingWritingOutputTif(Encoding.UTF8.GetBytes("alloc-tif"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalProximityJobExecutor.AllocationProcessId,
                ("source", Base64("fake-raster")));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            context.Artifacts.Single().Should().StartWith("data:image/tiff");

            var invocation = runner.Invocations.Single();
            invocation.Tool.Should().Be("python3");
            invocation.Arguments[0].Should().EndWith(GdalProximityJobExecutor.AllocationScriptName);
            invocation.Arguments[1].Should().EndWith("input.tif");
            invocation.Arguments[2].Should().EndWith("output.tif");
            invocation.Arguments.Should().ContainInOrder("--dist-units", "GEO");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Allocation_WithOptions_ProjectsScriptFlags()
    {
        var runner = SucceedingWritingOutputTif(Encoding.UTF8.GetBytes("alloc-tif"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalProximityJobExecutor.AllocationProcessId,
                ("source", Base64("fake-raster")),
                ("maxDistance", "750"),
                ("distUnits", "PIXEL"),
                ("values", "3,7"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            var invocation = runner.Invocations.Single();
            invocation.Tool.Should().Be("python3");
            invocation.Arguments.Should().ContainInOrder("--dist-units", "PIXEL");
            invocation.Arguments.Should().ContainInOrder("--max-distance", "750");
            invocation.Arguments.Should().ContainInOrder("--values", "3,7");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Allocation_InvalidUnits_FailsBeforeReachingTheCli()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalProximityJobExecutor.AllocationProcessId,
                ("source", Base64("fake-raster")),
                ("distUnits", "MILES"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("distUnits");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    /// <summary>
    /// The allocation step puts the output path positionally before its optional
    /// flags, so this fake locates the <c>output.tif</c> argument (rather than a
    /// fixed offset) and writes the payload there so the read-back path runs.
    /// </summary>
    private static FakeGdalCommandRunner SucceedingWritingOutputTif(byte[] outputBytes)
        => new((_, args, _) =>
        {
            var outputPath = args.First(a => a.EndsWith("output.tif", StringComparison.Ordinal));
            File.WriteAllBytes(outputPath, outputBytes);
            return new GdalCommandResult { ExitCode = 0 };
        });

    private static GdalProximityJobExecutor NewExecutor(IGdalCommandRunner runner, out string scratch)
    {
        scratch = GdalCli.NewScratch(ScratchSuite);
        return new GdalProximityJobExecutor(
            runner, GdalJobFactory.Options(scratch), NullLogger<GdalProximityJobExecutor>.Instance);
    }

    private static void CleanupScratch(string scratch) => GdalCli.CleanupScratch(scratch);
}
