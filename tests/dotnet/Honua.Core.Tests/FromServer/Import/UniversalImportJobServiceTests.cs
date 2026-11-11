// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
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
    public async Task QueueImportAsync_SuccessfulImport_RecordsCompletedProgressWithFeatureCount()
    {
        var progressStore = new ComparingInMemoryProgressStore();
        var importService = new FakeFileImportService(
            (_, _, _) => Task.FromResult(ImportResult.CreateSuccess(
                "widgets", SupportedFileFormat.GeoJson, featureCount: 3, warnings: ["1 feature repaired."],
                physicalTableName: "imp_widgets_123", schema: "imports")));
        using var jobService = CreateService(importService, progressStore);

        using var stream = new MemoryStream([0x01]);
        var jobId = await jobService.QueueImportAsync(
            new ImportRequest { FileStream = stream, FileName = "widgets.geojson", TableName = "widgets", TargetSrid = 4326 },
            fileSize: 1);

        var progress = await WaitForTerminalProgressAsync(jobService, jobId);

        progress.Status.Should().Be(ImportStatus.Completed);
        progress.FeaturesProcessed.Should().Be(3);
        progress.Warnings.Should().Equal("1 feature repaired.");
        progress.CurrentPhase.Should().Be("Import completed");
        progress.CompletedAt.Should().NotBeNull();
        var receipt = JsonSerializer.SerializeToElement(progress, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        receipt.TryGetProperty("schema", out var schema).Should().BeTrue();
        schema.GetString().Should().Be("imports");
        receipt.TryGetProperty("physicalTableName", out var table).Should().BeTrue();
        table.GetString().Should().Be("imp_widgets_123");
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public async Task QueueImportAsync_CompletedTickBeforeResult_RemainsProcessingUntilReceiptExists()
    {
        var store = new ComparingInMemoryProgressStore();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var importer = new FakeFileImportService(async (_, progress, token) =>
        {
            progress!.Report(ImportProgress.CreateInitial("tick", "widgets", SupportedFileFormat.GeoJson) with
            {
                Status = ImportStatus.Completed,
                FeaturesProcessed = 3
            });
            await release.Task.WaitAsync(token);
            return ImportResult.CreateSuccess("widgets", SupportedFileFormat.GeoJson, 3,
                physicalTableName: "imp_widgets", schema: "imports");
        });
        using var service = CreateService(importer, store);
        using var stream = new MemoryStream([1]);
        var id = await service.QueueImportAsync(new ImportRequest
        {
            FileStream = stream, FileName = "widgets.geojson", TableName = "widgets"
        }, 1);
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            ImportProgress? observed;
            do
            {
                await Task.Delay(10, budget.Token);
                observed = await service.GetProgressAsync(id);
            } while (observed?.FeaturesProcessed != 3);
            observed.Status.Should().Be(ImportStatus.Processing);
        }
        finally
        {
            release.TrySetResult();
        }

        var receipt = await WaitForTerminalProgressAsync(service, id);
        receipt.Schema.Should().Be("imports");
        receipt.PhysicalTableName.Should().Be("imp_widgets");
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public async Task QueueImportAsync_FailedImport_RecordsFailedProgressWithSanitizedMessage()
    {
        var progressStore = new ComparingInMemoryProgressStore();
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
        AssertNoPublicationReceipt(progress);
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public async Task QueueImportAsync_OwnershipConflict_PreservesStableCodeWithoutProviderDetails()
    {
        var progressStore = new ComparingInMemoryProgressStore();
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
        var progressStore = new ComparingInMemoryProgressStore();
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
        AssertNoPublicationReceipt((await jobService.GetProgressAsync(jobId))!);

        var cancelled = await jobService.CancelJobAsync(jobId);
        cancelled.Should().BeTrue();

        var observedCancellation = await importerObservedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(10));
        observedCancellation.Should().BeTrue("the importer's own cancellation token must be signalled, not just the progress record updated");

        var progress = await WaitForTerminalProgressAsync(jobService, jobId);
        progress.Status.Should().Be(ImportStatus.Cancelled);
        progress.CurrentPhase.Should().Be("Cancelled");
        AssertNoPublicationReceipt(progress);
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public async Task CancelJobAsync_AfterImporterReturnsSuccess_RecordsCompletedWithCommittedWarning()
    {
        var progressStore = new ComparingInMemoryProgressStore();
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
        var progressStore = new ComparingInMemoryProgressStore();
        var importService = new FakeFileImportService(
            (_, _, _) => Task.FromResult(ImportResult.CreateSuccess("widgets", SupportedFileFormat.GeoJson, featureCount: 0)));
        using var jobService = CreateService(importService, progressStore);

        (await jobService.CancelJobAsync("no-such-job")).Should().BeFalse();
    }

    private static void AssertNoPublicationReceipt(ImportProgress progress)
    {
        var receipt = JsonSerializer.SerializeToElement(progress, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var name in new[] { "schema", "physicalTableName", "connectionId" })
        {
            if (receipt.TryGetProperty(name, out var value))
            {
                value.ValueKind.Should().Be(JsonValueKind.Null, $"{progress.Status} jobs cannot be published from a completion receipt");
            }
        }
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

}
