// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Worker.Gdal.Tests;

public sealed class GdalRasterConversionExecutorTests
{
    private const string ScratchSuite = "honua-gdal-conversion-test";

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
                ("source", GdalCli.RasterBase64("fake-input-raster")),
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
                ("source", GdalCli.RasterBase64("fake-input-raster")),
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
                ("source", GdalCli.RasterBase64("fake-input-raster")),
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
    public async Task RasterFormat_EveryPublishedAllowedValue_IsExecutableByTheWorker()
    {
        var unexecutable = new List<string>();

        foreach (var publishedFormat in ProcessValueDomains.RasterFormat)
        {
            var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("converted"));
            var executor = NewFormatExecutor(runner, out var scratch);
            try
            {
                var job = GdalJobFactory.Job(
                    GdalRasterFormatConvertJobExecutor.HandledProcessId,
                    ("source", GdalCli.RasterBase64("fake-input-raster")),
                    ("targetFormat", publishedFormat));

                var result = await executor.ExecuteAsync(
                    job, new RecordingJobExecutionContext(job.OperationId), default);

                if (result.Status != ExecutionJobStatus.Succeeded)
                {
                    unexecutable.Add($"{publishedFormat}: {result.ErrorMessage}");
                }
                else if (runner.Invocations.Count == 0)
                {
                    unexecutable.Add($"{publishedFormat}: never reached gdal_translate");
                }
            }
            finally
            {
                CleanupScratch(scratch);
            }
        }

        unexecutable.Should().BeEmpty(
            "every value the catalog advertises for targetFormat and the canonical validator "
            + "accepts must normalize onto a gdal_translate driver in "
            + "GdalRasterFormatConvertJobExecutor.Formats; otherwise the submit path accepts a "
            + "job the worker cannot finish");
    }

    [UnitTest]
    public async Task RasterFormat_AliasSpellings_ResolveToTheirCanonicalDriver()
    {
        (string Alias, string Driver, string ContentType)[] aliases =
        [
            ("GeoTIFF", "GTiff", "data:image/tiff"),
            ("TIFF", "GTiff", "data:image/tiff"),
            ("TIF", "GTiff", "data:image/tiff"),
            ("JPG", "JPEG", "data:image/jpeg"),
        ];

        foreach (var (alias, driver, contentType) in aliases)
        {
            var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("converted"));
            var executor = NewFormatExecutor(runner, out var scratch);
            try
            {
                var job = GdalJobFactory.Job(
                    GdalRasterFormatConvertJobExecutor.HandledProcessId,
                    ("source", GdalCli.RasterBase64("fake-input-raster")),
                    ("targetFormat", alias));
                var context = new RecordingJobExecutionContext(job.OperationId);

                var result = await executor.ExecuteAsync(job, context, default);

                result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
                runner.Invocations.Single().Arguments.Should().ContainInOrder("-of", driver);
                context.Artifacts.Single().Should().StartWith(contentType);
            }
            finally
            {
                CleanupScratch(scratch);
            }
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
                ("source", GdalCli.RasterBase64("fake-input-raster")),
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
                ("source", GdalCli.RasterBase64("fake-input-raster")),
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
                ("source", GdalCli.RasterBase64("fake-input-raster")),
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
