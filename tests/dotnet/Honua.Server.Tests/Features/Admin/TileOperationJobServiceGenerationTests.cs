// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Tiles;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Progress;
using Honua.Server.Features.Admin.TileOperations;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Evidence that <see cref="TileOperationJobService"/> stamps a stable generation id on the first
/// seed submission and that <see cref="TileOperationJobService.RetryAsync"/> preserves that
/// generation across the newly minted job id (issue #2661), so a retry resumes the same
/// generation checkpoint rather than forking a fresh full-grid pass.
/// </summary>
public sealed class TileOperationJobServiceGenerationTests
{
    [Fact]
    public async Task StartAsync_StampsGenerationId_AndRetryPreservesIt()
    {
        using var serviceProvider = CreateServiceProvider();
        var progressStore = new InMemoryUniversalProgressStore();
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var sut = CreateSut(progressStore, serviceProvider.GetRequiredService<IServiceScopeFactory>(), cache);

        // "UnsupportedSet" makes the seed fail deterministically so the job becomes retryable.
        var failedJobId = await sut.StartAsync(new TileOperationStartRequest
        {
            Operation = "seed",
            LayerId = 1,
            TileMatrixSetId = "UnsupportedSet"
        });

        await sut.ProcessQueuedJobAsync(failedJobId);
        (await sut.GetAsync(failedJobId))!.Status.Should().Be(OperationStatus.Failed);

        var originalGeneration = await ReadPersistedGenerationIdAsync(cache, failedJobId);
        originalGeneration.Should().Be(failedJobId, "the first submission stamps the job id as the generation id");

        var retryJobId = await sut.RetryAsync(failedJobId);
        retryJobId.Should().NotBeNullOrWhiteSpace();
        retryJobId.Should().NotBe(failedJobId);

        var retryGeneration = await ReadPersistedGenerationIdAsync(cache, retryJobId!);
        retryGeneration.Should().Be(
            originalGeneration,
            "retry forwards the original generation id so the checkpoint resumes rather than restarting");
        retryGeneration.Should().NotBe(retryJobId, "the generation is intentionally decoupled from the new job id");
    }

    private static async Task<string?> ReadPersistedGenerationIdAsync(IDistributedCache cache, string jobId)
    {
        var json = await cache.GetStringAsync($"tile:request:{jobId}");
        json.Should().NotBeNullOrWhiteSpace();
        var persisted = JsonSerializer.Deserialize(
            json!,
            TileOperationsJsonContext.Default.PersistedTileOperationRequest);
        return persisted!.Request.GenerationId;
    }

    private static TileOperationJobService CreateSut(
        IUniversalProgressStore progressStore,
        IServiceScopeFactory serviceScopeFactory,
        IDistributedCache requestCache)
    {
        var cacheInvalidationService = new OutputCacheInvalidationService(
            cacheStore: null,
            responseCache: null,
            metadataCache: null,
            scopeFactory: serviceScopeFactory,
            refreshCoordinator: null,
            logger: NullLogger<OutputCacheInvalidationService>.Instance);

        return new TileOperationJobService(
            progressStore,
            requestCache,
            cacheInvalidationService,
            serviceScopeFactory,
            Options.Create(new TileOptions()),
            Options.Create(new LimitsOptions()),
            NullLogger<TileOperationJobService>.Instance);
    }

    private static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IMetadataV2GraphProvider>());
        services.AddSingleton(Substitute.For<ITileProvider>());
        return services.BuildServiceProvider();
    }

    private sealed class InMemoryUniversalProgressStore : IUniversalProgressStore
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IOperationProgress> _entries = new(StringComparer.Ordinal);

        public Task SetProgressAsync(string operationId, IOperationProgress progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _entries[operationId] = progress;
            return Task.CompletedTask;
        }

        public Task<ProgressCompareAndSetResult> TrySetProgressAsync(string operationId, IOperationProgress progress, OperationStatus expectedStatus, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
            => Task.FromResult(ProgressCompareAndSetResult.Updated);

        public Task<TProgress?> GetProgressAsync<TProgress>(string operationId, CancellationToken cancellationToken = default)
            where TProgress : class, IOperationProgress
            => Task.FromResult(_entries.TryGetValue(operationId, out var p) && p is TProgress typed ? typed : null);

        public Task<IOperationProgress?> GetProgressAsync(string operationId, CancellationToken cancellationToken = default)
        {
            _entries.TryGetValue(operationId, out var p);
            return Task.FromResult(p);
        }

        public Task DeleteProgressAsync(string operationId, CancellationToken cancellationToken = default)
        {
            _entries.TryRemove(operationId, out _);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetActiveOperationIdsAsync(OperationType? operationType = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([.. _entries.Keys]);

        public Task<IReadOnlyList<TProgress>> GetActiveOperationsAsync<TProgress>(OperationType operationType, CancellationToken cancellationToken = default)
            where TProgress : class, IOperationProgress
            => Task.FromResult<IReadOnlyList<TProgress>>([.. _entries.Values.OfType<TProgress>()]);
    }
}
