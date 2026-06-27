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
/// Fake-runner coverage for <see cref="GdalRasterSpectralIndexJobExecutor"/>: preset
/// band-role binding, the trusted Float32 calc expression projection, and the
/// required-band guards (#2239).
/// </summary>
public sealed class GdalRasterSpectralIndexExecutorTests
{
    private const string ScratchSuite = "honua-gdal-spectral-test";

    private static string Base64(string text) => GdalCli.Base64(text);

    [UnitTest]
    public void GdalRasterSpectralIndexExecutor_DeclaresNativeRuntimeProfile()
    {
        var executor = NewExecutor(FakeGdalCommandRunner.Failing(1, "n/a"), out _);

        executor.Kind.Should().Be(ExecutionJobKind.Geoprocessing);
        executor.AcceptedRuntimeProfiles.Should().ContainSingle().Which.Should().Be(RuntimeProfiles.Native);
        executor.ProcessIds.Should().ContainSingle().Which.Should().Be("raster.spectral-index");
    }

    [UnitTest]
    public async Task Ndvi_BindsNirAndRed_AndForcesFloat32()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("ndvi-tif"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterSpectralIndexJobExecutor.HandledProcessId,
                ("index", "NDVI"),
                ("nir", Base64("nir-raster")),
                ("red", Base64("red-raster")));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            context.Artifacts.Single().Should().StartWith("data:image/tiff");

            var args = runner.Invocations.Single().Arguments;
            args.Should().Contain(a => a.StartsWith("-A")).And.Contain(a => a.StartsWith("-B"));
            args.Should().ContainInOrder("--calc", "(1.0*A-B)/(1.0*A+B)");
            args.Should().ContainInOrder("--type", "Float32");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Savi_AppliesSoilFactorLiteral()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("savi"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterSpectralIndexJobExecutor.HandledProcessId,
                ("index", "SAVI"),
                ("nir", Base64("nir")),
                ("red", Base64("red")),
                ("L", "0.25"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            var args = runner.Invocations.Single().Arguments;
            var calcIndex = args.ToList().IndexOf("--calc");
            args[calcIndex + 1].Should().Contain("0.25");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Evi_MissingBlueBand_Fails()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterSpectralIndexJobExecutor.HandledProcessId,
                ("index", "EVI"),
                ("nir", Base64("nir")),
                ("red", Base64("red")));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("blue");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task UnknownIndex_Fails()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterSpectralIndexJobExecutor.HandledProcessId,
                ("index", "NDXX"),
                ("nir", Base64("nir")));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("index");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    private static GdalRasterSpectralIndexJobExecutor NewExecutor(IGdalCommandRunner runner, out string scratch)
    {
        scratch = GdalCli.NewScratch(ScratchSuite);
        return new GdalRasterSpectralIndexJobExecutor(
            runner, GdalJobFactory.Options(scratch), NullLogger<GdalRasterSpectralIndexJobExecutor>.Instance);
    }

    private static void CleanupScratch(string scratch) => GdalCli.CleanupScratch(scratch);
}
