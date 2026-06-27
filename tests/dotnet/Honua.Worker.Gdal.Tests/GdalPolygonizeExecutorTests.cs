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
/// Fake-runner coverage for <see cref="GdalPolygonizeJobExecutor"/>: gdal_polygonize.py
/// band/connectedness/field argument projection and the GeoJSON vector artifact
/// contract (#2240).
/// </summary>
public sealed class GdalPolygonizeExecutorTests
{
    private const string ScratchSuite = "honua-gdal-polygonize-test";

    private static string Base64(string text) => GdalCli.Base64(text);

    // gdal_polygonize.py positional layout is "raster out_file layer fieldname", so
    // the output path is NOT the last argument; write to whichever arg names it.
    private static FakeGdalCommandRunner SucceedingGeoJson(byte[] bytes)
        => new((_, args, _) =>
        {
            var outputPath = args.First(a => a.EndsWith("output.geojson", StringComparison.Ordinal));
            File.WriteAllBytes(outputPath, bytes);
            return new GdalCommandResult { ExitCode = 0 };
        });

    [UnitTest]
    public void GdalPolygonizeExecutor_DeclaresNativeRuntimeProfile()
    {
        var executor = NewExecutor(FakeGdalCommandRunner.Failing(1, "n/a"), out _);

        executor.Kind.Should().Be(ExecutionJobKind.Geoprocessing);
        executor.AcceptedRuntimeProfiles.Should().ContainSingle().Which.Should().Be(RuntimeProfiles.Native);
        executor.ProcessIds.Should().ContainSingle().Which.Should().Be("conversion.polygonize");
    }

    [UnitTest]
    public async Task Polygonize_EightConnected_ProjectsFlagsAndField_AndPublishesGeoJson()
    {
        var runner = SucceedingGeoJson(Encoding.UTF8.GetBytes("{\"type\":\"FeatureCollection\"}"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalPolygonizeJobExecutor.HandledProcessId,
                ("source", Base64("fake-raster")),
                ("band", "2"),
                ("connectedness", "8"),
                ("fieldName", "value"));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            context.Artifacts.Single().Should().StartWith("data:application/geo+json");

            var invocation = runner.Invocations.Single();
            invocation.Tool.Should().Be("gdal_polygonize.py");
            invocation.Arguments.Should().Contain("-8");
            invocation.Arguments.Should().ContainInOrder("-b", "2");
            invocation.Arguments.Should().ContainInOrder("-f", "GeoJSON");
            invocation.Arguments[^1].Should().Be("value");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Polygonize_InvalidFieldName_FailsBeforeReachingTheCli()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalPolygonizeJobExecutor.HandledProcessId,
                ("source", Base64("fake-raster")),
                ("fieldName", "bad name!"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("fieldName");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Polygonize_InvalidConnectedness_Fails()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalPolygonizeJobExecutor.HandledProcessId,
                ("source", Base64("fake-raster")),
                ("connectedness", "6"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("connectedness");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    private static GdalPolygonizeJobExecutor NewExecutor(IGdalCommandRunner runner, out string scratch)
    {
        scratch = GdalCli.NewScratch(ScratchSuite);
        return new GdalPolygonizeJobExecutor(
            runner, GdalJobFactory.Options(scratch), NullLogger<GdalPolygonizeJobExecutor>.Instance);
    }

    private static void CleanupScratch(string scratch) => GdalCli.CleanupScratch(scratch);
}
