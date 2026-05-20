// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.TestKit.Infrastructure;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Tiles;
using Honua.Server.Features.Admin.TileOperations;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Progress;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Admin;

public sealed class TileOperationJobServiceTests
{
    [Fact]
    public async Task ProcessQueuedJobAsync_WhenJobCompletes_RemovesCachedStartRequest()
    {
        using var serviceProvider = CreateServiceProvider();
        var progressStore = new InMemoryUniversalProgressStore();
        var sut = CreateSut(progressStore, serviceProvider.GetRequiredService<IServiceScopeFactory>());

        var jobId = await sut.StartAsync(new TileOperationStartRequest
        {
            Operation = "invalidate",
            LayerId = 1
        });

        ContainsCachedRequest(sut, jobId).Should().BeTrue();

        await sut.ProcessQueuedJobAsync(jobId);

        ContainsCachedRequest(sut, jobId).Should().BeFalse();
        (await sut.GetAsync(jobId))!.Status.Should().Be(OperationStatus.Completed);
    }

    [Fact]
    public async Task RetryAsync_WhenJobFailed_UsesCachedRequestAndReturnsNewJobId()
    {
        using var serviceProvider = CreateServiceProvider();
        var progressStore = new InMemoryUniversalProgressStore();
        var sut = CreateSut(progressStore, serviceProvider.GetRequiredService<IServiceScopeFactory>());

        var failedJobId = await sut.StartAsync(new TileOperationStartRequest
        {
            Operation = "seed",
            LayerId = 1,
            TileMatrixSetId = "UnsupportedSet"
        });

        await sut.ProcessQueuedJobAsync(failedJobId);
        (await sut.GetAsync(failedJobId))!.Status.Should().Be(OperationStatus.Failed);
        ContainsCachedRequest(sut, failedJobId).Should().BeTrue();

        var retryJobId = await sut.RetryAsync(failedJobId);

        retryJobId.Should().NotBeNullOrWhiteSpace();
        retryJobId.Should().NotBe(failedJobId);
    }

    [Fact]
    public async Task RetryAsync_WithLegacyCachedRequestPayload_ReturnsNewJobId()
    {
        using var serviceProvider = CreateServiceProvider();
        var progressStore = new InMemoryUniversalProgressStore();
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var sut = CreateSut(progressStore, serviceProvider.GetRequiredService<IServiceScopeFactory>(), cache);
        const string jobId = "legacy-failed-job";

        await cache.SetStringAsync(
            $"tile:request:{jobId}",
            JsonSerializer.Serialize(new TileOperationStartRequest
            {
                Operation = "seed",
                LayerId = 1,
                TileMatrixSetId = "UnsupportedSet"
            }));

        await progressStore.SetProgressAsync(
            jobId,
            TileOperationProgress.CreateInitial(jobId, "seed", null, 1, "UnsupportedSet") with
            {
                Status = OperationStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = "Simulated failure"
            });

        var retryJobId = await sut.RetryAsync(jobId);

        retryJobId.Should().NotBeNullOrWhiteSpace();
        retryJobId.Should().NotBe(jobId);
    }

    [Fact]
    public async Task ReadQueuedJobIdsAsync_WithLegacyCachedRequestPayload_RecoversQueuedJob()
    {
        using var serviceProvider = CreateServiceProvider();
        var progressStore = new InMemoryUniversalProgressStore();
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var sut = CreateSut(progressStore, serviceProvider.GetRequiredService<IServiceScopeFactory>(), cache);
        const string jobId = "legacy-queued-job";

        await cache.SetStringAsync(
            $"tile:request:{jobId}",
            JsonSerializer.Serialize(new TileOperationStartRequest
            {
                Operation = "archive",
                LayerId = 1,
                TileMatrixSetId = "WebMercatorQuad"
            }));

        await progressStore.SetProgressAsync(
            jobId,
            TileOperationProgress.CreateInitial(jobId, "archive", null, 1, "WebMercatorQuad"));

        await using var enumerator = sut.ReadQueuedJobIdsAsync().GetAsyncEnumerator();
        var hasRecoveredJob = await enumerator.MoveNextAsync();

        hasRecoveredJob.Should().BeTrue();
        enumerator.Current.Should().Be(jobId);
    }

    [Fact]
    public async Task ReadQueuedJobIdsAsync_WithQueuedPublishJob_RecoversAcrossRestart()
    {
        using var serviceProvider = CreateServiceProvider();
        var progressStore = new InMemoryUniversalProgressStore();
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var sut = CreateSut(progressStore, serviceProvider.GetRequiredService<IServiceScopeFactory>(), cache);
        const string jobId = "publish-queued-job";

        await cache.SetStringAsync(
            $"tile:request:{jobId}",
            JsonSerializer.Serialize(new PersistedTileOperationRequest
            {
                Request = new TileOperationStartRequest
                {
                    Operation = "publish",
                    LayerId = 99,
                    TileMatrixSetId = "WebMercatorQuad"
                }
            }));

        await progressStore.SetProgressAsync(
            jobId,
            TileOperationProgress.CreateInitial(jobId, "publish", null, 99, "WebMercatorQuad"));

        await using var enumerator = sut.ReadQueuedJobIdsAsync().GetAsyncEnumerator();
        var hasRecoveredJob = await enumerator.MoveNextAsync();

        hasRecoveredJob.Should().BeTrue();
        enumerator.Current.Should().Be(jobId);
    }

