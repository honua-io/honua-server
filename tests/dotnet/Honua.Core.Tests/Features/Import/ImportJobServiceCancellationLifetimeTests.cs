// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Reflection;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Import;

public sealed class ImportJobServiceCancellationLifetimeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InMemoryService_FinishedJob_RemovesCancellationSource(bool succeeds)
    {
        using var service = CreateInMemory(new ResultImportService(succeeds));

        var jobId = await service.QueueImportAsync(CreateRequest(), 0);
        await WaitUntilAsync(() => GetCancellationSourceCount(service) == 0);

        (await service.CancelJobAsync(jobId)).Should().BeFalse();
        var act = service.Dispose;
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UniversalService_FinishedJob_RemovesCancellationSource(bool succeeds)
    {
        using var provider = CreateProvider(new ResultImportService(succeeds));
        var store = new MemoryProgressStore();
        using var service = CreateUniversal(provider, store);

        var jobId = await service.QueueImportAsync(CreateRequest(), 0);
        await WaitUntilAsync(() => GetCancellationSourceCount(service) == 0);

        (await service.CancelJobAsync(jobId)).Should().BeFalse();
        var act = service.Dispose;
        act.Should().NotThrow();
    }

    [Fact]
    public async Task InMemoryService_DisposeAfterCancellation_DoesNotThrow()
    {
        var importer = new CancellationBlockingImportService();
        var service = CreateInMemory(importer);
        var jobId = await service.QueueImportAsync(CreateRequest(), 0);
        await importer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        (await service.CancelJobAsync(jobId)).Should().BeTrue();
        await WaitUntilAsync(() => GetCancellationSourceCount(service) == 0);

        var act = service.Dispose;
        act.Should().NotThrow();
    }

    [Fact]
    public async Task UniversalService_DisposeAfterCancellation_DoesNotThrow()
    {
        var importer = new CancellationBlockingImportService();
        using var provider = CreateProvider(importer);
        var service = CreateUniversal(provider, new MemoryProgressStore());
        var jobId = await service.QueueImportAsync(CreateRequest(), 0);
        await importer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        (await service.CancelJobAsync(jobId)).Should().BeTrue();
        await WaitUntilAsync(() => GetCancellationSourceCount(service) == 0);

        var act = service.Dispose;
        act.Should().NotThrow();
    }

    [Fact]
    public void InMemoryService_DisposeWithOutOfBandDisposedSource_DoesNotThrow()
    {
        var service = CreateInMemory(new ResultImportService(true));
        AddDisposedCancellationSource(service);

        var act = service.Dispose;

        act.Should().NotThrow();
        GetCancellationSourceCount(service).Should().Be(0);
    }

    [Fact]
    public void UniversalService_DisposeWithOutOfBandDisposedSource_DoesNotThrow()
    {
        using var provider = CreateProvider(new ResultImportService(true));
        var service = CreateUniversal(provider, new MemoryProgressStore());
        AddDisposedCancellationSource(service);

        var act = service.Dispose;

        act.Should().NotThrow();
        GetCancellationSourceCount(service).Should().Be(0);
    }

    private static InMemoryImportJobService CreateInMemory(IFileImportService importer) =>
        new(importer, new NoopPerformanceMonitor(), NullLogger<InMemoryImportJobService>.Instance);

    private static UniversalImportJobService CreateUniversal(IServiceProvider provider, IUniversalProgressStore store) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(), store, new NoopPerformanceMonitor(),
            NullLogger<UniversalImportJobService>.Instance);

    private static ServiceProvider CreateProvider(IFileImportService importer) =>
        new ServiceCollection().AddSingleton(importer).BuildServiceProvider();

    private static ImportRequest CreateRequest() => new()
    {
        LocalFilePath = Path.Join(Path.GetTempPath(), $"unused-{Guid.NewGuid():N}.geojson"),
        FileName = "input.geojson",
        TableName = "import_target",
        TargetSrid = 4326
    };

    private static int GetCancellationSourceCount(object service) => GetCancellationSources(service).Count;

    private static void AddDisposedCancellationSource(object service)
    {
        var source = new CancellationTokenSource();
        source.Dispose();
        GetCancellationSources(service).TryAdd("stale", source).Should().BeTrue();
    }

    private static ConcurrentDictionary<string, CancellationTokenSource> GetCancellationSources(object service)
    {
        var field = service.GetType().GetField("_cancellationTokens", BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull();
        return (ConcurrentDictionary<string, CancellationTokenSource>)field!.GetValue(service)!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= timeout)
            {
                throw new TimeoutException("Timed out waiting for import job cleanup.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class ResultImportService(bool succeeds) : ImportServiceBase
    {
        public override Task<ImportResult> ImportFileAsync(
            ImportRequest request,
            IProgress<ImportProgress>? progress,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(succeeds
                ? ImportResult.CreateSuccess(request.TableName, SupportedFileFormat.GeoJson, 1)
                : ImportResult.CreateFailure(request.TableName, SupportedFileFormat.GeoJson, "failure"));
    }

    private sealed class CancellationBlockingImportService : ImportServiceBase
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<ImportResult> ImportFileAsync(
            ImportRequest request,
            IProgress<ImportProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private abstract class ImportServiceBase : IFileImportService
    {
        public ImportLimits Limits { get; } = new();
        public SupportedFileFormat? DetectFormat(string fileName) => SupportedFileFormat.GeoJson;
        public string[] GetSupportedExtensions() => [".geojson"];
        public Task<ImportResult> ImportFileAsync(ImportRequest request, CancellationToken cancellationToken = default) =>
            ImportFileAsync(request, null, cancellationToken);
        public abstract Task<ImportResult> ImportFileAsync(
            ImportRequest request,
            IProgress<ImportProgress>? progress,
            CancellationToken cancellationToken = default);
        public Task<FilePreview> PreviewFileAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FilePreview { Format = SupportedFileFormat.GeoJson, TotalFeatureCount = 0 });
    }

    private sealed class MemoryProgressStore : IUniversalProgressStore
    {
        private readonly ConcurrentDictionary<string, IOperationProgress> _progress = new();
        public Task SetProgressAsync(string id, IOperationProgress progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _progress[id] = progress;
            return Task.CompletedTask;
        }
        public Task<T?> GetProgressAsync<T>(string id, CancellationToken cancellationToken = default) where T : class, IOperationProgress =>
            Task.FromResult(_progress.TryGetValue(id, out var value) ? value as T : null);
        public Task<IOperationProgress?> GetProgressAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_progress.TryGetValue(id, out var value) ? value : null);
        public Task DeleteProgressAsync(string id, CancellationToken cancellationToken = default)
        {
            _progress.TryRemove(id, out _);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<string>> GetActiveOperationIdsAsync(OperationType? operationType = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<T>> GetActiveOperationsAsync<T>(OperationType operationType, CancellationToken cancellationToken = default) where T : class, IOperationProgress =>
            Task.FromResult<IReadOnlyList<T>>([]);
        public Task<ProgressCompareAndSetResult> TrySetProgressAsync(string operationId, IOperationProgress progress, OperationStatus expectedStatus, TimeSpan? ttl = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(ProgressCompareAndSetResult.NotFound);
    }
}
