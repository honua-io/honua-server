// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Worker.Gdal.Tests;

/// <summary>
/// Fake-runner coverage for the native-profile raster.* executors:
/// <see cref="GdalRasterClipJobExecutor"/>, <see cref="GdalRasterReprojectCatalogJobExecutor"/>,
/// <see cref="GdalRasterStatisticsJobExecutor"/>, and
/// <see cref="GdalRasterZonalStatisticsJobExecutor"/>. Each test pins routing,
/// argument projection, guardrails, and the canonical artifact contract.
/// </summary>
public sealed class GdalRasterExecutorTests
{
    private const string ZonalGdalinfoFixture =
        """{"bands":[{"band":1,"type":"Float32","minimum":1.0,"maximum":5.0,"mean":3.0,"stdDev":1.4,"validCount":25}]}""";

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    // -------------------------------------------------------------------------
    // raster.clip
    // -------------------------------------------------------------------------

    [UnitTest]
    public async Task RasterClip_Succeeds_PublishesGeoTiff_AndWritesCutlineGeoJson()
    {
        var output = Encoding.UTF8.GetBytes("fake-clipped-tif");
        var runner = FakeGdalCommandRunner.Succeeding(output);
        var executor = NewClipExecutor(runner, out var scratch);
        try
        {
            var polygon = new GeometryFactory().CreatePolygon(new[]
            {
                new Coordinate(0, 0),
                new Coordinate(10, 0),
                new Coordinate(10, 10),
                new Coordinate(0, 10),
                new Coordinate(0, 0),
            });
            var wkb = new WKBWriter().Write(polygon);

            var job = GdalJobFactory.Job(
                GdalRasterClipJobExecutor.HandledProcessId,
                ("source", Base64("fake-input-raster")),
                ("boundary", Convert.ToBase64String(wkb)));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);
            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            context.Artifacts.Should().ContainSingle();
            context.Artifacts[0].Should().StartWith("data:image/tiff");

            var invocation = runner.Invocations.Single();
            invocation.Tool.Should().Be("gdalwarp");
            invocation.Arguments.Should().Contain("-cutline");
            invocation.Arguments.Should().Contain("-crop_to_cutline");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task RasterClip_NonPolygonBoundary_RejectedWithClearMessage()
    {
        var executor = NewClipExecutor(FakeGdalCommandRunner.Failing(1, "n/a"), out var scratch);
        try
        {
            var point = new GeometryFactory().CreatePoint(new Coordinate(1, 2));
            var wkb = new WKBWriter().Write(point);
            var job = GdalJobFactory.Job(
                GdalRasterClipJobExecutor.HandledProcessId,
                ("source", Base64("fake-input-raster")),
                ("boundary", Convert.ToBase64String(wkb)));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);
            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("Polygon or MultiPolygon");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public void RasterClip_CutlineGeoJsonOmitsCrs_WhenBoundarySridZero()
    {
        var polygon = new GeometryFactory().CreatePolygon(new[]
        {
            new Coordinate(0, 0),
            new Coordinate(1, 0),
            new Coordinate(1, 1),
            new Coordinate(0, 1),
            new Coordinate(0, 0),
        });
        // SRID defaults to 0 — gdalwarp must NOT see a fabricated CRS metadata.
        polygon.SRID = 0;
        var json = GdalRasterClipJobExecutor.WriteCutlineGeoJson(polygon);
        json.Should().Contain("\"FeatureCollection\"");
        json.Should().Contain("\"Polygon\"");
        json.Should().NotContain("\"crs\":");
    }

    [UnitTest]
    public void RasterClip_CutlineGeoJsonEmitsCrs_WhenBoundarySridSupplied()
    {
        var polygon = new GeometryFactory(new PrecisionModel(), 4326).CreatePolygon(new[]
        {
            new Coordinate(0, 0),
            new Coordinate(1, 0),
            new Coordinate(1, 1),
            new Coordinate(0, 1),
            new Coordinate(0, 0),
        });
        var json = GdalRasterClipJobExecutor.WriteCutlineGeoJson(polygon);
        json.Should().Contain("\"crs\":");
        json.Should().Contain("\"EPSG:4326\"");
    }

    // -------------------------------------------------------------------------
    // raster.reproject (catalog)
    // -------------------------------------------------------------------------

    [UnitTest]
    public async Task RasterReprojectCatalog_DefaultResampling_PassesBilinear_AndEpsgToken()
    {
        var output = Encoding.UTF8.GetBytes("fake-reprojected");
        var runner = FakeGdalCommandRunner.Succeeding(output);
        var executor = NewReprojectExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterReprojectCatalogJobExecutor.HandledProcessId,
                ("source", Base64("fake-input-raster")),
                ("targetSrid", "3857"));

            var context = new RecordingJobExecutionContext(job.OperationId);
            var result = await executor.ExecuteAsync(job, context, default);
            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            context.Artifacts.Single().Should().StartWith("data:image/tiff");

            var args = runner.Invocations.Single().Arguments;
            args.Should().ContainInOrder("-t_srs", "EPSG:3857");
            args.Should().ContainInOrder("-r", "bilinear");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task RasterReprojectCatalog_NearestNeighborAlias_MapsToNear()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("ok"));
        var executor = NewReprojectExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterReprojectCatalogJobExecutor.HandledProcessId,
                ("source", Base64("fake-input-raster")),
                ("targetSrid", "4326"),
                ("resampling", "nearestneighbor"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);
            result.Status.Should().Be(ExecutionJobStatus.Succeeded);
            runner.Invocations.Single().Arguments.Should().ContainInOrder("-r", "near");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task RasterReprojectCatalog_RejectsUnknownResampling()
    {
        var executor = NewReprojectExecutor(FakeGdalCommandRunner.Failing(1, "n/a"), out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterReprojectCatalogJobExecutor.HandledProcessId,
                ("source", Base64("fake-input-raster")),
                ("targetSrid", "4326"),
                ("resampling", "nope"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);
            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("resampling");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task RasterReprojectCatalog_RejectsNonPositiveTargetSrid()
    {
        var executor = NewReprojectExecutor(FakeGdalCommandRunner.Failing(1, "n/a"), out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterReprojectCatalogJobExecutor.HandledProcessId,
                ("source", Base64("fake-input-raster")),
                ("targetSrid", "0"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);
            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("targetSrid");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    // -------------------------------------------------------------------------
    // raster.statistics / raster.histogram
    // -------------------------------------------------------------------------

    [UnitTest]
    public async Task RasterStatistics_PublishesScalarFromGdalinfoStdout()
    {
        const string fakeGdalinfo =
            """{"bands":[{"band":1,"type":"Float32","minimum":0.0,"maximum":100.0,"mean":50.0,"stdDev":15.0,"validCount":256}]}""";

        var runner = new FakeGdalCommandRunner((_, _, _) => new GdalCommandResult
        {
            ExitCode = 0,
            StandardOutput = fakeGdalinfo,
        });
        var executor = NewStatsExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterStatisticsJobExecutor.StatisticsProcessId,
                ("source", Base64("fake-input-raster")));

            var context = new RecordingJobExecutionContext(job.OperationId);
            var result = await executor.ExecuteAsync(job, context, default);
            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);

            var scalar = ReadScalar(context.Artifacts.Single());
            using var doc = JsonDocument.Parse(scalar);
            doc.RootElement.GetProperty("kind").GetString().Should().Be("raster.statistics");
            var band = doc.RootElement.GetProperty("bands").EnumerateArray().Single();
            band.GetProperty("band").GetInt32().Should().Be(1);
            band.GetProperty("min").GetDouble().Should().Be(0d);
            band.GetProperty("max").GetDouble().Should().Be(100d);
            band.GetProperty("mean").GetDouble().Should().Be(50d);
            band.GetProperty("stddev").GetDouble().Should().Be(15d);
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task RasterStatistics_FiltersBands_WhenBandsParameterSupplied()
    {
        const string fakeGdalinfo =
            """{"bands":[{"band":1,"mean":10},{"band":2,"mean":20},{"band":3,"mean":30}]}""";

        var runner = new FakeGdalCommandRunner((_, _, _) => new GdalCommandResult
        {
            ExitCode = 0,
            StandardOutput = fakeGdalinfo,
        });
        var executor = NewStatsExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterStatisticsJobExecutor.StatisticsProcessId,
                ("source", Base64("fake-input-raster")),
                ("bands", "1,3"));

            var context = new RecordingJobExecutionContext(job.OperationId);
            var result = await executor.ExecuteAsync(job, context, default);
            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);

            var scalar = ReadScalar(context.Artifacts.Single());
            using var doc = JsonDocument.Parse(scalar);
            var bands = doc.RootElement.GetProperty("bands").EnumerateArray()
                .Select(b => b.GetProperty("band").GetInt32()).ToArray();
            bands.Should().BeEquivalentTo(new[] { 1, 3 });
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task RasterHistogram_PublishesBucketsFromGdalinfo()
    {
        // gdalinfo -hist returns a fixed 256-bucket histogram; the executor
        // copies whatever the CLI emits and records the fixed-bucketing
        // disclosure on the artifact so callers know not to assume the
        // catalog can drive bin granularity.
        const string fakeGdalinfo =
            """{"bands":[{"band":1,"histogram":{"min":0,"max":255,"buckets":[10,20,30,40]}}]}""";

        var runner = new FakeGdalCommandRunner((_, _, _) => new GdalCommandResult
        {
            ExitCode = 0,
            StandardOutput = fakeGdalinfo,
        });
        var executor = NewStatsExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterStatisticsJobExecutor.HistogramProcessId,
                ("source", Base64("fake-input-raster")));

            var context = new RecordingJobExecutionContext(job.OperationId);
            var result = await executor.ExecuteAsync(job, context, default);
            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);

            var scalar = ReadScalar(context.Artifacts.Single());
            using var doc = JsonDocument.Parse(scalar);
            doc.RootElement.GetProperty("kind").GetString().Should().Be("raster.histogram");
            doc.RootElement.GetProperty("bucketSource").GetString().Should().Contain("gdalinfo -hist");
            var band = doc.RootElement.GetProperty("bands").EnumerateArray().Single();
            band.GetProperty("buckets").EnumerateArray().Count().Should().Be(4);

            // -hist must be passed to gdalinfo only for the histogram process id.
            runner.Invocations.Single().Arguments.Should().Contain("-hist");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task RasterStatistics_OmitsHistFlag()
    {
        const string fakeGdalinfo = """{"bands":[{"band":1,"mean":1}]}""";
        var runner = new FakeGdalCommandRunner((_, _, _) => new GdalCommandResult
        {
            ExitCode = 0,
            StandardOutput = fakeGdalinfo,
        });
        var executor = NewStatsExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterStatisticsJobExecutor.StatisticsProcessId,
                ("source", Base64("fake-input-raster")));

            await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);
            runner.Invocations.Single().Arguments.Should().NotContain("-hist");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    // -------------------------------------------------------------------------
    // raster.zonal-statistics
    // -------------------------------------------------------------------------

    [UnitTest]
    public async Task ZonalStatistics_OneZone_PublishesScalarWithAggregatesPerZone()
    {
        // First call is the clip (gdalwarp), second is the stats (gdalinfo).
        // The fake runner needs to be able to write a non-empty clipped file
        // and then return a JSON stats document on the gdalinfo call.
        var runner = new FakeGdalCommandRunner((tool, args, _) =>
        {
            if (string.Equals(tool, "gdalwarp", StringComparison.Ordinal))
            {
                var outputPath = args[^1];
                File.WriteAllBytes(outputPath, Encoding.UTF8.GetBytes("clipped"));
                return new GdalCommandResult { ExitCode = 0 };
            }
            return new GdalCommandResult { ExitCode = 0, StandardOutput = ZonalGdalinfoFixture };
        });

        var executor = NewZonalExecutor(runner, out var scratch);
        try
        {
            var zonesGeoJson =
                """{"type":"FeatureCollection","features":[{"type":"Feature","properties":{"id":"A"},"geometry":{"type":"Polygon","coordinates":[[[0,0],[10,0],[10,10],[0,10],[0,0]]]}}]}""";

            var job = GdalJobFactory.Job(
                GdalRasterZonalStatisticsJobExecutor.HandledProcessId,
                ("source", Base64("fake-input-raster")),
                ("zones", Base64(zonesGeoJson)),
                ("statistics", "count,mean,min,max,sum"));

            var context = new RecordingJobExecutionContext(job.OperationId);
            var result = await executor.ExecuteAsync(job, context, default);
            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);

            var scalar = ReadScalar(context.Artifacts.Single());
            using var doc = JsonDocument.Parse(scalar);
            doc.RootElement.GetProperty("kind").GetString().Should().Be("raster.zonal-statistics");
            doc.RootElement.GetProperty("band").GetInt32().Should().Be(1);

            var zone = doc.RootElement.GetProperty("zones").EnumerateArray().Single();
            zone.GetProperty("zoneIndex").GetInt32().Should().Be(0);
            zone.GetProperty("zoneId").GetString().Should().Be("A");
            zone.GetProperty("count").GetDouble().Should().Be(25d);
            zone.GetProperty("mean").GetDouble().Should().Be(3d);
            zone.GetProperty("min").GetDouble().Should().Be(1d);
            zone.GetProperty("max").GetDouble().Should().Be(5d);
            zone.GetProperty("sum").GetDouble().Should().Be(75d);
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task ZonalStatistics_BandAboveOne_ReadsClippedOutputBandOne_AndReportsOriginalBand()
    {
        // gdalwarp -b N writes the requested source band as output band 1 (the
        // clipped raster is single-band). The executor must read stats from
        // output band 1 even when the caller requested band 2 on the source,
        // and the artifact must surface the original requested band number so
        // callers can correlate back to the source. Regression for the gap
        // where ExtractZoneStatistics searched the clipped JSON for the
        // original band index and returned nulls.
        var runner = new FakeGdalCommandRunner((tool, args, _) =>
        {
            if (string.Equals(tool, "gdalwarp", StringComparison.Ordinal))
            {
                var outputPath = args[^1];
                File.WriteAllBytes(outputPath, Encoding.UTF8.GetBytes("clipped"));
                // Carry the -b 2 flag through so the test asserts the executor
                // actually selected the requested source band.
                args.Should().ContainInOrder("-b", "2");
                return new GdalCommandResult { ExitCode = 0 };
            }
            // gdalinfo on the clipped output reports a single band labeled 1.
            return new GdalCommandResult
            {
                ExitCode = 0,
                StandardOutput =
                    """{"bands":[{"band":1,"type":"Float32","minimum":7.0,"maximum":11.0,"mean":9.0,"stdDev":1.0,"validCount":4}]}"""
            };
        });

        var executor = NewZonalExecutor(runner, out var scratch);
        try
        {
            var zonesGeoJson =
                """{"type":"FeatureCollection","features":[{"type":"Feature","properties":{"id":"Z"},"geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,1],[0,0]]]}}]}""";

            var job = GdalJobFactory.Job(
                GdalRasterZonalStatisticsJobExecutor.HandledProcessId,
                ("source", Base64("fake-input-raster")),
                ("zones", Base64(zonesGeoJson)),
                ("band", "2"),
                ("statistics", "count,mean,min,max"));

            var context = new RecordingJobExecutionContext(job.OperationId);
            var result = await executor.ExecuteAsync(job, context, default);
            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);

            var scalar = ReadScalar(context.Artifacts.Single());
            using var doc = JsonDocument.Parse(scalar);

            // The artifact reports the ORIGINAL requested source band so the
            // caller can correlate the result back to the input raster.
            doc.RootElement.GetProperty("band").GetInt32().Should().Be(2);

            var zone = doc.RootElement.GetProperty("zones").EnumerateArray().Single();
            zone.GetProperty("count").GetDouble().Should().Be(4d);
            zone.GetProperty("mean").GetDouble().Should().Be(9d);
            zone.GetProperty("min").GetDouble().Should().Be(7d);
            zone.GetProperty("max").GetDouble().Should().Be(11d);
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task ZonalStatistics_MissingZones_FailsWithDeferralMessage()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewZonalExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterZonalStatisticsJobExecutor.HandledProcessId,
                ("source", Base64("fake-input-raster")));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);
            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("zones");
            result.ErrorMessage.Should().Contain("inline base64 GeoJSON");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task ZonalStatistics_RejectsUnknownStatistic_BeforeReachingTheCli()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewZonalExecutor(runner, out var scratch);
        try
        {
            var zonesGeoJson =
                """{"type":"FeatureCollection","features":[{"type":"Feature","properties":{},"geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,1],[0,0]]]}}]}""";

            var job = GdalJobFactory.Job(
                GdalRasterZonalStatisticsJobExecutor.HandledProcessId,
                ("source", Base64("fake-input-raster")),
                ("zones", Base64(zonesGeoJson)),
                ("statistics", "p95"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);
            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("p95");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    // -------------------------------------------------------------------------
    // Dispatcher routing
    // -------------------------------------------------------------------------

    [UnitTest]
    public void Dispatcher_RoutesAllSurfaceAndRasterIds()
    {
        var dispatcher = NewDispatcher();

        var allIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "surface.slope", "surface.aspect", "surface.hillshade",
            "surface.rugosity-tri", "surface.rugosity-tpi", "surface.roughness",
            "raster.clip", "raster.reproject", "raster.statistics",
            "raster.histogram", "raster.zonal-statistics",
            // Existing native-routed ids must still be present.
            "gdal.gdalwarp", "gdal.ogr2ogr",
        };

        dispatcher.SupportedProcessIds.Should().BeEquivalentTo(allIds);
        dispatcher.AcceptedRuntimeProfiles.Should().ContainSingle()
            .Which.Should().Be(RuntimeProfiles.Native);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static GdalRasterClipJobExecutor NewClipExecutor(IGdalCommandRunner runner, out string scratch)
    {
        scratch = NewScratch();
        return new GdalRasterClipJobExecutor(
            runner, GdalJobFactory.Options(scratch), NullLogger<GdalRasterClipJobExecutor>.Instance);
    }

    private static GdalRasterReprojectCatalogJobExecutor NewReprojectExecutor(IGdalCommandRunner runner, out string scratch)
    {
        scratch = NewScratch();
        return new GdalRasterReprojectCatalogJobExecutor(
            runner, GdalJobFactory.Options(scratch), NullLogger<GdalRasterReprojectCatalogJobExecutor>.Instance);
    }

    private static GdalRasterStatisticsJobExecutor NewStatsExecutor(IGdalCommandRunner runner, out string scratch)
    {
        scratch = NewScratch();
        return new GdalRasterStatisticsJobExecutor(
            runner, GdalJobFactory.Options(scratch), NullLogger<GdalRasterStatisticsJobExecutor>.Instance);
    }

    private static GdalRasterZonalStatisticsJobExecutor NewZonalExecutor(IGdalCommandRunner runner, out string scratch)
    {
        scratch = NewScratch();
        return new GdalRasterZonalStatisticsJobExecutor(
            runner, GdalJobFactory.Options(scratch), NullLogger<GdalRasterZonalStatisticsJobExecutor>.Instance);
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

    private static string ReadScalar(string artifactUri)
    {
        var bytes = GdalCli.DecodeDataUri(artifactUri);
        return Encoding.UTF8.GetString(bytes);
    }

    private static string NewScratch()
        => Path.Combine(Path.GetTempPath(), "honua-gdal-raster-test", Guid.NewGuid().ToString("N"));

    private static void CleanupScratch(string scratch)
    {
        try
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort
        }
    }
}
