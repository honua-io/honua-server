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
/// Worker-side coverage for the native-profile GDAL executors. The fake-runner
/// tests pin routing, input validation, guardrails, and the canonical
/// artifact-publication contract without requiring GDAL on the host. The
/// integration test exercises the real <c>ogr2ogr</c> CLI when it is present in
/// the worker image / dev host.
/// </summary>
public sealed class GdalWorkerExecutorTests
{
    private const string ScratchSuite = "honua-gdal-test";

    private static readonly string PointGeoJson =
        """{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"Point","coordinates":[1,2]},"properties":{"name":"a"}}]}""";

    private static string Base64(string text) => GdalCli.Base64(text);

    // -------------------------------------------------------------------------
    // Runtime-profile routing contract
    // -------------------------------------------------------------------------

    [UnitTest]
    public void GdalExecutors_DeclareNativeRuntimeProfile_SoTheClaimFenceRoutesToTheWorker()
    {
        var vector = NewVectorExecutor(FakeGdalCommandRunner.Failing(1, "n/a"), out _);
        var raster = NewRasterExecutor(FakeGdalCommandRunner.Failing(1, "n/a"), out _);

        vector.Kind.Should().Be(ExecutionJobKind.Geoprocessing);
        raster.Kind.Should().Be(ExecutionJobKind.Geoprocessing);

        // The claim fence (this branch's RuntimeProfiles helper) is fail-closed:
        // an executor must explicitly declare { "native" } — never the managed
        // default — for JobExecutionService to claim native jobs for it.
        vector.AcceptedRuntimeProfiles.Should().NotBeNull().And.ContainSingle()
            .Which.Should().Be(RuntimeProfiles.Native);
        raster.AcceptedRuntimeProfiles.Should().NotBeNull().And.ContainSingle()
            .Which.Should().Be(RuntimeProfiles.Native);
        vector.AcceptedRuntimeProfiles.Should().NotContain(RuntimeProfiles.Managed);
        raster.AcceptedRuntimeProfiles.Should().NotContain(RuntimeProfiles.Managed);
    }

    [UnitTest]
    public void Dispatcher_DeclaresNativeProfile_AndRoutesSupportedProcessIds()
    {
        var dispatcher = NewDispatcher();

        dispatcher.AcceptedRuntimeProfiles.Should().NotBeNull().And.ContainSingle()
            .Which.Should().Be(RuntimeProfiles.Native);
        dispatcher.AcceptedRuntimeProfiles.Should().NotContain(RuntimeProfiles.Managed);
        dispatcher.SupportedProcessIds.Should().Contain(new[] { "gdal.ogr2ogr", "gdal.gdalwarp" });
    }

    [UnitTest]
    public async Task Dispatcher_RejectsUnsupportedProcessId_WithFailedResult()
    {
        var dispatcher = NewDispatcher();

        var job = GdalJobFactory.Job("geometry.buffer");
        var result = await dispatcher.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("not supported by the GDAL worker runtime");
    }

