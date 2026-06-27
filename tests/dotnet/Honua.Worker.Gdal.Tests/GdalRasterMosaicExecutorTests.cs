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
/// Fake-runner coverage for <see cref="GdalRasterMosaicJobExecutor"/>: multi-input
/// gdalwarp argument projection, the first/last overlap-operator ordering, source
/// list parsing guards, and the canonical data-URI artifact contract.
/// </summary>
public sealed class GdalRasterMosaicExecutorTests
{
    private const string ScratchSuite = "honua-gdal-mosaic-test";

    private static string Base64(string text) => GdalCli.Base64(text);

    private static string Sources(params string[] entries)
        => string.Join(GdalRasterMosaicJobExecutor.SourceSeparator, entries.Select(Base64));

    [UnitTest]
    public void GdalRasterMosaicExecutor_DeclaresNativeRuntimeProfile()
    {
        var executor = NewExecutor(FakeGdalCommandRunner.Failing(1, "n/a"), out _);

        executor.Kind.Should().Be(ExecutionJobKind.Geoprocessing);
        executor.AcceptedRuntimeProfiles.Should().ContainSingle().Which.Should().Be(RuntimeProfiles.Native);
        executor.ProcessIds.Should().ContainSingle().Which.Should().Be("raster.mosaic");
    }

    [UnitTest]
    public async Task Mosaic_TwoSources_RunsGdalwarpWithBothInputs_AndPublishesGeoTiff()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("mosaic-tif"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterMosaicJobExecutor.HandledProcessId,
                ("sources", Sources("raster-a", "raster-b")));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            context.Artifacts.Should().ContainSingle();
            context.Artifacts[0].Should().StartWith("data:image/tiff");

            var invocation = runner.Invocations.Single();
            invocation.Tool.Should().Be("gdalwarp");
            invocation.Arguments.Should().Contain(a => a.EndsWith("input0.tif"));
            invocation.Arguments.Should().Contain(a => a.EndsWith("input1.tif"));
            invocation.Arguments.Should().ContainInOrder("-r", "near");
            invocation.Arguments[^1].Should().EndWith("output.tif");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Mosaic_FirstOperator_ReversesSourceOrderSoFirstSourceWins()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("ok"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterMosaicJobExecutor.HandledProcessId,
                ("sources", Sources("raster-a", "raster-b")),
                ("operator", "first"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);

            // 'first' reverses the source order so the first-listed source is written
            // LAST into the output (gdalwarp later-source-wins). The input paths are
            // numbered by write order, so input0 (the reversed write) comes before
            // input1 in the argument list.
            var args = runner.Invocations.Single().Arguments;
            var firstInputIndex = args.ToList().FindIndex(a => a.EndsWith("input0.tif"));
            var secondInputIndex = args.ToList().FindIndex(a => a.EndsWith("input1.tif"));
            firstInputIndex.Should().BeLessThan(secondInputIndex);
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Mosaic_UnsupportedOperator_FailsWithClearMessage()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterMosaicJobExecutor.HandledProcessId,
                ("sources", Sources("raster-a", "raster-b")),
                ("operator", "mean"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("operator").And.Contain("not supported");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Mosaic_SingleSource_FailsBecauseAtLeastTwoAreRequired()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterMosaicJobExecutor.HandledProcessId,
                ("sources", Base64("only-one")));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("at least two");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Mosaic_InvalidBase64Source_FailsWithClearMessage()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterMosaicJobExecutor.HandledProcessId,
                ("sources", Base64("valid") + GdalRasterMosaicJobExecutor.SourceSeparator + "!!not-base64!!"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("not valid base64");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Mosaic_TooManySources_FailsBeforeReachingTheCli()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var entries = Enumerable
                .Range(0, GdalRasterMosaicJobExecutor.MaxSources + 1)
                .Select(i => "tile" + i.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
            var job = GdalJobFactory.Job(
                GdalRasterMosaicJobExecutor.HandledProcessId,
                ("sources", Sources(entries)));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("at most");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Mosaic_AggregateDecodedBytesExceedCeiling_FailsBeforeReachingTheCli()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var scratch = GdalCli.NewScratch(ScratchSuite);
        // Each source decodes to 4 bytes (under the 6-byte ceiling) but together they
        // exceed it, so the aggregate guard must reject before any decode reaches scratch.
        var executor = new GdalRasterMosaicJobExecutor(
            runner, GdalJobFactory.Options(scratch, maxArtifactBytes: 6), NullLogger<GdalRasterMosaicJobExecutor>.Instance);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterMosaicJobExecutor.HandledProcessId,
                ("sources", Sources("ABCD", "EFGH")));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("total");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    private static GdalRasterMosaicJobExecutor NewExecutor(IGdalCommandRunner runner, out string scratch)
    {
        scratch = GdalCli.NewScratch(ScratchSuite);
        return new GdalRasterMosaicJobExecutor(
            runner, GdalJobFactory.Options(scratch), NullLogger<GdalRasterMosaicJobExecutor>.Instance);
    }

    private static void CleanupScratch(string scratch) => GdalCli.CleanupScratch(scratch);
}
