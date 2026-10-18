// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Import;

/// <summary>
/// honua-server#4422 — <c>UniversalImportJobService</c> (the shipped, DI-registered
/// <see cref="IImportJobService"/>; see ServiceCollectionExtensions.cs) is 720 lines with zero
/// tests exercising it directly. These cover the fire-and-forget lifecycle end to end: a
/// successful background import, a failed one, and mid-flight cancellation, each verified
/// through the same <see cref="IUniversalProgressStore"/> contract real callers observe rather
/// than through internal state.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class UniversalImportJobServiceTests
{
    [UnitTest]
    [Operation(Operations.Import)]
    public async Task ProviderProgress_CannotReplaceAcceptingOwnership()
    {
        var store = new InMemoryUniversalProgressStore();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new ImportOperationOwnership("accepting-actor", "tenant-a");
        var importer = new FakeFileImportService(async (_, progress, token) =>
        {
            progress!.Report(ImportProgress.CreateInitial("provider-id", "roads", SupportedFileFormat.GeoJson,
                ownership: new ImportOperationOwnership("provider-actor", "tenant-b")) with
            {
                Status = ImportStatus.Processing,
                FeaturesProcessed = 17,
            });
            await release.Task.WaitAsync(token);
            return ImportResult.CreateSuccess("roads", SupportedFileFormat.GeoJson, 17);
        });
        using var service = CreateService(importer, store);
        using var stream = new MemoryStream([1]);
        var id = await service.QueueImportAsync(new ImportRequest
        {
            FileStream = stream,
            FileName = "roads.geojson",
            TableName = "roads",
            Ownership = owner,
        }, 1);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            ImportProgress? observed;
            do
            {
                await Task.Delay(10, timeout.Token);
                observed = await service.GetProgressAsync(id, timeout.Token);
            } while (observed?.FeaturesProcessed != 17);
            observed.JobId.Should().Be(id);
            observed.Ownership.Should().Be(owner);
        }
        finally
        {
            release.TrySetResult();
        }

        (await WaitForTerminalProgressAsync(service, id)).Ownership.Should().Be(owner);
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public async Task QueueImportAsync_SuccessfulImport_RecordsCompletedProgressWithFeatureCount()
    {
        var progressStore = new InMemoryUniversalProgressStore();
        var importService = new FakeFileImportService(
            (_, _, _) => Task.FromResult(ImportResult.CreateSuccess(
                "widgets", SupportedFileFormat.GeoJson, featureCount: 3, warnings: ["1 feature repaired."],
                physicalTableName: "actual_widgets", schema: "tenant_data")));
        using var jobService = CreateService(importService, progressStore);

        using var stream = new MemoryStream([0x01]);
        var jobId = await jobService.QueueImportAsync(
            new ImportRequest
            {
                FileStream = stream,
                FileName = "widgets.geojson",
                TableName = "widgets",
                TargetSrid = 4326,
                Ownership = new ImportOperationOwnership("accepting-actor", "tenant-a")
            },
            fileSize: 1);

        var progress = await WaitForTerminalProgressAsync(jobService, jobId);

        progress.Ownership.Should().Be(new ImportOperationOwnership("accepting-actor", "tenant-a"));
        progress.Status.Should().Be(ImportStatus.Completed);
        progress.FeaturesProcessed.Should().Be(3);
        progress.PhysicalTableName.Should().Be("actual_widgets");
        progress.Schema.Should().Be("tenant_data");
        progress.Warnings.Should().Equal("1 feature repaired.");
        progress.CurrentPhase.Should().Be("Import completed");
        progress.CompletedAt.Should().NotBeNull();
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public async Task QueueImportAsync_FailedImport_RecordsFailedProgressWithSanitizedMessage()
    {
        var progressStore = new InMemoryUniversalProgressStore();
        var importService = new FakeFileImportService(
            (_, _, _) => Task.FromResult(ImportResult.CreateFailure(
                "widgets", SupportedFileFormat.GeoJson, "relation \"widgets\" already exists; leaked internal detail")));
        using var jobService = CreateService(importService, progressStore);

        using var stream = new MemoryStream([0x01]);
        var jobId = await jobService.QueueImportAsync(
            new ImportRequest { FileStream = stream, FileName = "widgets.geojson", TableName = "widgets", TargetSrid = 4326 },
            fileSize: 1);

        var progress = await WaitForTerminalProgressAsync(jobService, jobId);

        progress.Status.Should().Be(ImportStatus.Failed);
        // The background path deliberately replaces the provider's own error message with the
        // generic client-safe one - never the raw ImportResult.ErrorMessage, which can carry
        // provider/schema internals (see SafeImportFailureMessage in UniversalImportJobService).
        progress.ErrorMessage.Should().Be("Import failed.");
        progress.CurrentPhase.Should().Be("Import failed");
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public async Task QueueImportAsync_OwnershipConflict_PreservesStableCodeWithoutProviderDetails()
    {
        var progressStore = new InMemoryUniversalProgressStore();
        var importService = new FakeFileImportService(
            (_, _, _) => Task.FromResult(ImportResult.CreateFailure(
                "widgets", SupportedFileFormat.GeoJson, "SQL and connection details must not escape",
                errorCode: ImportTableOwnershipException.Code)));
        using var jobService = CreateService(importService, progressStore);
        using var stream = new MemoryStream([0x01]);
        var jobId = await jobService.QueueImportAsync(
            new ImportRequest { FileStream = stream, FileName = "widgets.geojson", TableName = "widgets" }, 1);

        var progress = await WaitForTerminalProgressAsync(jobService, jobId);

        progress.Status.Should().Be(ImportStatus.Failed);
        progress.ErrorCode.Should().Be(ImportTableOwnershipException.Code);
        progress.ErrorMessage.Should().Be(ImportTableOwnershipException.CreateSafeMessage("widgets"));
        progress.ErrorMessage.Should().NotContain("SQL");
        progress.CompletedAt.Should().NotBeNull();
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public async Task CancelJobAsync_WhileImportIsRunning_RecordsCancelledProgressAndStopsTheImporter()
    {
        var progressStore = new InMemoryUniversalProgressStore();
        var releaseImporter = new TaskCompletionSource();
        var importerObservedCancellation = new TaskCompletionSource<bool>();
        var importService = new FakeFileImportService(async (_, _, cancellationToken) =>
        {
            try
            {
                await releaseImporter.Task.WaitAsync(cancellationToken);
                return ImportResult.CreateSuccess("widgets", SupportedFileFormat.GeoJson, featureCount: 0);
            }
            catch (OperationCanceledException)
            {
                importerObservedCancellation.TrySetResult(true);
                throw;
            }
        });
        using var jobService = CreateService(importService, progressStore);

        using var stream = new MemoryStream([0x01]);
        var jobId = await jobService.QueueImportAsync(
            new ImportRequest { FileStream = stream, FileName = "widgets.geojson", TableName = "widgets", TargetSrid = 4326 },
            fileSize: 1);

        // Wait for the background job to actually reach the importer before cancelling, so the
        // cancellation exercises the "running" path rather than racing QueueImportAsync itself.
        await WaitForStatusAsync(jobService, jobId, ImportStatus.Processing);

        var cancelled = await jobService.CancelJobAsync(jobId);
        cancelled.Should().BeTrue();

        var observedCancellation = await importerObservedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(10));
        observedCancellation.Should().BeTrue("the importer's own cancellation token must be signalled, not just the progress record updated");

        var progress = await WaitForTerminalProgressAsync(jobService, jobId);
        progress.Status.Should().Be(ImportStatus.Cancelled);
        progress.CurrentPhase.Should().Be("Cancelled");
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public async Task CancelJobAsync_AfterImporterReturnsSuccess_RecordsCompletedWithCommittedWarning()
    {
        var progressStore = new InMemoryUniversalProgressStore();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var importService = new FakeFileImportService(async (_, _, _) =>
        {
            started.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return ImportResult.CreateSuccess("widgets", SupportedFileFormat.GeoJson, featureCount: 4);
        });
        using var jobService = CreateService(importService, progressStore);

        using var stream = new MemoryStream([0x01]);
        var jobId = await jobService.QueueImportAsync(
            new ImportRequest { FileStream = stream, FileName = "widgets.geojson", TableName = "widgets", TargetSrid = 4326 },
            fileSize: 1);

        await WaitForStatusAsync(jobService, jobId, ImportStatus.Processing);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        (await jobService.CancelJobAsync(jobId)).Should().BeTrue();
        release.TrySetResult();

        var progress = await WaitForTerminalProgressAsync(jobService, jobId);
        progress.Status.Should().Be(ImportStatus.Completed);
        progress.FeaturesProcessed.Should().Be(4);
        progress.Warnings.Should().Contain(
            "Import was cancelled after the data was committed. The imported rows were kept.");
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public async Task CancelJobAsync_ForUnknownJobId_ReturnsFalse()
    {
        var progressStore = new InMemoryUniversalProgressStore();
        var importService = new FakeFileImportService(
            (_, _, _) => Task.FromResult(ImportResult.CreateSuccess("widgets", SupportedFileFormat.GeoJson, featureCount: 0)));
        using var jobService = CreateService(importService, progressStore);

        (await jobService.CancelJobAsync("no-such-job")).Should().BeFalse();
    }

    private static UniversalImportJobService CreateService(
        IFileImportService importService,
        IUniversalProgressStore progressStore)
    {
        var services = new ServiceCollection();
        services.AddSingleton(importService);
        // Deliberately not disposed here: UniversalImportJobService creates scopes from this
        // provider on its own fire-and-forget background task for as long as a queued job is
        // still running, so the provider must outlive this factory method's stack frame.
        var provider = services.BuildServiceProvider();
        return new UniversalImportJobService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            progressStore,
            new NoopPerformanceMonitor(),
            NullLogger<UniversalImportJobService>.Instance);
    }

    private static async Task<ImportProgress> WaitForTerminalProgressAsync(UniversalImportJobService jobService, string jobId)
    {
        var deadline = DateTimeOffset.UtcNow.Add(TimeSpan.FromSeconds(10));
        while (DateTimeOffset.UtcNow < deadline)
        {
            var progress = await jobService.GetProgressAsync(jobId);
            if (progress is { Status: ImportStatus.Completed or ImportStatus.Failed or ImportStatus.Cancelled })
            {
                return progress;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        throw new TimeoutException($"Import job {jobId} did not reach a terminal status in time.");
    }

    private static async Task WaitForStatusAsync(UniversalImportJobService jobService, string jobId, ImportStatus status)
    {
        var deadline = DateTimeOffset.UtcNow.Add(TimeSpan.FromSeconds(10));
        while (DateTimeOffset.UtcNow < deadline)
        {
            var progress = await jobService.GetProgressAsync(jobId);
            if (progress?.Status == status)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }

        throw new TimeoutException($"Import job {jobId} did not reach status {status} in time.");
    }

    private sealed class FakeFileImportService(
        Func<ImportRequest, IProgress<ImportProgress>?, CancellationToken, Task<ImportResult>> importFileAsync)
        : IFileImportService
    {
        public ImportLimits Limits { get; } = new();

        public SupportedFileFormat? DetectFormat(string fileName) => SupportedFileFormat.GeoJson;

        public string[] GetSupportedExtensions() => [".geojson"];

        public Task<ImportResult> ImportFileAsync(ImportRequest request, CancellationToken cancellationToken = default)
            => ImportFileAsync(request, progress: null, cancellationToken);

        public Task<ImportResult> ImportFileAsync(
            ImportRequest request, IProgress<ImportProgress>? progress, CancellationToken cancellationToken = default)
            => importFileAsync(request, progress, cancellationToken);

        public Task<FilePreview> PreviewFileAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default)
            => Task.FromResult(new FilePreview { Format = SupportedFileFormat.GeoJson, TotalFeatureCount = 0 });
    }

    private sealed class InMemoryUniversalProgressStore : IUniversalProgressStore
    {
        private readonly ConcurrentDictionary<string, IOperationProgress> _entries = new(StringComparer.Ordinal);

        public Task SetProgressAsync(
            string operationId,
            IOperationProgress progress,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
        {
            _entries[operationId] = progress;
            return Task.CompletedTask;
        }

        public Task<ProgressCompareAndSetResult> TrySetProgressAsync(
            string operationId,
            IOperationProgress progress,
            OperationStatus expectedStatus,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
        {
            while (true)
            {
                if (!_entries.TryGetValue(operationId, out var current))
                {
                    return Task.FromResult(ProgressCompareAndSetResult.NotFound);
                }

                if (current.Status != expectedStatus)
                {
                    return Task.FromResult(ProgressCompareAndSetResult.StatusMismatch(current));
                }

                if (_entries.TryUpdate(operationId, progress, current))
                {
                    return Task.FromResult(ProgressCompareAndSetResult.Updated);
                }
            }
        }

        public Task<TProgress?> GetProgressAsync<TProgress>(string operationId, CancellationToken cancellationToken = default)
            where TProgress : class, IOperationProgress
        {
            if (_entries.TryGetValue(operationId, out var progress) && progress is TProgress typed)
            {
                return Task.FromResult<TProgress?>(typed);
            }

            return Task.FromResult<TProgress?>(null);
        }

        public Task<IOperationProgress?> GetProgressAsync(string operationId, CancellationToken cancellationToken = default)
        {
            _entries.TryGetValue(operationId, out var progress);
            return Task.FromResult(progress);
        }

        public Task DeleteProgressAsync(string operationId, CancellationToken cancellationToken = default)
        {
            _entries.TryRemove(operationId, out _);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetActiveOperationIdsAsync(OperationType? operationType = null, CancellationToken cancellationToken = default)
        {
            var ids = _entries
                .Where(kvp => operationType == null || kvp.Value.Type == operationType.Value)
                .Select(static kvp => kvp.Key)
                .ToArray();
            return Task.FromResult<IReadOnlyList<string>>(ids);
        }

        public Task<IReadOnlyList<TProgress>> GetActiveOperationsAsync<TProgress>(OperationType operationType, CancellationToken cancellationToken = default)
            where TProgress : class, IOperationProgress
        {
            var operations = _entries.Values
                .Where(progress => progress.Type == operationType)
                .OfType<TProgress>()
                .ToArray();
            return Task.FromResult<IReadOnlyList<TProgress>>(operations);
        }
    }
}