    private static GdalDispatchJobExecutor NewDispatcher()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var options = GdalJobFactory.Options(Path.Combine(Path.GetTempPath(), "honua-gdal-dispatch-test"));
        return new GdalDispatchJobExecutor(
            new GdalVectorConvertJobExecutor(runner, options, NullLogger<GdalVectorConvertJobExecutor>.Instance),
            new GdalRasterReprojectJobExecutor(runner, options, NullLogger<GdalRasterReprojectJobExecutor>.Instance),
            new GdalSurfaceJobExecutor(runner, options, NullLogger<GdalSurfaceJobExecutor>.Instance),
            new GdalRasterClipJobExecutor(runner, options, NullLogger<GdalRasterClipJobExecutor>.Instance),
            new GdalRasterReprojectCatalogJobExecutor(runner, options, NullLogger<GdalRasterReprojectCatalogJobExecutor>.Instance),
            new GdalRasterStatisticsJobExecutor(runner, options, NullLogger<GdalRasterStatisticsJobExecutor>.Instance),
            new GdalRasterZonalStatisticsJobExecutor(runner, options, NullLogger<GdalRasterZonalStatisticsJobExecutor>.Instance),
            NullLogger<GdalDispatchJobExecutor>.Instance);
    }

    // -------------------------------------------------------------------------
    // Vector conversion (ogr2ogr) — fake runner
    // -------------------------------------------------------------------------

    [UnitTest]
    public async Task VectorConvert_Succeeds_PublishesCanonicalDataUriArtifact()
    {
        var convertedBytes = Encoding.UTF8.GetBytes("converted-csv-payload");
        // ogr2ogr arg order is "-f <fmt> <output> <input>", so the output path is
        // the second-to-last argument.
        var runner = FakeGdalCommandRunner.Succeeding(convertedBytes, outputArgIndexFromEnd: 1);
        var executor = NewVectorExecutor(runner, out var scratch);

        try
        {
            var job = GdalJobFactory.Job(
                GdalVectorConvertJobExecutor.HandledProcessId,
                ("source", Base64(PointGeoJson)),
                ("sourceFormat", "GeoJSON"),
                ("targetFormat", "CSV"));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded);
            context.Artifacts.Should().ContainSingle();
            context.Artifacts[0].Should().StartWith("data:text/csv;base64,");
            GdalCli.DecodeDataUri(context.Artifacts[0]).Should().Equal(convertedBytes);
            context.Progress.Should().Contain(p => p.Percent == 100);

            // ogr2ogr was invoked with the target driver and the output before the input.
            runner.Invocations.Should().ContainSingle();
            runner.Invocations[0].Tool.Should().Be("ogr2ogr");
            runner.Invocations[0].Arguments.Should().ContainInOrder("-f", "CSV");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task VectorConvert_WhenToolFails_ReturnsFailedWithStderr()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "ERROR 1: source dataset unreadable");
        var executor = NewVectorExecutor(runner, out var scratch);

        try
        {
            var job = GdalJobFactory.Job(
                GdalVectorConvertJobExecutor.HandledProcessId,
                ("source", Base64(PointGeoJson)),
                ("targetFormat", "CSV"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("ogr2ogr exited with code 1");
            result.ErrorMessage.Should().Contain("source dataset unreadable");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task VectorConvert_RejectsMissingTargetFormat()
    {
        var executor = NewVectorExecutor(FakeGdalCommandRunner.Failing(1, "n/a"), out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalVectorConvertJobExecutor.HandledProcessId,
                ("source", Base64(PointGeoJson)));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);
            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("targetFormat");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task VectorConvert_RejectsUnsupportedTargetFormat()
    {
        var executor = NewVectorExecutor(FakeGdalCommandRunner.Failing(1, "n/a"), out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalVectorConvertJobExecutor.HandledProcessId,
                ("source", Base64(PointGeoJson)),
                ("targetFormat", "NotARealDriver"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);
            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("is not supported");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    // -------------------------------------------------------------------------
    // Raster reproject (gdalwarp) — fake runner + SRS validation
    // -------------------------------------------------------------------------

    [UnitTest]
    public async Task RasterReproject_Succeeds_PublishesGeoTiffArtifact_AndPassesTargetSrs()
    {
        var reprojected = Encoding.UTF8.GetBytes("fake-geotiff-bytes");
        var runner = FakeGdalCommandRunner.Succeeding(reprojected);
        var executor = NewRasterExecutor(runner, out var scratch);

        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterReprojectJobExecutor.HandledProcessId,
                ("source", Base64("fake-input-raster")),
                ("sourceSrs", "EPSG:4326"),
                ("targetSrs", "3857"));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded);
            context.Artifacts.Should().ContainSingle();
            context.Artifacts[0].Should().StartWith("data:image/tiff");

            var args = runner.Invocations.Single().Arguments;
            args.Should().ContainInOrder("-t_srs", "EPSG:3857");
            args.Should().ContainInOrder("-s_srs", "EPSG:4326");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task RasterReproject_RejectsInjectionShapedTargetSrs()
    {
        var runner = FakeGdalCommandRunner.Succeeding([1, 2, 3]);
        var executor = NewRasterExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterReprojectJobExecutor.HandledProcessId,
                ("source", Base64("fake-input-raster")),
                ("targetSrs", "4326; rm -rf /"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("not an accepted CRS token");
            runner.Invocations.Should().BeEmpty("a rejected SRS token must never reach the CLI");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    // -------------------------------------------------------------------------
    // Real GDAL CLI — end-to-end proof when ogr2ogr is available
    // -------------------------------------------------------------------------

    [IntegrationTest]
    [Protocol("OgcApiProcesses")]
    [Operation("ProcessExecution")]
    public async Task VectorConvert_WithRealOgr2Ogr_ConvertsGeoJsonToCsv()
    {
        if (!GdalCli.Available("ogr2ogr"))
        {
            // GDAL CLI absent (e.g. lean CI agent). The fake-runner tests already
            // pin the executor contract; this case requires the worker image's
            // GDAL base layer or a dev host with GDAL installed.
            return;
        }

        var scratch = NewScratch();
        var executor = new GdalVectorConvertJobExecutor(
            new ProcessGdalCommandRunner(NullLogger<ProcessGdalCommandRunner>.Instance),
            GdalJobFactory.Options(scratch),
            NullLogger<GdalVectorConvertJobExecutor>.Instance);

        try
        {
            var job = GdalJobFactory.Job(
                GdalVectorConvertJobExecutor.HandledProcessId,
                ("source", Base64(PointGeoJson)),
                ("sourceFormat", "GeoJSON"),
                ("targetFormat", "CSV"));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            context.Artifacts.Should().ContainSingle();
            context.Artifacts[0].Should().StartWith("data:text/csv;base64,");

            var csv = Encoding.UTF8.GetString(GdalCli.DecodeDataUri(context.Artifacts[0]));
            // ogr2ogr's CSV driver emits a WKT/coordinate column plus the 'name' property.
            csv.Should().Contain("name");
            csv.Should().Contain("a");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static GdalVectorConvertJobExecutor NewVectorExecutor(IGdalCommandRunner runner, out string scratch)
    {
        scratch = NewScratch();
        return new GdalVectorConvertJobExecutor(
            runner, GdalJobFactory.Options(scratch), NullLogger<GdalVectorConvertJobExecutor>.Instance);
    }

    private static GdalRasterReprojectJobExecutor NewRasterExecutor(IGdalCommandRunner runner, out string scratch)
    {
        scratch = NewScratch();
        return new GdalRasterReprojectJobExecutor(
            runner, GdalJobFactory.Options(scratch), NullLogger<GdalRasterReprojectJobExecutor>.Instance);
    }

    private static string NewScratch() => GdalCli.NewScratch(ScratchSuite);

    private static void CleanupScratch(string scratch) => GdalCli.CleanupScratch(scratch);
}
