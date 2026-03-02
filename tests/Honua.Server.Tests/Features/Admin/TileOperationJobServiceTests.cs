// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Reflection;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Tiles;
using Honua.Server.Features.Admin.TileOperations;
using Honua.Server.Features.Infrastructure.Caching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

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

    private static TileOperationJobService CreateSut(
        IUniversalProgressStore progressStore,
        IServiceScopeFactory serviceScopeFactory)
    {
        var cacheInvalidationService = new OutputCacheInvalidationService(
            cacheStore: null,
            responseCache: null,
            metadataCache: null,
            NullLogger<OutputCacheInvalidationService>.Instance);

        return new TileOperationJobService(
            progressStore,
            cacheInvalidationService,
            serviceScopeFactory,
            Options.Create(new TileOptions()),
            Options.Create(new LimitsOptions()),
            NullLogger<TileOperationJobService>.Instance);
    }

    private static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<ILayerCatalog>());
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
