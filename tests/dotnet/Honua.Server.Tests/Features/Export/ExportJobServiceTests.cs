// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Progress;
using Honua.Io.Export;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Export;

[Protocol(TestProtocols.TestQuality)]
public sealed class ExportJobServiceTests
{
    [UnitTest]
    [Operation(Operations.Export)]
    public async Task Start_CreatesAQueuedExportJobCarryingTheRequestAndSubmitter()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var jobQueue = Substitute.For<IJobQueue>();
        var progressStore = new ComparingInMemoryProgressStore();
        var job = ExportJobTestData.CreateJob(Guid.NewGuid().ToString("N"));

        await CreateService(progressStore, jobStore, jobQueue).StartAsync(job);

        var record = await jobStore.GetAsync(job.JobId);
        record.Should().NotBeNull();
        record!.Status.Should().Be(ExecutionJobStatus.Queued);
        record.Spec.Kind.Should().Be(ExecutionJobKind.Export);
        record.Audit.SubmitterSecurityContext.Should().BeEquivalentTo(job.Submitter);
        record.Spec.Parameters[ExportJobService.RequestParameter].Should().NotContain(
            "subject-a",
            "the submitter travels on the record's audit, not in the request parameter");
        ExportJobService.TryReadRequest(record, out var request).Should().BeTrue();
        request!.Should().BeEquivalentTo(job with { Submitter = null });
        await jobQueue.Received(1).EnqueueAsync(job.JobId, Arg.Any<OperationPriority>(), Arg.Any<CancellationToken>());
        (await progressStore.GetProgressAsync<ExportProgress>(job.JobId))!.Status.Should().Be(OperationStatus.Queued);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task Start_WithoutAJobStore_IsRefused()
    {
        var progressStore = new ComparingInMemoryProgressStore();
        var sut = new ExportJobService(progressStore, NullLogger<ExportJobService>.Instance);

        var start = () => sut.StartAsync(ExportJobTestData.CreateJob("no-store"));

        await start.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(ExportJobService.DurableQueueUnavailableMessage);
        (await progressStore.GetProgressAsync("no-store")).Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task Start_WhenProgressCannotBeWritten_CreatesNoJob()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var jobQueue = Substitute.For<IJobQueue>();
        var progressStore = Substitute.For<IUniversalProgressStore>();
        progressStore
            .SetProgressAsync(Arg.Any<string>(), Arg.Any<IOperationProgress>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Simulated progress-store failure.")));
        var job = ExportJobTestData.CreateJob("headless");

        var start = () => CreateService(progressStore, jobStore, jobQueue).StartAsync(job);

        await start.Should().ThrowAsync<InvalidOperationException>();
        (await jobStore.GetAsync(job.JobId)).Should().BeNull();
        await jobQueue.DidNotReceiveWithAnyArgs().EnqueueAsync(default!);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task Start_WhenEnqueueFails_KeepsTheAcceptedJobForDispatchRepair()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.EnqueueAsync(Arg.Any<string>(), Arg.Any<OperationPriority>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new TimeoutException("Simulated queue timeout.")));
        var job = ExportJobTestData.CreateJob("deferred");

        var jobId = await CreateService(new ComparingInMemoryProgressStore(), jobStore, jobQueue).StartAsync(job);

        jobId.Should().Be(job.JobId);
        (await jobStore.GetAsync(job.JobId))!.Status.Should().Be(ExecutionJobStatus.Queued);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task Execute_CsvArtifact_PreservesValuesNullsAndZmOrdinatesAndCompletesTheProgress()
    {
        string? artifact = null;
        var storage = Substitute.For<ICloudFileStorage>();
        storage.UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            using var reader = new StreamReader(call.Arg<FileUploadRequest>().Content, leaveOpen: true);
            artifact = await reader.ReadToEndAsync();
            return ExportJobTestData.Uploaded("csv-proof", "text/csv");
        });
        storage.GetPresignedUrlAsync("csv-proof", Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns("https://example.test/proof.csv");
        var store = Substitute.For<IStreamingFeatureStore>();
        store.StreamFeaturesAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(FixtureFeatures());
        var progressStore = new ComparingInMemoryProgressStore();
        var job = ExportJobTestData.CreateJob(Guid.NewGuid().ToString("N")) with
        {
            Fields = [new ExportField("name", ExportFieldType.String, true), new ExportField("depth", ExportFieldType.Double, true)],
            TotalFeatures = 99,
            GeometryType = ExportGeometryType.Point
        };

        var result = await ExecuteAsync(progressStore, store, storage, job);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        artifact.Should().NotBeNull();
        var lines = artifact!.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(3);
        lines[0].Should().Be("name,depth,WKT");
        lines[2].Should().Be("Unlocated,,", "null property and geometry are represented as empty CSV cells");
        var cells = lines[1].Split(',');
        cells.Should().HaveCount(3);
        cells[0].Should().Be("Harbor");
        cells[1].Should().Be("12.5");
        var geometry = new NetTopologySuite.IO.WKTReader().Read(cells[2]);
        geometry.GeometryType.Should().Be("Point");
        var coordinate = geometry.Coordinates.Should().ContainSingle().Subject;
        coordinate.X.Should().Be(-157.5);
        coordinate.Y.Should().Be(21.25);
        coordinate.Z.Should().Be(12.5);
        coordinate.M.Should().Be(7.25);
        var progress = await progressStore.GetProgressAsync<ExportProgress>(job.JobId);
        progress!.Status.Should().Be(OperationStatus.Completed);
        progress.ProcessedFeatures.Should().Be(2, "the receipt counts rows actually exported, not the estimated 99");
        progress.DownloadUrl.Should().Be("https://example.test/proof.csv");
        progress.Warnings.Should().BeEmpty();
        Directory.Exists(Path.Join(Path.GetTempPath(), "honua-export", job.JobId)).Should().BeFalse();

        static async IAsyncEnumerable<Feature> FixtureFeatures()
        {
            // ISO WKB: little-endian PointZM (3001), followed by X/Y/Z/M doubles.
            // Encoded independently from the export writer; expected ordinates below are fixture constants.
            yield return Feature.Create(1, Convert.FromHexString("01B90B00000000000000B063C0000000000040354000000000000029400000000000001D40"),
                ImmutableDictionary<string, object?>.Empty.Add("name", "Harbor").Add("depth", 12.5));
            yield return Feature.Create(2, null,
                ImmutableDictionary<string, object?>.Empty.Add("name", "Unlocated").Add("depth", null));
            await Task.CompletedTask;
        }
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task Execute_ShapefileNullRow_PreservesRowAndSurfacesWarningsAndActualCount()
    {
        await using var uploaded = new MemoryStream();
        var storage = Substitute.For<ICloudFileStorage>();
        storage.UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await call.Arg<FileUploadRequest>().Content.CopyToAsync(uploaded);
            return ExportJobTestData.Uploaded("file-1", "application/zip");
        });
        storage.GetPresignedUrlAsync("file-1", Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns("https://example.test/export.zip");
        var store = Substitute.For<IStreamingFeatureStore>();
        store.StreamFeaturesAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(WarningFeatures());
        var progressStore = new ComparingInMemoryProgressStore();
        var job = ExportJobTestData.CreateJob(Guid.NewGuid().ToString("N")) with
        {
            Format = "shapefile",
            GeometryType = ExportGeometryType.Point,
            Fields = [new ExportField("name", ExportFieldType.String, true), new ExportField("descriptive_name", ExportFieldType.String, true)],
            TotalFeatures = 99
        };

        var result = await ExecuteAsync(progressStore, store, storage, job);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        var progress = await progressStore.GetProgressAsync<ExportProgress>(job.JobId);
        progress!.Status.Should().Be(OperationStatus.Completed);
        progress.DownloadUrl.Should().Be("https://example.test/export.zip");
        progress.ProcessedFeatures.Should().Be(1);
        progress.Warnings.Should().Contain(warning => warning.Contains("descriptive_name", StringComparison.Ordinal));
        uploaded.Position = 0;
        using var archive = new System.IO.Compression.ZipArchive(uploaded, System.IO.Compression.ZipArchiveMode.Read);
        var warningEntry = archive.GetEntry("export-warnings.txt");
        warningEntry.Should().NotBeNull();
        using (var warningReader = new StreamReader(warningEntry!.Open()))
        {
            (await warningReader.ReadToEndAsync()).Should().Contain("descriptive_name");
        }

        // Independently decode the shapefile records: one Null Shape occupies a 100-byte
        // header, an 8-byte record header and a 4-byte shape type; SHX stores offset/length in words.
        var shp = await ReadEntryAsync(archive, "export.shp");
        shp.Should().HaveCount(112);
        System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(shp.AsSpan(0, 4)).Should().Be(9994);
        System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(shp.AsSpan(108, 4)).Should().Be(0);
        var shx = await ReadEntryAsync(archive, "export.shx");
        shx.Should().HaveCount(108);
        System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(shx.AsSpan(100, 4)).Should().Be(50);
        System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(shx.AsSpan(104, 4)).Should().Be(2);
        var dbf = await ReadEntryAsync(archive, "export.dbf");
        System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(dbf.AsSpan(4, 4)).Should().Be(1);
        var rowOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(dbf.AsSpan(8, 2));
        rowOffset.Should().Be(97, "two field descriptors plus the fixed header and terminator");
        dbf[rowOffset].Should().Be((byte)' ', "the preserved null-geometry row is not deleted");
        var nameLength = dbf[32 + 16];
        var detailLength = dbf[64 + 16];
        System.Text.Encoding.UTF8.GetString(dbf, rowOffset + 1, nameLength).TrimEnd().Should().Be("Unlocated");
        System.Text.Encoding.UTF8.GetString(dbf, rowOffset + 1 + nameLength, detailLength).TrimEnd().Should().Be("Retained detail");

        static async IAsyncEnumerable<Feature> WarningFeatures()
        {
            yield return Feature.Create(1, geometry: null,
                ImmutableDictionary<string, object?>.Empty.Add("name", "Unlocated").Add("descriptive_name", "Retained detail"));
            await Task.CompletedTask;
        }

        static async Task<byte[]> ReadEntryAsync(System.IO.Compression.ZipArchive zip, string name)
        {
            var entry = zip.GetEntry(name);
            entry.Should().NotBeNull();
            using var bytes = new MemoryStream();
            await using var source = entry!.Open();
            await source.CopyToAsync(bytes);
            return bytes.ToArray();
        }
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task Execute_CancelledWhileTheFileWasWritten_RetractsTheArtifact()
    {
        var progressStore = new ComparingInMemoryProgressStore();
        var job = ExportJobTestData.CreateJob(Guid.NewGuid().ToString("N"));
        var storage = Substitute.For<ICloudFileStorage>();
        storage.UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            // An operator cancels on another node after the file was written.
            var running = (await progressStore.GetProgressAsync<ExportProgress>(job.JobId))!;
            await progressStore.SetProgressAsync(job.JobId, running.WithCancellation(DateTimeOffset.UtcNow, "Cancelled by user"));
            return ExportJobTestData.Uploaded("late-file", "text/csv");
        });
        storage.GetPresignedUrlAsync("late-file", Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns("https://example.test/late.csv");
        var store = Substitute.For<IStreamingFeatureStore>();
        store.StreamFeaturesAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(ExportJobTestData.CreateFeatures());

        var execute = () => ExecuteAsync(progressStore, store, storage, job);

        await execute.Should().ThrowAsync<OperationCanceledException>();
        await storage.Received(1).DeleteAsync("late-file", Arg.Any<CancellationToken>());
        (await progressStore.GetProgressAsync<ExportProgress>(job.JobId))!.Status.Should().Be(OperationStatus.Cancelled);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task Execute_WhenTheLayerCannotBeRead_FailsWithoutRetry()
    {
        var store = Substitute.For<IStreamingFeatureStore>();
        store.StreamFeaturesAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("Simulated provider failure."));
        var storage = Substitute.For<ICloudFileStorage>();

        var result = await ExecuteAsync(
            new ComparingInMemoryProgressStore(), store, storage, ExportJobTestData.CreateJob(Guid.NewGuid().ToString("N")));

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Be(ExportJobExecutor.ExportFailureMessage);
        result.IsRetryable.Should().BeFalse();
        await storage.DidNotReceive().UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task Execute_WithoutASubmitter_FailsBeforeReadingFeatures()
    {
        var store = Substitute.For<IStreamingFeatureStore>();
        var job = ExportJobTestData.CreateJob("anonymous") with { Submitter = null };

        var result = await ExecuteAsync(new ComparingInMemoryProgressStore(), store, Substitute.For<ICloudFileStorage>(), job);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.IsRetryable.Should().BeFalse();
        store.ReceivedCalls().Should().BeEmpty();
    }

    [UnitTheory]
    [Operation(Operations.Export)]
    [InlineData(ExecutionJobStatus.Failed, OperationStatus.Failed)]
    [InlineData(ExecutionJobStatus.Cancelled, OperationStatus.Cancelled)]
    public async Task TerminalCallback_JobEndedByTheWorker_ClosesTheExportProgress(
        ExecutionJobStatus jobStatus,
        OperationStatus expected)
    {
        var progressStore = new ComparingInMemoryProgressStore();
        var running = ExportProgress.CreateInitial("export-1", "csv", "svc", 1, 10) with { Status = OperationStatus.Processing };
        await progressStore.SetProgressAsync("export-1", running);
        var now = DateTimeOffset.UtcNow;
        var callback = new ExportJobTerminalCallback(progressStore, NullLogger<ExportJobTerminalCallback>.Instance);

        await callback.OnTerminalAsync(new ExecutionJobRecord
        {
            OperationId = "export-1",
            Status = jobStatus,
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = now,
            ErrorMessage = "license expired",
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Export,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "export:csv:svc:1"
            }
        }, CancellationToken.None);

        var closed = (await progressStore.GetProgressAsync<ExportProgress>("export-1"))!;
        closed.Status.Should().Be(expected);
        closed.CompletedAt.Should().Be(now);
        if (expected == OperationStatus.Failed)
        {
            closed.ErrorMessage.Should().Be("license expired");
        }
    }

