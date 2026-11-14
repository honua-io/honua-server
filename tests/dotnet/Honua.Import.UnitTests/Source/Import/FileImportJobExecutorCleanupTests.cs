// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.ObjectStore;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Import.FileImport.Durable;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Import;

public sealed class FileImportJobExecutorCleanupTests
{
    private const long SixGiB = 6L * 1024 * 1024 * 1024;

    [UnitTest]
    public async Task ExecuteAsync_CompletedTickBeforeResult_RemainsProcessingUntilReceiptExists()
    {
        var progress = new MemoryProgress();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var importer = new ScriptedImporter(async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return ImportResult.CreateSuccess("roads", SupportedFileFormat.GeoPackage, 3,
                physicalTableName: "imp_roads", schema: "imports");
        }, sink => sink!.Report(ImportProgress.CreateInitial("tick", "roads", SupportedFileFormat.GeoPackage) with
        {
            Status = ImportStatus.Completed,
            FeaturesProcessed = 3
        }));
        var execution = ExecuteAsync(new RecordingStorage(), importer, SixGiB, progress);
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (progress.Last is not ImportProgress { FeaturesProcessed: 3 })
            {
                await Task.Delay(10, budget.Token);
            }

            ((ImportProgress)progress.Last).Status.Should().Be(ImportStatus.Processing);
        }
        finally
        {
            release.TrySetResult();
            await execution;
        }

        var receipt = (ImportProgress)progress.Last!;
        receipt.Schema.Should().Be("imports");
        receipt.PhysicalTableName.Should().Be("imp_roads");
    }

    [UnitTest]
    public async Task SuccessfulImport_PreservesPhysicalTableReceipt()
    {
        var progress = new MemoryProgress();
        var importer = new ScriptedImporter((_, _) => Task.FromResult(ImportResult.CreateSuccess(
            "roads", SupportedFileFormat.GeoPackage, 3, physicalTableName: "imp_roads_123", schema: "imports")));

        var result = await ExecuteAsync(new RecordingStorage(), importer, SixGiB, progress: progress);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        var terminal = progress.Last.Should().BeOfType<ImportProgress>().Subject;
        terminal.Status.Should().Be(ImportStatus.Completed);
        terminal.FeaturesProcessed.Should().Be(3);
        var receipt = JsonSerializer.SerializeToElement(terminal, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        receipt.TryGetProperty("schema", out var schema).Should().BeTrue();
        schema.GetString().Should().Be("imports");
        receipt.TryGetProperty("physicalTableName", out var table).Should().BeTrue();
        table.GetString().Should().Be("imp_roads_123");
    }

    [UnitTest]
    public async Task SuccessfulImport_ReceiptReportsSourceAndStoredCoordinateSystem()
    {
        var progress = new MemoryProgress();
        var importer = new ScriptedImporter((_, _) => Task.FromResult(
            ImportResult.CreateSuccess("roads", SupportedFileFormat.GeoPackage, 3, detectedSrid: 2193) with { SourceSrid = 2193, StoredSrid = 4326, Reprojected = true }));

        await ExecuteAsync(new RecordingStorage(), importer, SixGiB, progress: progress);

        var receipt = JsonSerializer.SerializeToElement(
            progress.Last.Should().BeOfType<ImportProgress>().Subject,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        receipt.GetProperty("detectedSrid").GetInt32().Should().Be(2193);
        receipt.GetProperty("sourceSrid").GetInt32().Should().Be(2193);
        receipt.GetProperty("storedSrid").GetInt32().Should().Be(4326);
        receipt.GetProperty("reprojected").GetBoolean().Should().BeTrue();
    }

    [UnitTest]
    public async Task StagedCopy_IsDeletedAfterSuccess()
    {
        var storage = new RecordingStorage();
        var importer = new ScriptedImporter((_, _) => Task.FromResult(ImportResult.CreateSuccess("roads", SupportedFileFormat.GeoPackage, 1)));

        var result = await ExecuteAsync(storage, importer, fileSize: SixGiB);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        storage.Upload!.SizeBytes.Should().Be(SixGiB);
        storage.Upload.TimeToLive.Should().Be(ImportObjectStoreMaterializer.StagedCopyLifetime);
        storage.Deletes.Should().ContainSingle()
            .Which.Should().Be(("staged-copy", CancellationToken.None));
        storage.DeletedWhileScopeAlive.Should().BeTrue();
    }

    [UnitTest]
    public async Task StagedCopy_IsDeletedAfterImportFailure()
    {
        var storage = new RecordingStorage();
        var importer = new ScriptedImporter((_, _) =>
            Task.FromResult(ImportResult.CreateFailure("roads", SupportedFileFormat.GeoPackage, "bad file")));

        var result = await ExecuteAsync(storage, importer, fileSize: SixGiB);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        storage.Deletes.Should().ContainSingle()
            .Which.FileId.Should().Be("staged-copy");
        storage.Deletes[0].Token.Should().Be(CancellationToken.None);
    }

    [UnitTest]
    public async Task StagedCopy_IsDeletedWhenTheImportIsCancelled()
    {
        var storage = new RecordingStorage();
        using var cancelled = new CancellationTokenSource();
        var importer = new ScriptedImporter((_, token) =>
        {
            cancelled.Cancel();
            throw new OperationCanceledException(token);
        });

        var act = () => ExecuteAsync(storage, importer, fileSize: SixGiB, cancellationToken: cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        storage.Deletes.Should().ContainSingle()
            .Which.Token.Should().Be(CancellationToken.None);
        storage.DeletedWhileScopeAlive.Should().BeTrue();
    }

    private static Task<JobExecutionResult> ExecuteAsync(
        RecordingStorage storage,
        ScriptedImporter importer,
        long fileSize,
        MemoryProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        var request = new ImportRequest
        {
            ObjectStoreUri = "s3://bucket/layers/roads.gpkg",
            ObjectStoreETag = "\"pin\"",
            FileName = "roads.gpkg",
            TableName = "roads",
            SourceKind = "object-store",
        };
        var parameters = FileImportJobParameters.Build(request, fileSize);
        parameters[FileImportJobParameters.Runner] = "in-process";
        var now = DateTimeOffset.UtcNow;
        var job = new ExecutionJobRecord
        {
            OperationId = "job-1",
            Status = ExecutionJobStatus.Running,
            CreatedAt = now,
            UpdatedAt = now,
            Spec = new ExecutionJobSpec
            {
                TargetKind = BatchComputeTargetKind.AwsBatch,
                Backend = "in-process",
                Kind = ExecutionJobKind.FileImport,
                WorkloadName = "file-import",
                Parameters = parameters,
            },
        };
        var scope = new ProbeScope(storage, importer);
        storage.Scope = scope;
        var executor = new FileImportJobExecutor(
            new SingleScopeFactory(scope),
            progress ?? new MemoryProgress(),
            NullLogger<FileImportJobExecutor>.Instance);
        return executor.ExecuteAsync(job, new RecordingContext("job-1"), cancellationToken);
    }

    private sealed class ScriptedImporter(
        Func<ImportRequest, CancellationToken, Task<ImportResult>> run,
        Action<IProgress<ImportProgress>?>? report = null) : IFileImportService
    {
        public ImportLimits Limits { get; } = new();

        public SupportedFileFormat? DetectFormat(string fileName) => null;

        public string[] GetSupportedExtensions() => [];

        public Task<ImportResult> ImportFileAsync(ImportRequest request, CancellationToken cancellationToken = default)
            => run(request, cancellationToken);

        public Task<ImportResult> ImportFileAsync(
            ImportRequest request,
            IProgress<ImportProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            report?.Invoke(progress);
            return run(request, cancellationToken);
        }

        public Task<FilePreview> PreviewFileAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class RecordingContext(string operationId) : IJobExecutionContext
    {
        public string OperationId { get; } = operationId;

        public Task ReportProgressAsync(double? percentComplete, string? phase, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task AppendLogAsync(ExecutionLogEntry entry, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishArtifactAsync(string artifactReference, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class MemoryProgress : IUniversalProgressStore
    {
        public IOperationProgress? Last { get; private set; }

        public Task SetProgressAsync(
            string operationId,
            IOperationProgress progress,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
        {
            Last = progress;
            return Task.CompletedTask;
        }

        public Task<ProgressCompareAndSetResult> TrySetProgressAsync(OperationProgressWrite write, OperationStatus expectedStatus, CancellationToken cancellationToken = default)
            => Task.FromResult(ProgressCompareAndSetResult.Updated);

        public Task<TProgress?> GetProgressAsync<TProgress>(string operationId, CancellationToken cancellationToken = default)
            where TProgress : class, IOperationProgress
            => Task.FromResult(Last as TProgress);

        public Task<IOperationProgress?> GetProgressAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(Last);

        public Task DeleteProgressAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<string>> GetActiveOperationIdsAsync(
            OperationType? operationType = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<TProgress>> GetActiveOperationsAsync<TProgress>(
            OperationType operationType,
            CancellationToken cancellationToken = default)
            where TProgress : class, IOperationProgress
            => Task.FromResult<IReadOnlyList<TProgress>>([]);
    }

    private sealed class SingleScopeFactory(ProbeScope scope) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => scope;
    }

    private sealed class ProbeScope : IServiceScope, IAsyncDisposable
    {
        private readonly RecordingStorage _storage;
        private readonly ScriptedImporter _importer;

        public ProbeScope(RecordingStorage storage, ScriptedImporter importer)
        {
            _storage = storage;
            _importer = importer;
            ServiceProvider = new ProbeProvider(this);
        }

        public bool Disposed { get; private set; }

        public IServiceProvider ServiceProvider { get; }

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        private sealed class ProbeProvider(ProbeScope scope) : IServiceProvider
        {
            public object? GetService(Type serviceType)
            {
                if (serviceType == typeof(IImportObjectStoreReader))
                {
                    return new BytesReader();
                }

                if (serviceType == typeof(ICloudFileStorage))
                {
                    return scope._storage;
                }

                if (serviceType == typeof(IOptions<ImportObjectStoreOptions>))
                {
                    return Options.Create(new ImportObjectStoreOptions { AllowedBuckets = ["bucket"] });
                }

                if (serviceType == typeof(IFileImportService))
                {
                    return scope._importer;
                }

                return null;
            }
        }
    }

    private sealed class BytesReader : IImportObjectStoreReader
    {
        public Task<ImportObjectHead> HeadAsync(ImportObjectLocation location, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(ImportObjectLocation location, string etag, CancellationToken cancellationToken)
            => Task.FromResult<Stream>(new MemoryStream([1, 2, 3, 4], writable: false));
    }

    private sealed class RecordingStorage : ICloudFileStorage
    {
        public FileUploadRequest? Upload { get; private set; }

        public List<(string FileId, CancellationToken Token)> Deletes { get; } = [];

        public bool DeletedWhileScopeAlive { get; private set; }

        public ProbeScope? Scope { get; set; }

        public CloudStorageProvider Provider => CloudStorageProvider.AwsS3;

        public Task<UploadResult> UploadAsync(FileUploadRequest request, CancellationToken cancellationToken = default)
        {
            Upload = request;
            return Task.FromResult(UploadResult.CreateSuccess(new CloudFile
            {
                FileId = "staged-copy",
                FileName = request.FileName,
                StoragePath = "imports/staged-copy",
                ContentType = request.ContentType,
                SizeBytes = request.SizeBytes ?? 0,
                UploadedAt = DateTimeOffset.UtcNow,
                Provider = CloudStorageProvider.AwsS3,
            }));
        }

        public Task<UploadResult> UploadAsync(ByteArrayUploadRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UploadProgress?> GetUploadProgressAsync(string uploadId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> CancelUploadAsync(string uploadId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<UploadProgress>> GetActiveUploadsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Stream?> DownloadAsync(string fileId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<byte[]?> DownloadBytesAsync(string fileId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken = default)
        {
            Deletes.Add((fileId, cancellationToken));
            DeletedWhileScopeAlive = Scope is { Disposed: false };
            return Task.FromResult(true);
        }

        public Task<BatchUploadResult> UploadBatchAsync(BatchUploadRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> DeleteBatchAsync(string batchId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CloudFile?> GetMetadataAsync(string fileId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> ExistsAsync(string fileId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<CloudFile>> ListFilesAsync(
            string? folder = null,
            int maxResults = 1000,
            bool includeMetadata = true,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<string?> GetPresignedUrlAsync(string fileId, TimeSpan? expiresIn = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<(string Url, string FileId)?> GetPresignedUploadUrlAsync(
            string fileName,
            string contentType,
            TimeSpan? expiresIn = null,
            string? folder = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> CleanupExpiredFilesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
