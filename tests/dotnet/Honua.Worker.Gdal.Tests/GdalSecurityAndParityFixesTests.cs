// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Scene.PointCloud;
using Honua.TestKit.Attributes;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Xunit;

namespace Honua.Worker.Gdal.Tests;

public sealed class GdalSecurityAndParityFixesTests
{
    private const string ScratchSuite = "honua-gdal-security-parity-test";

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData("../other-bucket", "a/b.nc")]
    [InlineData("bucket/sub", "a/b.nc")]
    [InlineData("bucket", "../escape.nc")]
    [InlineData("bucket", "a\\b.nc")]
    [InlineData("bucket", "a/b.nc?foo=bar")]
    [InlineData("bucket", "%2e%2e/secret.nc")]
    public void GdalVsiPath_Build_UnsafeBucketOrKey_Throws(string bucket, string objectKey)
    {
        var act = () => GdalVsiPath.Build(CloudStorageProvider.AwsS3, bucket, objectKey);
        act.Should().Throw<InvalidOperationException>();
    }

    [UnitTest]
    public void GdalVsiPath_LocalStorageRoot_RejectsAbsolutePathOutsideRoot()
    {
        var safeRoot = Path.Join(Path.GetTempPath(), $"honua-safe-root-{Guid.NewGuid():N}");
        var outside = Path.Join(Path.GetTempPath(), $"honua-outside-{Guid.NewGuid():N}");

        var act = () => GdalVsiPath.Build(CloudStorageProvider.Local, outside, "cube.nc", safeRoot);
        act.Should().Throw<InvalidOperationException>().WithMessage("*local storage root*");
    }

    [UnitTest]
    public async Task MultidimExecutor_RejectsDisallowedBucketAndUnsafePaths()
    {
        var scratch = GdalCli.NewScratch(ScratchSuite);
        try
        {
            var opts = new GdalWorkerOptions
            {
                ScratchRoot = scratch,
                AllowedCoverageBuckets = ["approved-bucket"],
            };
            var monitor = GdalJobFactory.OptionsMonitor(opts);
            var runner = FakeGdalCommandRunner.Failing(1, "should not run");
            var executor = new GdalMultidimCoverageMetadataJobExecutor(
                runner, monitor, NullLogger<GdalMultidimCoverageMetadataJobExecutor>.Instance);

            var disallowedBucketJob = GdalJobFactory.Job(
                GdalMultidimCoverageMetadataJobExecutor.HandledProcessId,
                ("provider", "AwsS3"),
                ("bucket", "other-bucket"),
                ("objectKey", "data/file.nc"));
            var result = await executor.ExecuteAsync(
                disallowedBucketJob, new RecordingJobExecutionContext(disallowedBucketJob.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("AllowedCoverageBuckets");
            runner.Invocations.Should().BeEmpty();

            var localEscapeJob = GdalJobFactory.Job(
                GdalMultidimCoverageMetadataJobExecutor.HandledProcessId,
                ("provider", "Local"),
                ("bucket", Path.GetTempPath()),
                ("objectKey", "etc/passwd"));
            var localResult = await executor.ExecuteAsync(
                localEscapeJob, new RecordingJobExecutionContext(localEscapeJob.OperationId), default);

            localResult.Status.Should().Be(ExecutionJobStatus.Failed);
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            GdalCli.CleanupScratch(scratch);
        }
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData("/etc/passwd")]
    [InlineData("https://evil.example/crs.wkt")]
    [InlineData("EPSG:4326\",\"injected\":true")]
    [InlineData("EPSG:4326\n")]
    public void ZonalStatistics_TryExtractGeoJsonCrsName_RejectsUnsafeTokens(string maliciousCrs)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "FeatureCollection",
            crs = new { type = "name", properties = new { name = maliciousCrs } },
            features = Array.Empty<object>(),
        });

        GdalRasterZonalStatisticsJobExecutor.TryExtractGeoJsonCrsName(payload, out _, out var error)
            .Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [UnitTest]
    public void ZonalStatistics_TryExtractGeoJsonCrsName_NormalizesUrnAndEpsg()
    {
        var payload = Encoding.UTF8.GetBytes(
            """{"type":"FeatureCollection","crs":{"type":"name","properties":{"name":"urn:ogc:def:crs:EPSG::3857"}},"features":[]}""");

        GdalRasterZonalStatisticsJobExecutor.TryExtractGeoJsonCrsName(payload, out var crsName, out var error)
            .Should().BeTrue();
        error.Should().BeEmpty();
        crsName.Should().Be("EPSG:3857");
    }

    [UnitTest]
    public void RasterClip_WriteCutlineGeoJson_OmitsCrsForZeroSrid_AndRejectsInvalidCrs()
    {
        var polygon = new GeometryFactory().CreatePolygon(
        [
            new Coordinate(500_000, 4_000_000),
            new Coordinate(500_100, 4_000_000),
            new Coordinate(500_100, 4_000_100),
            new Coordinate(500_000, 4_000_100),
            new Coordinate(500_000, 4_000_000),
        ]);
        polygon.SRID = 0;

        var geoJson = GdalRasterClipJobExecutor.WriteCutlineGeoJson(polygon);
        using var doc = JsonDocument.Parse(geoJson);
        doc.RootElement.TryGetProperty("crs", out _).Should().BeFalse(
            "SRID-less cutlines must not fabricate EPSG:3857");

        var invalid = () => GdalRasterClipJobExecutor.WriteCutlineGeoJson(polygon, "https://evil.example/crs");
        invalid.Should().Throw<ArgumentException>();
    }

    [UnitTest]
    public void OutputGridGuard_NonFiniteExtent_FailsClosed()
    {
        GdalOutputGridGuard.TryAdmitResolution(double.PositiveInfinity, 100, 1, 1, new GdalWorkerOptions(), out var infError)
            .Should().BeFalse();
        infError.Should().Contain("finite");

        GdalOutputGridGuard.TryAdmitResolution(100, double.NaN, 1, 1, new GdalWorkerOptions(), out var nanError)
            .Should().BeFalse();
        nanError.Should().Contain("finite");
    }

    [UnitTest]
    public async Task Rasterize_NonGeoJsonWithCellSize_FailsClosed_AndNoDataAddsInit()
    {
        var scratch = GdalCli.NewScratch(ScratchSuite);
        try
        {
            var failingRunner = FakeGdalCommandRunner.Failing(1, "should not run");
            var failExecutor = new GdalRasterizeJobExecutor(
                failingRunner, GdalJobFactory.Options(scratch), NullLogger<GdalRasterizeJobExecutor>.Instance);

            var nonGeoJsonJob = GdalJobFactory.Job(
                GdalRasterizeJobExecutor.HandledProcessId,
                ("source", GdalCli.Base64("SQLite format 3\0fake-gpkg")),
                ("burnValue", "1"),
                ("cellSize", "0.001"));
            var nonGeoJsonResult = await failExecutor.ExecuteAsync(
                nonGeoJsonJob, new RecordingJobExecutionContext(nonGeoJsonJob.OperationId), default);

            nonGeoJsonResult.Status.Should().Be(ExecutionJobStatus.Failed);
            nonGeoJsonResult.ErrorMessage.Should().Contain("extent");
            failingRunner.Invocations.Should().BeEmpty();

            var okRunner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("tif"));
            var okExecutor = new GdalRasterizeJobExecutor(
                okRunner, GdalJobFactory.Options(scratch), NullLogger<GdalRasterizeJobExecutor>.Instance);
            var validGeoJson =
                """{"type":"FeatureCollection","features":[{"type":"Feature","properties":{},"geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,1],[0,0]]]}}]}""";
            var noDataJob = GdalJobFactory.Job(
                GdalRasterizeJobExecutor.HandledProcessId,
                ("source", GdalCli.Base64(validGeoJson)),
                ("burnValue", "5"),
                ("cellSize", "0.1"),
                ("nodata", "-9999"));
            var noDataResult = await okExecutor.ExecuteAsync(
                noDataJob, new RecordingJobExecutionContext(noDataJob.OperationId), default);

            noDataResult.Status.Should().Be(ExecutionJobStatus.Succeeded, noDataResult.ErrorMessage);
            var args = okRunner.Invocations.Single().Arguments;
            args.Should().ContainInOrder("-init", "-9999");
            args.Should().ContainInOrder("-a_nodata", "-9999");
        }
        finally
        {
            GdalCli.CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task PdalConvert_PointCountCeilingAndOutputSizeGuard_RejectBeforeOom()
    {
        var scratch = GdalCli.NewScratch(ScratchSuite);
        try
        {
            var header = new byte[260];
            Encoding.ASCII.GetBytes("LASF").CopyTo(header, 0);
            header[24] = 1;
            header[25] = 4;
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(94, 2), 375);
            header[104] = 6;
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(105, 2), 30);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(247, 8), 50_000_000UL);

            LasPublicHeader.TryReadPointCount(header, out var count, out var recLen).Should().BeTrue();
            count.Should().Be(50_000_000UL);
            recLen.Should().Be(30);

            var runner = FakeGdalCommandRunner.Failing(1, "should not run");
            var executor = new PdalPointCloudConvertJobExecutor(
                runner,
                GdalJobFactory.OptionsMonitor(new GdalWorkerOptions
                {
                    ScratchRoot = scratch,
                    MaxPointCloudPoints = 1_000_000L,
                }),
                NullLogger<PdalPointCloudConvertJobExecutor>.Instance);

            var job = GdalJobFactory.Job(
                PdalPointCloudConvertJobExecutor.HandledProcessId,
                ("source", Convert.ToBase64String(header)));
            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("MaxPointCloudPoints");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            GdalCli.CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public void RasterDimensionGuard_ThreeBandClassicTiff_ReadsOutOfLineBitsPerSampleArray()
    {
        // Classic LE TIFF with 4 IFD entries; BitsPerSample count=3 (6 bytes > 4 inline)
        // stored out-of-line at offset 64 containing [16, 16, 16].
        var bytes = new byte[80];
        bytes[0] = (byte)'I';
        bytes[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2, 2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), 4);

        WriteIfdEntry(bytes, 10, tag: 256, type: 4, count: 1, valueOrOffset: 100);
        WriteIfdEntry(bytes, 22, tag: 257, type: 4, count: 1, valueOrOffset: 100);
        WriteIfdEntry(bytes, 34, tag: 258, type: 3, count: 3, valueOrOffset: 64);
        WriteIfdEntry(bytes, 46, tag: 277, type: 3, count: 1, valueOrOffset: 3);

        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(64, 2), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(66, 2), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(68, 2), 16);

        GdalRasterDimensionGuard.TryReadGeoTiffDimensions(bytes, out var dims).Should().BeTrue();
        dims.Bands.Should().Be(3);
        dims.BitsPerSample.Should().Be(16, "must dereference the out-of-line SHORT array instead of reading offset 64");
    }

    [UnitTest]
    public void ProcessGdalCommandRunner_ApplyHardenedEnvironment_ScrubsSensitiveHostVariables()
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = "/usr/bin",
            ["ConnectionStrings__Default"] = "Host=secret;Password=pw",
            ["PGPASSWORD"] = "pg-secret",
            ["HONUA_LICENSE_KEY"] = "lic-123",
            ["CUSTOM_API_TOKEN"] = "tok-xyz",
        };
        var hardening = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GDAL_SKIP"] = "VRT",
        };

        ProcessGdalCommandRunner.ApplyHardenedEnvironment(env, hardening, new AwsS3Options(), new AzureBlobOptions());

        env.Should().ContainKey("PATH");
        env.Should().ContainKey("GDAL_SKIP");
        env.Should().NotContainKey("ConnectionStrings__Default");
        env.Should().NotContainKey("PGPASSWORD");
        env.Should().NotContainKey("HONUA_LICENSE_KEY");
        env.Should().NotContainKey("CUSTOM_API_TOKEN");
    }

    [UnitTest]
    public void ProcessDockerCommandInvoker_EnsureRunContainerName_InjectsUniqueName()
    {
        var (args, name) = ProcessDockerCommandInvoker.EnsureRunContainerName(["run", "--rm", "image:tag"]);

        name.Should().NotBeNullOrWhiteSpace().And.StartWith("honua-gdal-");
        args.Should().ContainInOrder("run", "--name", name!, "--rm", "image:tag");
    }

    [UnitTest]
    public async Task GdalProximity_CopiesDistanceScriptIntoWorkspace()
    {
        var scratch = GdalCli.NewScratch(ScratchSuite);
        try
        {
            var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("prox"));
            var executor = new GdalProximityJobExecutor(
                runner, GdalJobFactory.Options(scratch), NullLogger<GdalProximityJobExecutor>.Instance);
            var job = GdalJobFactory.Job(
                GdalProximityJobExecutor.DistanceProcessId,
                ("source", GdalCli.RasterBase64("fake-raster")));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            var invocation = runner.Invocations.Single();
            invocation.Arguments[0].Should().StartWith(scratch);
            invocation.Arguments[0].Should().EndWith("gdal_euclidean_distance.py");
        }
        finally
        {
            GdalCli.CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public void MapAlgebraExpression_HardeningAndSyntaxFixes()
    {
        MapAlgebraExpression.IsAllowed("9**9**9 + A", 1, out var powError).Should().BeFalse();
        powError.Should().Contain("**");

        MapAlgebraExpression.IsAllowed("(A << 99999)", 1, out var shiftError).Should().BeFalse();
        shiftError.Should().Contain("<<");

        MapAlgebraExpression.IsAllowed("nan_to_num(A) * 1e-6 + 2.5E+3", 1, out var validError).Should().BeTrue();
        validError.Should().BeEmpty();
    }

    private static void WriteIfdEntry(byte[] buffer, int offset, ushort tag, ushort type, uint count, uint valueOrOffset)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), tag);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 2, 2), type);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 4, 4), count);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 8, 4), valueOrOffset);
    }
}
