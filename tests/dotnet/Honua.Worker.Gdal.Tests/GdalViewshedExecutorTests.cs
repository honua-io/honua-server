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
/// Fake-runner coverage for <see cref="GdalViewshedJobExecutor"/>: gdal_viewshed
/// observer/distance argument projection and the GeoTIFF artifact contract (#2240).
/// </summary>
public sealed class GdalViewshedExecutorTests
{
    private const string ScratchSuite = "honua-gdal-viewshed-test";

    private static string Base64(string text) => GdalCli.Base64(text);

    [UnitTest]
    public void GdalViewshedExecutor_DeclaresNativeRuntimeProfile()
    {
        var executor = NewExecutor(FakeGdalCommandRunner.Failing(1, "n/a"), out _);

        executor.Kind.Should().Be(ExecutionJobKind.Geoprocessing);
        executor.AcceptedRuntimeProfiles.Should().ContainSingle().Which.Should().Be(RuntimeProfiles.Native);
        executor.ProcessIds.Should().ContainSingle().Which.Should().Be("surface.viewshed");
    }

    [UnitTest]
    public async Task Viewshed_Default_RunsGdalViewshed_AndPublishesGeoTiff()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("viewshed"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalViewshedJobExecutor.HandledProcessId,
                ("source", Base64("fake-dem")),
                ("observerX", "100.5"),
                ("observerY", "200.25"),
                ("observerHeight", "1.8"),
                ("maxDistance", "5000"));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            context.Artifacts.Single().Should().StartWith("data:image/tiff");

            var invocation = runner.Invocations.Single();
            invocation.Tool.Should().Be("gdal_viewshed");
            invocation.Arguments.Should().ContainInOrder("-ox", "100.5");
            invocation.Arguments.Should().ContainInOrder("-oy", "200.25");
            invocation.Arguments.Should().ContainInOrder("-oz", "1.8");
            invocation.Arguments.Should().ContainInOrder("-md", "5000");
            invocation.Arguments[^1].Should().EndWith("output.tif");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Viewshed_MissingObserver_Fails()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalViewshedJobExecutor.HandledProcessId,
                ("source", Base64("fake-dem")),
                ("observerX", "100"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("observerY");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    private static GdalViewshedJobExecutor NewExecutor(IGdalCommandRunner runner, out string scratch)
    {
        scratch = GdalCli.NewScratch(ScratchSuite);
        return new GdalViewshedJobExecutor(
            runner, GdalJobFactory.Options(scratch), NullLogger<GdalViewshedJobExecutor>.Instance);
    }

    private static void CleanupScratch(string scratch) => GdalCli.CleanupScratch(scratch);
}