    private static ExportJobService CreateService(
        IUniversalProgressStore progressStore,
        IExecutionJobStore jobStore,
        IJobQueue jobQueue)
        => new(progressStore, NullLogger<ExportJobService>.Instance, jobStore, jobQueue);

    // Submits through the service so the executor reads the request the way a worker does.
    private static async Task<JobExecutionResult> ExecuteAsync(
        IUniversalProgressStore progressStore,
        IStreamingFeatureStore store,
        ICloudFileStorage storage,
        ExportJob job)
    {
        var jobStore = new InMemoryExecutionJobStore();
        await CreateService(progressStore, jobStore, Substitute.For<IJobQueue>()).StartAsync(job);
        using var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton(storage)
            .AddSingleton<ICrsRegistry>(new ExportJobTestData.NullCrsRegistry())
            .BuildServiceProvider();
        var executor = new ExportJobExecutor(
            progressStore,
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ExportJobExecutor>.Instance);
        return await executor.ExecuteAsync(
            (await jobStore.GetAsync(job.JobId))!,
            Substitute.For<IJobExecutionContext>(),
            CancellationToken.None);
    }
}

internal static class ExportJobTestData
{
    public static ExportJob CreateJob(string jobId)
        => new(
            jobId,
            "svc",
            1,
            "layer",
            "csv",
            new FeatureQuery(),
            [],
            4326,
            1,
            ExportGeometryType.None,
            new JobSecurityContext("subject-a", "tenant-a", [new JobSecurityClaim("role", "reader")]));

    public static async IAsyncEnumerable<Feature> CreateFeatures()
    {
        yield return Feature.Create(
            1,
            geometry: null,
            ImmutableDictionary<string, object?>.Empty.Add("name", "Test feature"));
        await Task.CompletedTask;
    }

    public static UploadResult Uploaded(string fileId, string contentType)
        => UploadResult.CreateSuccess(new CloudFile
        {
            FileId = fileId,
            FileName = fileId,
            StoragePath = $"exports/{fileId}",
            ContentType = contentType,
            SizeBytes = 32,
            UploadedAt = DateTimeOffset.UtcNow,
            Provider = CloudStorageProvider.AwsS3
        });

    internal sealed class NullCrsRegistry : ICrsRegistry
    {
        public ValueTask<CrsDefinition?> ResolveAsync(string? crsIdentifier, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<CrsDefinition?>(null);

        public ValueTask<CrsDefinition?> ResolveBySridAsync(int srid, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<CrsDefinition?>(null);

        public ValueTask<bool> IsSridSupportedAsync(int srid, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
    }
}
