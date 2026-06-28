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
/// Fake-runner coverage for <see cref="GdalContourJobExecutor"/>: gdal_contour
/// interval/base argument projection and the GeoJSON vector artifact contract (#2240).
/// </summary>
public sealed class GdalContourExecutorTests
{
    private const string ScratchSuite = "honua-gdal-contour-test";

    private static string Base64(string text) => GdalCli.Base64(text);

    [UnitTest]
    public void GdalContourExecutor_DeclaresNativeRuntimeProfile()
    {
        var executor = NewExecutor(FakeGdalCommandRunner.Failing(1, "n/a"), out _);

        executor.Kind.Should().Be(ExecutionJobKind.Geoprocessing);
        executor.AcceptedRuntimeProfiles.Should().ContainSingle().Which.Should().Be(RuntimeProfiles.Native);
        executor.ProcessIds.Should().ContainSingle().Which.Should().Be("surface.contour");
    }

    [UnitTest]
    public async Task Contour_Default_RunsGdalContour_AndPublishesGeoJson()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("{\"type\":\"FeatureCollection\"}"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalContourJobExecutor.HandledProcessId,
                ("source", Base64("fake-dem")),
                ("interval", "10"),
                ("base", "5"));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            context.Artifacts.Single().Should().StartWith("data:application/geo+json");

            var invocation = runner.Invocations.Single();
            invocation.Tool.Should().Be("gdal_contour");
            invocation.Arguments.Should().ContainInOrder("-a", "ELEV");
            invocation.Arguments.Should().ContainInOrder("-i", "10");
            invocation.Arguments.Should().ContainInOrder("-off", "5");
            invocation.Arguments.Should().ContainInOrder("-f", "GeoJSON");
            invocation.Arguments[^1].Should().EndWith("output.geojson");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Contour_MissingInterval_Fails()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalContourJobExecutor.HandledProcessId,
                ("source", Base64("fake-dem")));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("interval");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Contour_NonPositiveInterval_Fails()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalContourJobExecutor.HandledProcessId,
                ("source", Base64("fake-dem")),
                ("interval", "0"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("interval");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    private static GdalContourJobExecutor NewExecutor(IGdalCommandRunner runner, out string scratch)
    {
        scratch = GdalCli.NewScratch(ScratchSuite);
        return new GdalContourJobExecutor(
            runner, GdalJobFactory.Options(scratch), NullLogger<GdalContourJobExecutor>.Instance);
    }

    private static void CleanupScratch(string scratch) => GdalCli.CleanupScratch(scratch);
}
