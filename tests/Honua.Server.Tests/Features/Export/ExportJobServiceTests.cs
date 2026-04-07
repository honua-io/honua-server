// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Export;
using Honua.Server.Features.Infrastructure.Progress;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Export;

[Protocol(Protocols.TestQuality)]
public sealed class ExportJobServiceTests
{
    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ReadQueuedJobIdsAsync_WithRecoveredProcessingJob_RequeuesPersistedJob()
    {
        var progressStore = new InMemoryUniversalProgressStore();
        var requestCache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var channel = Channel.CreateUnbounded<string>();
        using var services = new ServiceCollection().BuildServiceProvider();
        var sut = new ExportJobService(
            progressStore,
            requestCache,
            channel,
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ExportJobService>.Instance);

        var job = CreateJob("recover-export");
        await requestCache.SetStringAsync(
            "export:request:recover-export",
            JsonSerializer.Serialize(
                new ExportJobService.PersistedExportJobRequest { Job = job },
                ExportJsonContext.Default.PersistedExportJobRequest));
        await progressStore.SetProgressAsync(
            job.JobId,
            ExportProgress.CreateInitial(job.JobId, job.Format, job.ServiceName, job.LayerId, job.TotalFeatures) with
            {
                Status = OperationStatus.Processing,
                CurrentPhase = "Exporting features"
            });

        await using var enumerator = sut.ReadQueuedJobIdsAsync().GetAsyncEnumerator();
        var hasRecoveredJob = await enumerator.MoveNextAsync();

        hasRecoveredJob.Should().BeTrue();
        enumerator.Current.Should().Be(job.JobId);

        var recoveredProgress = await progressStore.GetProgressAsync<ExportProgress>(job.JobId);
        recoveredProgress.Should().NotBeNull();
        recoveredProgress!.Status.Should().Be(OperationStatus.Queued);
        recoveredProgress.CurrentPhase.Should().Be("Recovered after worker restart");
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ProcessQueuedJobAsync_WhenJobCompletes_RemovesPersistedRequestAndCompletesProgress()
    {
        var progressStore = new InMemoryUniversalProgressStore();
        var requestCache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var channel = Channel.CreateUnbounded<string>();

        var streamingStore = Substitute.For<IStreamingFeatureStore>();
        streamingStore
            .StreamFeaturesAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(CreateFeatures());

        var crsRegistry = new NullCrsRegistry();

        var cloudStorage = Substitute.For<ICloudFileStorage>();
        cloudStorage.UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(UploadResult.CreateSuccess(new CloudFile
            {
                FileId = "file-1",
                FileName = "export.csv",
                StoragePath = "exports/export.csv",
                ContentType = "text/csv",
                SizeBytes = 32,
                UploadedAt = DateTimeOffset.UtcNow,
                Provider = CloudStorageProvider.AwsS3
            }));
        cloudStorage.GetPresignedUrlAsync("file-1", Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns("https://example.test/export.csv");

        using var services = new ServiceCollection()
            .AddSingleton<IStreamingFeatureStore>(streamingStore)
            .AddSingleton<ICrsRegistry>(crsRegistry)
            .AddSingleton<ICloudFileStorage>(cloudStorage)
            .BuildServiceProvider();

        var sut = new ExportJobService(
            progressStore,
            requestCache,
            channel,
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ExportJobService>.Instance);

        var job = CreateJob("complete-export");
        await sut.StartAsync(job);

        await sut.ProcessQueuedJobAsync(job.JobId);

        var persistedRequest = await requestCache.GetStringAsync("export:request:complete-export");
        persistedRequest.Should().BeNull();

        var progress = await progressStore.GetProgressAsync<ExportProgress>(job.JobId);
        progress.Should().NotBeNull();
        progress!.Status.Should().Be(OperationStatus.Completed);
        progress.DownloadUrl.Should().Be("https://example.test/export.csv");
    }

    private static async IAsyncEnumerable<Feature> CreateFeatures()
    {
        yield return Feature.Create(
            1,
            geometry: null,
            ImmutableDictionary<string, object?>.Empty.Add("name", "Test feature"));
        await Task.CompletedTask;
    }

    private static ExportJob CreateJob(string jobId)
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
            GeometryType.None);

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

    private sealed class NullCrsRegistry : ICrsRegistry
    {
        public ValueTask<CrsDefinition?> ResolveAsync(string? crsIdentifier, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<CrsDefinition?>(null);

        public ValueTask<CrsDefinition?> ResolveBySridAsync(int srid, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<CrsDefinition?>(null);

        public ValueTask<bool> IsSridSupportedAsync(int srid, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
    }
}