    [Fact]
    public async Task ProcessQueuedJobAsync_WhenRequestMetadataIsMissing_FailsJobAndCleansUpRequest()
    {
        using var serviceProvider = CreateServiceProvider();
        var progressStore = new InMemoryUniversalProgressStore();
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var sut = CreateSut(progressStore, serviceProvider.GetRequiredService<IServiceScopeFactory>(), cache);

        var jobId = await sut.StartAsync(new TileOperationStartRequest
        {
            Operation = "seed",
            LayerId = 1,
            TileMatrixSetId = "WebMercatorQuad"
        });

        RemoveCachedRequest(sut, jobId);
        await cache.RemoveAsync($"tile:request:{jobId}");

        await sut.ProcessQueuedJobAsync(jobId);

        var progress = await sut.GetAsync(jobId);
        progress.Should().NotBeNull();
        progress!.Status.Should().Be(OperationStatus.Failed);
        progress.ErrorMessage.Should().Be("Tile operation request metadata is no longer available.");
        ContainsCachedRequest(sut, jobId).Should().BeFalse();
    }

    [Fact]
    public async Task ReadQueuedJobIdsAsync_WhenRecoveredJobRequestMetadataIsMissing_FailsJob()
    {
        using var serviceProvider = CreateServiceProvider();
        var progressStore = new InMemoryUniversalProgressStore();
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var sut = CreateSut(progressStore, serviceProvider.GetRequiredService<IServiceScopeFactory>(), cache);
        const string jobId = "missing-request-job";

        await progressStore.SetProgressAsync(
            jobId,
            TileOperationProgress.CreateInitial(jobId, "seed", null, 1, "WebMercatorQuad"));

        var recoverQueuedJobIds = typeof(TileOperationJobService)
            .GetMethod("RecoverQueuedJobIdsAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RecoverQueuedJobIdsAsync method was not found.");
        var recoveryTask = (Task<IReadOnlyList<string>>)recoverQueuedJobIds.Invoke(sut, [CancellationToken.None])!;
        var recoveredJobIds = await recoveryTask;

        recoveredJobIds.Should().BeEmpty();

        var progress = await sut.GetAsync(jobId);
        progress.Should().NotBeNull();
        progress!.Status.Should().Be(OperationStatus.Failed);
        progress.ErrorMessage.Should().Be("Tile operation request metadata is no longer available.");
    }

