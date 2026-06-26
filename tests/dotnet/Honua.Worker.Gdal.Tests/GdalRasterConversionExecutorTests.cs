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
/// Fake-runner coverage for the conversion idioms wired to real GDAL executors
/// (#2138): <c>conversion.raster-format</c> (gdal_translate) and
/// <c>conversion.raster-reproject</c> (gdalwarp, sharing the catalog reproject
/// executor). Each test pins routing, argument projection, the artifact content
/// type, and GDAL-failure-to-job-failure mapping.
/// </summary>
public sealed class GdalRasterConversionExecutorTests
{
    private const string ScratchSuite = "honua-gdal-conversion-test";

    private static string Base64(string text) => GdalCli.Base64(text);

    private static string NewScratch() => GdalCli.NewScratch(ScratchSuite);

    private static void CleanupScratch(string scratch) => GdalCli.CleanupScratch(scratch);

    private static GdalRasterFormatConvertJobExecutor NewFormatExecutor(IGdalCommandRunner runner, out string scratch)
    {
        scratch = NewScratch();
        return new GdalRasterFormatConvertJobExecutor(
            runner, GdalJobFactory.Options(scratch), NullLogger<GdalRasterFormatConvertJobExecutor>.Instance);
    }

    private static GdalRasterReprojectCatalogJobExecutor NewReprojectExecutor(IGdalCommandRunner runner, out string scratch)
    {
        scratch = NewScratch();
        return new GdalRasterReprojectCatalogJobExecutor(
            runner, GdalJobFactory.Options(scratch), NullLogger<GdalRasterReprojectCatalogJobExecutor>.Instance);
    }

    [UnitTest]
    public async Task RasterFormat_GTiff_RunsGdalTranslate_AndPublishesGeoTiff()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("converted"));
        var executor = NewFormatExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterFormatConvertJobExecutor.HandledProcessId,
                ("source", Base64("fake-input-raster")),
                ("targetFormat", "GTiff"));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            context.Artifacts.Single().Should().StartWith("data:image/tiff");
            var invocation = runner.Invocations.Single();
            invocation.Tool.Should().Be("gdal_translate");
            invocation.Arguments.Should().ContainInOrder("-of", "GTiff");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task RasterFormat_Png_PublishesPngContentType()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("png-bytes"));
        var executor = NewFormatExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterFormatConvertJobExecutor.HandledProcessId,
                ("source", Base64("fake-input-raster")),
                ("targetFormat", "PNG"));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            context.Artifacts.Single().Should().StartWith("data:image/png");
            runner.Invocations.Single().Arguments.Should().ContainInOrder("-of", "PNG");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task RasterFormat_CompressionHint_PassesCreationOption()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("converted"));
        var executor = NewFormatExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterFormatConvertJobExecutor.HandledProcessId,
                ("source", Base64("fake-input-raster")),
                ("targetFormat", "GTiff"),
                ("compression", "DEFLATE"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            runner.Invocations.Single().Arguments.Should().ContainInOrder("-co", "COMPRESS=DEFLATE");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task RasterFormat_UnknownFormat_FailsWithoutInvokingGdal()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("x"));
        var executor = NewFormatExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterFormatConvertJobExecutor.HandledProcessId,
                ("source", Base64("fake-input-raster")),
                ("targetFormat", "BOGUS"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task RasterFormat_GdalFailure_SurfacesAsJobFailure()
    {
        var executor = NewFormatExecutor(FakeGdalCommandRunner.Failing(1, "translate boom"), out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterFormatConvertJobExecutor.HandledProcessId,
                ("source", Base64("fake-input-raster")),
                ("targetFormat", "GTiff"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("gdal_translate");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task RasterReproject_ConversionId_RunsGdalwarp_WithTargetSrs()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("reprojected"));
        var executor = NewReprojectExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterReprojectCatalogJobExecutor.ConversionProcessId,
                ("source", Base64("fake-input-raster")),
                ("targetSrid", "3857"));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            context.Artifacts.Single().Should().StartWith("data:image/tiff");
            var args = runner.Invocations.Single().Arguments;
            args.Should().ContainInOrder("-t_srs", "EPSG:3857");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }
}
