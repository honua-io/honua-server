// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
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
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Export;

[Protocol(TestProtocols.TestQuality)]
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
        recoveredProgress.CurrentPhase.Should().Be("Recovered for retry");
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ReadQueuedJobIdsAsync_WhenRecoveredRequestIsMissing_MarksProgressFailed()
    {
        var progressStore = new InMemoryUniversalProgressStore();
        var requestCache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var channel = Channel.CreateUnbounded<string>();
        channel.Writer.Complete();
        using var services = new ServiceCollection().BuildServiceProvider();
        var sut = new ExportJobService(
            progressStore,
            requestCache,
            channel,
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ExportJobService>.Instance);

        var job = CreateJob("missing-recovered-export");
        await progressStore.SetProgressAsync(
            job.JobId,
            ExportProgress.CreateInitial(job.JobId, job.Format, job.ServiceName, job.LayerId, job.TotalFeatures));

        await using var enumerator = sut.ReadQueuedJobIdsAsync().GetAsyncEnumerator();
        var hasRecoveredJob = await enumerator.MoveNextAsync();

        hasRecoveredJob.Should().BeFalse();

        var recoveredProgress = await progressStore.GetProgressAsync<ExportProgress>(job.JobId);
        recoveredProgress.Should().NotBeNull();
        recoveredProgress!.Status.Should().Be(OperationStatus.Failed);
        recoveredProgress.ErrorMessage.Should().Be("Export request metadata is no longer available.");
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ReadQueuedJobIdsAsync_PeriodicallyRecoversProcessingJobsWithoutWorkerRestart()
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
            NullLogger<ExportJobService>.Instance,
            recoveryPollInterval: TimeSpan.FromMilliseconds(25));

        var job = CreateJob("periodic-recover-export");
        await requestCache.SetStringAsync(
            "export:request:periodic-recover-export",
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
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        enumerator.Current.Should().Be(job.JobId);

        await progressStore.SetProgressAsync(
            job.JobId,
            ExportProgress.CreateInitial(job.JobId, job.Format, job.ServiceName, job.LayerId, job.TotalFeatures) with
            {
                Status = OperationStatus.Processing,
                CurrentPhase = "Exporting features"
            });

        (await enumerator.MoveNextAsync()).Should().BeTrue();
        enumerator.Current.Should().Be(job.JobId);

        var recoveredProgress = await progressStore.GetProgressAsync<ExportProgress>(job.JobId);
        recoveredProgress.Should().NotBeNull();
        recoveredProgress!.Status.Should().Be(OperationStatus.Queued);
        recoveredProgress.CurrentPhase.Should().Be("Recovered for retry");
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task RecoverQueuedJobIdsAsync_WithActiveRedisClaim_DoesNotRequeueProcessingJob()
    {
        var progressStore = new InMemoryUniversalProgressStore();
        var requestCache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var channel = Channel.CreateUnbounded<string>();
        using var services = new ServiceCollection().BuildServiceProvider();
        var database = Substitute.For<IDatabase>();
        database.StringGetAsync("export:job:claim:claimed-export", Arg.Any<CommandFlags>())
            .Returns(new RedisValue("owner-1"));
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        redis.GetDatabase().Returns(database);
        var sut = new ExportJobService(
            progressStore,
            requestCache,
            channel,
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ExportJobService>.Instance,
            redis);

        var job = CreateJob("claimed-export");
        await requestCache.SetStringAsync(
            "export:request:claimed-export",
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

        var recoverQueuedJobIds = typeof(ExportJobService)
            .GetMethod("RecoverQueuedJobIdsAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RecoverQueuedJobIdsAsync method was not found.");
        var recoveryTask = (Task<IReadOnlyList<string>>)recoverQueuedJobIds.Invoke(sut, [CancellationToken.None])!;
        var recoveredJobIds = await recoveryTask;

        recoveredJobIds.Should().BeEmpty();

        var progress = await progressStore.GetProgressAsync<ExportProgress>(job.JobId);
        progress.Should().NotBeNull();
        progress!.Status.Should().Be(OperationStatus.Processing);
        progress.CurrentPhase.Should().Be("Exporting features");
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

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task StartAsync_WhenProgressInitializationFails_RollsBackPersistedRequest()
    {
        var progressStore = new ThrowOnFirstSetProgressStore();
        var requestCache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var channel = Channel.CreateUnbounded<string>();
        using var services = new ServiceCollection().BuildServiceProvider();
        var sut = new ExportJobService(
            progressStore,
            requestCache,
            channel,
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ExportJobService>.Instance);

        var job = CreateJob("failed-start-export");

        await FluentActions.Invoking(() => sut.StartAsync(job))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("progress initialization failed");

        (await requestCache.GetStringAsync("export:request:failed-start-export")).Should().BeNull();
        (await progressStore.GetProgressAsync<ExportProgress>(job.JobId)).Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ProcessQueuedJobAsync_WhenPersistedRequestIsMissing_MarksProgressFailed()
    {
        var progressStore = new InMemoryUniversalProgressStore();
        var requestCache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var channel = Channel.CreateUnbounded<string>();
        using var services = new ServiceCollection().BuildServiceProvider();

        var startService = new ExportJobService(
            progressStore,
            requestCache,
            channel,
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ExportJobService>.Instance);

        var job = CreateJob("missing-process-export");
        await startService.StartAsync(job);
        await requestCache.RemoveAsync("export:request:missing-process-export");

        var processingService = new ExportJobService(
            progressStore,
            requestCache,
            channel,
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ExportJobService>.Instance);

        await processingService.ProcessQueuedJobAsync(job.JobId);

        var progress = await progressStore.GetProgressAsync<ExportProgress>(job.JobId);
        progress.Should().NotBeNull();
        progress!.Status.Should().Be(OperationStatus.Failed);
        progress.ErrorMessage.Should().Be("Export request metadata is no longer available.");
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ProcessQueuedJobAsync_WhenRequestLookupThrows_ReleasesRedisLease()
    {
        var progressStore = new InMemoryUniversalProgressStore();
        var requestCache = new ThrowOnGetDistributedCache();
        var channel = Channel.CreateUnbounded<string>();
        using var services = new ServiceCollection().BuildServiceProvider();
        var database = Substitute.For<IDatabase>();
        database.LockTakeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(true));
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        var startService = new ExportJobService(
            progressStore,
            requestCache,
            channel,
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ExportJobService>.Instance,
            redis);

        var job = CreateJob("lease-release-export");
        await startService.StartAsync(job);

        requestCache.ThrowOnGet = true;
        var processingService = new ExportJobService(
            progressStore,
            requestCache,
            channel,
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ExportJobService>.Instance,
            redis);

        await FluentActions.Invoking(() => processingService.ProcessQueuedJobAsync(job.JobId))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("request cache read failed");

        database.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IDatabase.LockReleaseAsync))
            .Should().Be(1);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ProcessQueuedJobAsync_WhenLeaseIsLost_RequeuesJobAndPreservesRequest()
    {
        var progressStore = new InMemoryUniversalProgressStore();
        var requestCache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var channel = Channel.CreateUnbounded<string>();

        var streamingStore = Substitute.For<IStreamingFeatureStore>();
        streamingStore
            .StreamFeaturesAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => CreateBlockingFeatures(callInfo.ArgAt<CancellationToken>(2)));

        var cloudStorage = Substitute.For<ICloudFileStorage>();
        var crsRegistry = new NullCrsRegistry();

        using var services = new ServiceCollection()
            .AddSingleton<IStreamingFeatureStore>(streamingStore)
            .AddSingleton<ICrsRegistry>(crsRegistry)
            .AddSingleton<ICloudFileStorage>(cloudStorage)
            .BuildServiceProvider();

        var database = Substitute.For<IDatabase>();
        database.LockTakeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(true), Task.FromResult(false));
        database.LockExtendAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(false));
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        var sut = new ExportJobService(
            progressStore,
            requestCache,
            channel,
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ExportJobService>.Instance,
            redis);

        var job = CreateJob("lease-lost-export");
        await sut.StartAsync(job);
        (await channel.Reader.ReadAsync()).Should().Be(job.JobId);

        await sut.ProcessQueuedJobAsync(job.JobId);

        var progress = await progressStore.GetProgressAsync<ExportProgress>(job.JobId);
        progress.Should().NotBeNull();
        progress!.Status.Should().Be(OperationStatus.Queued);
        progress.CurrentPhase.Should().Be("Lease lost; awaiting retry");

        var persistedRequest = await requestCache.GetStringAsync("export:request:lease-lost-export");
        persistedRequest.Should().NotBeNullOrWhiteSpace();

        (await channel.Reader.ReadAsync()).Should().Be(job.JobId);
        cloudStorage.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(ICloudFileStorage.UploadAsync))
            .Should().Be(0);
    }

    private static async IAsyncEnumerable<Feature> CreateFeatures()
    {
        yield return Feature.Create(
            1,
            geometry: null,
            ImmutableDictionary<string, object?>.Empty.Add("name", "Test feature"));
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<Feature> CreateBlockingFeatures(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
        yield break;
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
            ExportGeometryType.None);

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

    private sealed class ThrowOnFirstSetProgressStore : IUniversalProgressStore
    {
        private bool _hasThrown;

        public Task SetProgressAsync(
            string operationId,
            IOperationProgress progress,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
        {
            if (!_hasThrown)
            {
                _hasThrown = true;
                throw new InvalidOperationException("progress initialization failed");
            }

            return Task.CompletedTask;
        }

        public Task<TProgress?> GetProgressAsync<TProgress>(string operationId, CancellationToken cancellationToken = default)
            where TProgress : class, IOperationProgress
            => Task.FromResult<TProgress?>(null);

        public Task<IOperationProgress?> GetProgressAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult<IOperationProgress?>(null);

        public Task DeleteProgressAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<string>> GetActiveOperationIdsAsync(OperationType? operationType = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<IReadOnlyList<TProgress>> GetActiveOperationsAsync<TProgress>(OperationType operationType, CancellationToken cancellationToken = default)
            where TProgress : class, IOperationProgress
            => Task.FromResult<IReadOnlyList<TProgress>>(Array.Empty<TProgress>());
    }

    private sealed class ThrowOnGetDistributedCache : IDistributedCache
    {
        private readonly MemoryDistributedCache _inner = new(Options.Create(new MemoryDistributedCacheOptions()));

        public bool ThrowOnGet { get; set; }

        public byte[]? Get(string key)
        {
            ThrowIfConfigured(key);
            return _inner.Get(key);
        }

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            ThrowIfConfigured(key);
            return _inner.GetAsync(key, token);
        }

        public void Refresh(string key) => _inner.Refresh(key);

        public Task RefreshAsync(string key, CancellationToken token = default)
            => _inner.RefreshAsync(key, token);

        public void Remove(string key) => _inner.Remove(key);

        public Task RemoveAsync(string key, CancellationToken token = default)
            => _inner.RemoveAsync(key, token);

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
            => _inner.Set(key, value, options);

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
            => _inner.SetAsync(key, value, options, token);

        private void ThrowIfConfigured(string key)
        {
            if (ThrowOnGet && string.Equals(key, "export:request:lease-release-export", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("request cache read failed");
            }
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