    [Fact]
    public async Task ProcessQueuedJobAsync_WithMissingRequest_ReleasesRedisLease()
    {
        using var serviceProvider = CreateServiceProvider();
        var progressStore = new InMemoryUniversalProgressStore();
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var database = Substitute.For<IDatabase>();
        database.LockTakeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(true);
        database.LockReleaseAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(true);
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        redis.GetDatabase().Returns(database);
        var sut = CreateSut(progressStore, serviceProvider.GetRequiredService<IServiceScopeFactory>(), cache, redis);
        const string jobId = "missing-request-with-lease";

        // No cached request and no progress entry — simulates the post-restart
        // scenario where the request blob expired but the queue still references
        // the job.
        await sut.ProcessQueuedJobAsync(jobId);

        await database.Received().LockReleaseAsync(
            Arg.Is<RedisKey>(k => ((string?)k!)!.EndsWith(jobId, StringComparison.Ordinal)),
            Arg.Any<RedisValue>(),
            Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task ProcessQueuedJobAsync_WithCancelledStatus_ReleasesRedisLease()
    {
        using var serviceProvider = CreateServiceProvider();
        var progressStore = new InMemoryUniversalProgressStore();
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var database = Substitute.For<IDatabase>();
        database.LockTakeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(true);
        database.LockReleaseAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(true);
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        redis.GetDatabase().Returns(database);
        var sut = CreateSut(progressStore, serviceProvider.GetRequiredService<IServiceScopeFactory>(), cache, redis);

        var jobId = await sut.StartAsync(new TileOperationStartRequest
        {
            Operation = "invalidate",
            LayerId = 7
        });

        // Pre-cancel before the worker dispatches; ProcessQueuedJobAsync should
        // still release the Redis claim instead of leaving it held until TTL.
        await progressStore.SetProgressAsync(
            jobId,
            TileOperationProgress.CreateInitial(jobId, "invalidate", null, 7, "WebMercatorQuad") with
            {
                Status = OperationStatus.Cancelled,
                CompletedAt = DateTimeOffset.UtcNow
            });

        await sut.ProcessQueuedJobAsync(jobId);

        await database.Received().LockReleaseAsync(
            Arg.Is<RedisKey>(k => ((string?)k!)!.EndsWith(jobId, StringComparison.Ordinal)),
            Arg.Any<RedisValue>(),
            Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task RecoverQueuedJobIdsAsync_WithActiveRedisClaim_DoesNotRequeueProcessingJob()
    {
        using var serviceProvider = CreateServiceProvider();
        var progressStore = new InMemoryUniversalProgressStore();
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var database = Substitute.For<IDatabase>();
        database.StringGetAsync("tile:job:claim:claimed-tile-job", Arg.Any<CommandFlags>())
            .Returns(new RedisValue("owner-1"));
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        redis.GetDatabase().Returns(database);
        var sut = CreateSut(progressStore, serviceProvider.GetRequiredService<IServiceScopeFactory>(), cache, redis);
        const string jobId = "claimed-tile-job";

        await cache.SetStringAsync(
            $"tile:request:{jobId}",
            JsonSerializer.Serialize(new PersistedTileOperationRequest
            {
                Request = new TileOperationStartRequest
                {
                    Operation = "seed",
                    LayerId = 1,
                    TileMatrixSetId = "WebMercatorQuad"
                }
            }));
        await progressStore.SetProgressAsync(
            jobId,
            TileOperationProgress.CreateInitial(jobId, "seed", null, 1, "WebMercatorQuad") with
            {
                Status = OperationStatus.Processing,
                CurrentPhase = "Processing tiles"
            });

        var recoverQueuedJobIds = typeof(TileOperationJobService)
            .GetMethod("RecoverQueuedJobIdsAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RecoverQueuedJobIdsAsync method was not found.");
        var recoveryTask = (Task<IReadOnlyList<string>>)recoverQueuedJobIds.Invoke(sut, [CancellationToken.None])!;
        var recoveredJobIds = await recoveryTask;

        recoveredJobIds.Should().BeEmpty();

        var progress = await sut.GetAsync(jobId);
        progress.Should().NotBeNull();
        progress!.Status.Should().Be(OperationStatus.Processing);
        progress.CurrentPhase.Should().Be("Processing tiles");
    }

    private static TileOperationJobService CreateSut(
        IUniversalProgressStore progressStore,
        IServiceScopeFactory serviceScopeFactory,
        IDistributedCache? requestCache = null,
        IConnectionMultiplexer? redis = null)
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
            requestCache ?? new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            cacheInvalidationService,
            serviceScopeFactory,
            Options.Create(new TileOptions()),
            Options.Create(new LimitsOptions()),
            NullLogger<TileOperationJobService>.Instance,
            redis);
    }

    private static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMetadataV2GraphProvider>(new TestMetadataV2GraphBuilder().Build() is var graph
            ? new TestMetadataV2GraphProvider(graph)
            : throw new InvalidOperationException());
        services.AddSingleton(Substitute.For<ITileProvider>());
        return services.BuildServiceProvider();
    }

    private static bool ContainsCachedRequest(TileOperationJobService sut, string jobId)
    {
        var field = typeof(TileOperationJobService).GetField("_jobRequests", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Unable to access _jobRequests field.");
        var dictionary = field.GetValue(sut) ?? throw new InvalidOperationException("_jobRequests was null.");
        var containsKey = dictionary.GetType().GetMethod("ContainsKey", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException("ContainsKey method was not found.");

        return (bool)containsKey.Invoke(dictionary, new object?[] { jobId })!;
    }

    private static void RemoveCachedRequest(TileOperationJobService sut, string jobId)
    {
        var field = typeof(TileOperationJobService).GetField("_jobRequests", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Unable to access _jobRequests field.");
        var dictionary = field.GetValue(sut) ?? throw new InvalidOperationException("_jobRequests was null.");
        var remove = dictionary.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(method =>
            {
                if (!string.Equals(method.Name, "TryRemove", StringComparison.Ordinal))
                {
                    return false;
                }

                var parameters = method.GetParameters();
                return parameters.Length == 2
                    && parameters[0].ParameterType == typeof(string)
                    && parameters[1].IsOut;
            })
            ?? throw new InvalidOperationException("TryRemove method was not found.");
        var args = new object?[] { jobId, null };
        _ = remove.Invoke(dictionary, args);
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

        public Task<TProgress?> GetProgressAsync<TProgress>(
            string operationId,
            CancellationToken cancellationToken = default)
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

        public Task<IReadOnlyList<string>> GetActiveOperationIdsAsync(
            OperationType? operationType = null,
            CancellationToken cancellationToken = default)
        {
            var ids = _entries
                .Where(kvp => operationType == null || kvp.Value.Type == operationType.Value)
                .Select(static kvp => kvp.Key)
                .ToArray();
            return Task.FromResult<IReadOnlyList<string>>(ids);
        }

        public Task<IReadOnlyList<TProgress>> GetActiveOperationsAsync<TProgress>(
            OperationType operationType,
            CancellationToken cancellationToken = default)
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
