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
/// gdal_proximity.py distance argument projection plus the flagged allocation path
/// that fails fast with a clear unsupported-dependency message (#2240).
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
    public async Task Allocation_IsFlaggedUnsupported_AndFailsFast()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalProximityJobExecutor.AllocationProcessId,
                ("source", Base64("fake-raster")));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Be(GdalProximityJobExecutor.AllocationUnsupportedMessage);
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    private static GdalProximityJobExecutor NewExecutor(IGdalCommandRunner runner, out string scratch)
    {
        scratch = GdalCli.NewScratch(ScratchSuite);
        return new GdalProximityJobExecutor(
            runner, GdalJobFactory.Options(scratch), NullLogger<GdalProximityJobExecutor>.Instance);
    }

    private static void CleanupScratch(string scratch) => GdalCli.CleanupScratch(scratch);
}
