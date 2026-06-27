// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
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

        // The raster analysis & terrain GP tool packs (#2141, #2239, #2240) must all be
        // routable by the worker dispatcher, including the flagged-but-routed ids
        // (interpolate-kriging, euclidean-allocation) that fail fast with a clear
        // message inside the executor.
        dispatcher.SupportedProcessIds.Should().Contain(new[]
        {
            "raster.resample",
            "raster.interpolate-idw",
            "raster.interpolate-kriging",
            "raster.mosaic",
            "raster.map-algebra",
            "raster.spectral-index",
            "raster.reclassify",
            "proximity.euclidean-distance",
            "proximity.euclidean-allocation",
            "surface.contour",
            "surface.viewshed",
            "conversion.polygonize",
            "conversion.rasterize",
        });
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
        IProcessExecutor[] executors =
        {
            new GdalVectorConvertJobExecutor(runner, options, NullLogger<GdalVectorConvertJobExecutor>.Instance),
            new GdalVectorReprojectJobExecutor(runner, options, NullLogger<GdalVectorReprojectJobExecutor>.Instance),
            new GdalVectorSourceReadJobExecutor(runner, options, NullLogger<GdalVectorSourceReadJobExecutor>.Instance),
            new GdalRasterReprojectJobExecutor(runner, options, NullLogger<GdalRasterReprojectJobExecutor>.Instance),
            new GdalSurfaceJobExecutor(runner, options, NullLogger<GdalSurfaceJobExecutor>.Instance),
            new GdalRasterClipJobExecutor(runner, options, NullLogger<GdalRasterClipJobExecutor>.Instance),
            new GdalRasterReprojectCatalogJobExecutor(runner, options, NullLogger<GdalRasterReprojectCatalogJobExecutor>.Instance),
            new GdalRasterStatisticsJobExecutor(runner, options, NullLogger<GdalRasterStatisticsJobExecutor>.Instance),
            new GdalRasterZonalStatisticsJobExecutor(runner, options, NullLogger<GdalRasterZonalStatisticsJobExecutor>.Instance),
            new GdalMultidimCoverageMetadataJobExecutor(runner, options, NullLogger<GdalMultidimCoverageMetadataJobExecutor>.Instance),
            new PdalPointCloudConvertJobExecutor(runner, options, NullLogger<PdalPointCloudConvertJobExecutor>.Instance),
            // Raster analysis & terrain GP tool packs (#2141, #2239, #2240).
            new GdalRasterResampleJobExecutor(runner, options, NullLogger<GdalRasterResampleJobExecutor>.Instance),
            new GdalRasterInterpolateJobExecutor(runner, options, NullLogger<GdalRasterInterpolateJobExecutor>.Instance),
            new GdalRasterMosaicJobExecutor(runner, options, NullLogger<GdalRasterMosaicJobExecutor>.Instance),
            new GdalRasterMapAlgebraJobExecutor(runner, options, NullLogger<GdalRasterMapAlgebraJobExecutor>.Instance),
            new GdalRasterSpectralIndexJobExecutor(runner, options, NullLogger<GdalRasterSpectralIndexJobExecutor>.Instance),
            new GdalRasterReclassifyJobExecutor(runner, options, NullLogger<GdalRasterReclassifyJobExecutor>.Instance),
            new GdalProximityJobExecutor(runner, options, NullLogger<GdalProximityJobExecutor>.Instance),
            new GdalContourJobExecutor(runner, options, NullLogger<GdalContourJobExecutor>.Instance),
            new GdalViewshedJobExecutor(runner, options, NullLogger<GdalViewshedJobExecutor>.Instance),
            new GdalPolygonizeJobExecutor(runner, options, NullLogger<GdalPolygonizeJobExecutor>.Instance),
            new GdalRasterizeJobExecutor(runner, options, NullLogger<GdalRasterizeJobExecutor>.Instance),
        };

        return new GdalDispatchJobExecutor(
            executors,
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

    [GdalCliFact("ogr2ogr")]
    [Protocol(ProtocolNames.TestQuality)]
    [Operation(Operations.TestInfrastructure)]
    public async Task VectorConvert_WithRealOgr2Ogr_ConvertsGeoJsonToCsv()
    {
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
    // Vector reproject (ogr2ogr datum shift) — fake runner + routing
    // -------------------------------------------------------------------------

    [UnitTest]
    public void VectorReproject_DeclaresNativeProfile_AndHandlesTransformReproject()
    {
        var executor = NewVectorReprojectExecutor(FakeGdalCommandRunner.Failing(1, "n/a"), out _);

        executor.Kind.Should().Be(ExecutionJobKind.Geoprocessing);
        executor.AcceptedRuntimeProfiles.Should().ContainSingle().Which.Should().Be(RuntimeProfiles.Native);
        executor.AcceptedRuntimeProfiles.Should().NotContain(RuntimeProfiles.Managed);
        // The native reproject executor handles the SAME process id as the managed
        // executor — escalation routes by RuntimeProfile, not by a distinct id.
        GdalVectorReprojectJobExecutor.HandledProcessId.Should().Be("transform.reproject");
    }

    [UnitTest]
    public async Task VectorReproject_Succeeds_PublishesGeoJsonArtifact_AndPassesBothSrs()
    {
        var reprojected = Encoding.UTF8.GetBytes(PointGeoJson);
        // ogr2ogr arg order is "-f GeoJSON -s_srs .. -t_srs .. <output> <input>", so
        // the output path is the second-to-last argument.
        var runner = FakeGdalCommandRunner.Succeeding(reprojected, outputArgIndexFromEnd: 1);
        var executor = NewVectorReprojectExecutor(runner, out var scratch);

        try
        {
            var job = GdalJobFactory.Job(
                GdalVectorReprojectJobExecutor.HandledProcessId,
                ("input", "data:application/geo+json;base64," + Base64(PointGeoJson)),
                ("fromSrid", "4267"),
                ("toSrid", "4326"));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            context.Artifacts.Should().ContainSingle();
            context.Artifacts[0].Should().StartWith("data:application/geo+json;base64,");

            // PROJ selects the datum-shift pipeline from the explicit s_srs/t_srs pair.
            var args = runner.Invocations.Single().Arguments;
            args.Should().ContainInOrder("-s_srs", "EPSG:4267");
            args.Should().ContainInOrder("-t_srs", "EPSG:4326");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task VectorReproject_RejectsNonGeoJsonDataUri()
    {
        var executor = NewVectorReprojectExecutor(FakeGdalCommandRunner.Failing(1, "n/a"), out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalVectorReprojectJobExecutor.HandledProcessId,
                ("input", "not-a-data-uri"),
                ("fromSrid", "4267"),
                ("toSrid", "4326"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("data URI");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    // -------------------------------------------------------------------------
    // OGR source read (ogr2ogr canonicalization) — fake runner + routing
    // -------------------------------------------------------------------------

    [UnitTest]
    public void SourceRead_DeclaresNativeProfile_AndHandlesSourceOgr()
    {
        var executor = NewSourceReadExecutor(FakeGdalCommandRunner.Failing(1, "n/a"), out _);

        executor.Kind.Should().Be(ExecutionJobKind.Geoprocessing);
        executor.AcceptedRuntimeProfiles.Should().ContainSingle().Which.Should().Be(RuntimeProfiles.Native);
        GdalVectorSourceReadJobExecutor.HandledProcessId.Should().Be("source.ogr");
    }

    [UnitTest]
    public async Task SourceRead_Succeeds_CanonicalizesToGeoJsonArtifact()
    {
        var canonical = Encoding.UTF8.GetBytes(PointGeoJson);
        var runner = FakeGdalCommandRunner.Succeeding(canonical, outputArgIndexFromEnd: 1);
        var executor = NewSourceReadExecutor(runner, out var scratch);

        try
        {
            var job = GdalJobFactory.Job(
                GdalVectorSourceReadJobExecutor.HandledProcessId,
                ("source", Base64(PointGeoJson)),
                ("sourceFormat", "GeoJSON"));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            context.Artifacts.Should().ContainSingle();
            context.Artifacts[0].Should().StartWith("data:application/geo+json;base64,");
            runner.Invocations.Single().Arguments.Should().ContainInOrder("-f", "GeoJSON");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    // -------------------------------------------------------------------------
    // Real GDAL CLI — datum-shift reprojection proof when ogr2ogr is available
    // -------------------------------------------------------------------------

    [GdalCliFact("ogr2ogr")]
    [Protocol(ProtocolNames.TestQuality)]
    [Operation(Operations.TestInfrastructure)]
    public async Task VectorReproject_WithRealOgr2Ogr_AppliesNad27ToWgs84DatumShift()
    {
        var scratch = NewScratch();
        var executor = new GdalVectorReprojectJobExecutor(
            new ProcessGdalCommandRunner(NullLogger<ProcessGdalCommandRunner>.Instance),
            GdalJobFactory.Options(scratch),
            NullLogger<GdalVectorReprojectJobExecutor>.Instance);

        try
        {
            // A point near Meades Ranch, Kansas — the NAD 27 origin region where the
            // NAD27→WGS84 datum shift is on the order of tens of metres (well above any
            // rounding). NAD 27 = EPSG:4267, WGS 84 = EPSG:4326. The managed fast path
            // REJECTS this pair; only the PROJ-backed worker performs the grid shift.
            const double srcLon = -98.5;
            const double srcLat = 39.0;
            var nad27Point =
                "{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\",\"geometry\":" +
                "{\"type\":\"Point\",\"coordinates\":[" +
                srcLon.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
                srcLat.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                "]},\"properties\":{}}]}";

            var job = GdalJobFactory.Job(
                GdalVectorReprojectJobExecutor.HandledProcessId,
                ("input", "data:application/geo+json;base64," + Base64(nad27Point)),
                ("fromSrid", "4267"),
                ("toSrid", "4326"));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            context.Artifacts.Should().ContainSingle();

            var outGeoJson = Encoding.UTF8.GetString(GdalCli.DecodeDataUri(context.Artifacts[0]));
            var (outLon, outLat) = FirstPointCoordinates(outGeoJson);

            // The datum shift must move the coordinate — but only slightly (the NAD27
            // vs WGS84 horizontal difference is sub-kilometre). A passthrough/identity
            // bug would leave the coordinate exactly unchanged; a wrong CRS would move
            // it kilometres. Assert a non-zero shift bounded to a realistic envelope.
            var deltaLon = Math.Abs(outLon - srcLon);
            var deltaLat = Math.Abs(outLat - srcLat);
            (deltaLon + deltaLat).Should().BeGreaterThan(1e-6,
                "NAD27→WGS84 is a real datum shift, not an identity transform");
            deltaLon.Should().BeLessThan(0.01, "the horizontal datum shift is sub-kilometre, not a CRS mistake");
            deltaLat.Should().BeLessThan(0.01, "the horizontal datum shift is sub-kilometre, not a CRS mistake");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    private static (double Lon, double Lat) FirstPointCoordinates(string geoJson)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(geoJson);
        var coords = doc.RootElement
            .GetProperty("features")[0]
            .GetProperty("geometry")
            .GetProperty("coordinates");
        return (coords[0].GetDouble(), coords[1].GetDouble());
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static GdalVectorReprojectJobExecutor NewVectorReprojectExecutor(IGdalCommandRunner runner, out string scratch)
    {
        scratch = NewScratch();
        return new GdalVectorReprojectJobExecutor(
            runner, GdalJobFactory.Options(scratch), NullLogger<GdalVectorReprojectJobExecutor>.Instance);
    }

    private static GdalVectorSourceReadJobExecutor NewSourceReadExecutor(IGdalCommandRunner runner, out string scratch)
    {
        scratch = NewScratch();
        return new GdalVectorSourceReadJobExecutor(
            runner, GdalJobFactory.Options(scratch), NullLogger<GdalVectorSourceReadJobExecutor>.Instance);
    }

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
