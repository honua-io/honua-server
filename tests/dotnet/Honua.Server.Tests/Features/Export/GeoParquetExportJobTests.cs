// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Apache.Arrow;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Progress;
using Honua.Infrastructure.Services;
using Honua.Io.Export;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Honua.Tests.ParquetUnitSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Server.Tests.Features.Export;

[Protocol(TestProtocols.TestQuality)]
public sealed class GeoParquetExportJobTests
{
    [UnitTheory]
    [InlineData("geoparquet")]
    [InlineData("GeoParquet")]
    [InlineData("parquet")]
    [InlineData("PARQUET")]
    [Operation(Operations.Export)]
    public void BuiltInExportFormats_ResolvesGeoParquetAndParquetAlias(string token)
    {
        BuiltInExportFormats.Resolve(token).Should().Be(BuiltInExportFormat.GeoParquet);
    }

    [UnitTheory]
    [InlineData("geoparquet")]
    [InlineData("parquet")]
    [Operation(Operations.Export)]
    public async Task Execute_GeoParquetAndParquetAlias_UploadsHilbertZstdArtifactWithRowGroupsAndBboxCovering(string format)
    {
        await using var uploaded = new MemoryStream();
        string? uploadedFileName = null;
        string? uploadedContentType = null;
        string? scratchDirectory = null;

        var storage = Substitute.For<ICloudFileStorage>();
        storage.UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var req = call.Arg<FileUploadRequest>();
            uploadedFileName = req.FileName;
            uploadedContentType = req.ContentType;
            scratchDirectory = Path.GetDirectoryName(((FileStream)req.Content).Name);
            await req.Content.CopyToAsync(uploaded);
            return ExportJobTestData.Uploaded("geoparquet-artifact", req.ContentType);
        });
        storage.GetPresignedUrlAsync("geoparquet-artifact", Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns("https://example.test/export.parquet");

        var store = Substitute.For<IStreamingFeatureStore>();
        store.StreamFeaturesAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(InterleavedCoastPointsAsync());

        var progressStore = new ComparingInMemoryProgressStore();
        var job = ExportJobTestData.CreateJob(Guid.NewGuid().ToString("N")) with
        {
            Format = format,
            GeometryType = ExportGeometryType.Point,
            Fields =
            [
                new ExportField("objectid", ExportFieldType.BigInteger, false),
                new ExportField("name", ExportFieldType.String, true)
            ],
            OutputSrid = 4326,
            GeoParquetSpatial = new MetadataV2ObjectSpatial
            {
                StorageCrs = new MetadataV2SpatialReference { Srid = 4326 },
                StorageCrsCoordinateEpoch = 2024.5
            },
            TotalFeatures = 32
        };

        var limits = new LimitsOptions
        {
            GeoParquet = new GeoParquetLimits
            {
                MaxRowsPerBatch = 4,
                RowsPerRowGroup = 16,
                MaxArtifactBytes = 8 * 1024 * 1024
            }
        };

        var result = await ExecuteAsync(progressStore, store, storage, limits, job);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        uploadedFileName.Should().EndWith(".parquet");
        uploadedContentType.Should().Be(GeoParquetFeatureWriter.ContentType);
        scratchDirectory.Should().NotBeNull();
        Directory.Exists(scratchDirectory).Should().BeFalse();

        var progress = await progressStore.GetProgressAsync<ExportProgress>(job.JobId);
        progress!.Status.Should().Be(OperationStatus.Completed);
        progress.ProcessedFeatures.Should().Be(32);
        progress.DownloadUrl.Should().Be("https://example.test/export.parquet");

        uploaded.Position = 0;
        using var reader = ArrowParquetTestReader.Open(uploaded);
        reader.NumRowGroups.Should().Be(2);
        reader.Schema.Metadata[GeoParquetFeatureWriter.RowOrderMetadataKey]
            .Should().Be(GeoParquetFeatureWriter.HilbertBboxCenterOrder);
        reader.Schema.Metadata[GeoParquetFeatureWriter.RowsPerRowGroupMetadataKey]
            .Should().Be("16");

        using (var group = reader.ParquetReader.RowGroup(0))
        using (var chunk = group.MetaData.GetColumnChunkMetaData(0))
        {
            chunk.Compression.Should().Be(ParquetSharp.Compression.Zstd);
        }

        using var geo = JsonDocument.Parse(reader.Schema.Metadata["geo"]);
        var geometryColumn = geo.RootElement.GetProperty("columns").GetProperty("geometry");
        geometryColumn.GetProperty("epoch").GetDouble().Should().Be(2024.5);
        geometryColumn.GetProperty("covering").GetProperty("bbox").GetProperty("xmin")
            .EnumerateArray().Select(v => v.GetString()).Should().Equal("bbox", "xmin");

        var rows = 0;
        using var batches = reader.GetRecordBatchReader();
        while (await batches.ReadNextRecordBatchAsync() is { } batch)
        {
            using (batch)
            {
                var bbox = (StructArray)batch.Column("bbox");
                bbox.NullCount.Should().Be(0);
                rows += batch.Length;
            }
        }

        rows.Should().Be(32);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task Execute_GeoParquetExceedsArtifactBudget_FailsNonRetryableWithoutUpload()
    {
        var storage = Substitute.For<ICloudFileStorage>();
        var store = Substitute.For<IStreamingFeatureStore>();
        store.StreamFeaturesAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(InterleavedCoastPointsAsync());

        var job = ExportJobTestData.CreateJob(Guid.NewGuid().ToString("N")) with
        {
            Format = "geoparquet",
            GeometryType = ExportGeometryType.Point,
            Fields = [new ExportField("name", ExportFieldType.String, true)],
            OutputSrid = 4326,
            TotalFeatures = 32
        };

        var limits = new LimitsOptions
        {
            GeoParquet = new GeoParquetLimits { MaxArtifactBytes = 32 }
        };

        var result = await ExecuteAsync(new ComparingInMemoryProgressStore(), store, storage, limits, job);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.IsRetryable.Should().BeFalse();
        await storage.DidNotReceive().UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>());
    }

    private static async Task<JobExecutionResult> ExecuteAsync(
        IUniversalProgressStore progressStore,
        IStreamingFeatureStore featureStore,
        ICloudFileStorage storage,
        LimitsOptions limits,
        ExportJob job)
    {
        var jobStore = new InMemoryExecutionJobStore();
        await new ExportJobService(progressStore, NullLogger<ExportJobService>.Instance, jobStore, Substitute.For<IJobQueue>())
            .StartAsync(job);
        var record = (await jobStore.GetAsync(job.JobId))!;

        using var services = new ServiceCollection()
            .AddSingleton(featureStore)
            .AddSingleton(storage)
            .AddSingleton<ICrsRegistry>(new ExportJobTestData.NullCrsRegistry())
            .AddSingleton<IOptions<LimitsOptions>>(Options.Create(limits))
            .BuildServiceProvider();

        var executor = new ExportJobExecutor(
            progressStore,
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ExportJobExecutor>.Instance);
        var context = Substitute.For<IJobExecutionContext>();
        context.TryPublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        return await executor.ExecuteAsync(record, context, CancellationToken.None);
    }

    private static async IAsyncEnumerable<Feature> InterleavedCoastPointsAsync()
    {
        var writer = new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: false, emitM: false);
        for (var i = 0; i < 16; i++)
        {
            var west = writer.Write(new Point(-120 + (i % 2) * 0.0001, 35));
            var east = writer.Write(new Point(10 + (i % 2) * 0.0001, 50));
            yield return Feature.Create(i + 1, west,
                ImmutableDictionary<string, object?>.Empty
                    .Add("objectid", (long)(i + 1))
                    .Add("name", $"west-{i.ToString(CultureInfo.InvariantCulture)}"));
            yield return Feature.Create(1_000 + i, east,
                ImmutableDictionary<string, object?>.Empty
                    .Add("objectid", (long)(1_000 + i))
                    .Add("name", $"east-{i.ToString(CultureInfo.InvariantCulture)}"));
        }

        await Task.CompletedTask;
    }
}
