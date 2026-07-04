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
/// Fake-runner coverage for <see cref="GdalRasterReclassifyJobExecutor"/>: remap-table
/// folding into a trusted nested-where gdal_calc.py expression, plus the parse guards
/// (#2239).
/// </summary>
public sealed class GdalRasterReclassifyExecutorTests
{
    private const string ScratchSuite = "honua-gdal-reclassify-test";

    private static string Base64(string text) => GdalCli.Base64(text);

    [UnitTest]
    public void GdalRasterReclassifyExecutor_DeclaresNativeRuntimeProfile()
    {
        var executor = NewExecutor(FakeGdalCommandRunner.Failing(1, "n/a"), out _);

        executor.Kind.Should().Be(ExecutionJobKind.Geoprocessing);
        executor.AcceptedRuntimeProfiles.Should().ContainSingle().Which.Should().Be(RuntimeProfiles.Native);
        executor.ProcessIds.Should().ContainSingle().Which.Should().Be("raster.reclassify");
    }

    [UnitTest]
    public async Task Reclassify_RangeTable_BuildsNestedWhere_AndPublishesGeoTiff()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("reclassified"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterReclassifyJobExecutor.HandledProcessId,
                ("source", Base64("fake-raster")),
                ("remap", "0..10:1;10..20:2"));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            context.Artifacts.Single().Should().StartWith("data:image/tiff");

            var args = runner.Invocations.Single(i => i.Tool == "gdal_calc.py").Arguments;
            args.Should().Contain("-A");
            var calcIndex = args.ToList().IndexOf("--calc");
            var calc = args[calcIndex + 1];
            calc.Should().StartWith("where(");
            calc.Should().Contain("(A>=0)&(A<10)");
            calc.Should().EndWith("A))");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Reclassify_SingleValuesAndDefault_FoldsDefaultLast()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("ok"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterReclassifyJobExecutor.HandledProcessId,
                ("source", Base64("fake-raster")),
                ("remap", "1:100;2:200"),
                ("defaultValue", "0"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            var args = runner.Invocations.Single(i => i.Tool == "gdal_calc.py").Arguments;
            var calc = args[args.ToList().IndexOf("--calc") + 1];
            calc.Should().Contain("(A==1)");
            calc.Should().Contain("(A==2)");
            calc.Should().EndWith(",0))");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Reclassify_NonNumericRemap_FailsBeforeReachingTheCli()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterReclassifyJobExecutor.HandledProcessId,
                ("source", Base64("fake-raster")),
                ("remap", "low..high:1"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("remap");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Reclassify_MissingRemap_Fails()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterReclassifyJobExecutor.HandledProcessId,
                ("source", Base64("fake-raster")));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("remap");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    private static GdalRasterReclassifyJobExecutor NewExecutor(IGdalCommandRunner runner, out string scratch)
    {
        scratch = GdalCli.NewScratch(ScratchSuite);
        return new GdalRasterReclassifyJobExecutor(
            runner, GdalJobFactory.Options(scratch), NullLogger<GdalRasterReclassifyJobExecutor>.Instance);
    }

    private static void CleanupScratch(string scratch) => GdalCli.CleanupScratch(scratch);
}
