// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Import;
using Honua.Migration;
using Honua.Import.FileImport;
using Honua.Import.RasterImport;
using Honua.Infrastructure.Progress;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Import;

[Collection("Unit")]
public sealed class ImportDistributedStateFailureTests
{
    [UnitTest]
    public async Task UniversalProgressStore_WithConfiguredDistributedCache_WhenCacheWriteFails_ThrowsInsteadOfUsingNodeLocalState()
    {
        var cache = new ThrowingDistributedCache();
        var store = new UniversalProgressStore(cache, NullLogger<UniversalProgressStore>.Instance);
        var progress = ExportProgress.CreateInitial("export-1", "csv", "svc", 1, 10);

        await FluentActions
            .Invoking(() => store.SetProgressAsync("export-1", progress, TimeSpan.FromMinutes(5)))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*Distributed import progress state is unavailable*");

        await FluentActions
            .Invoking(() => store.GetProgressAsync("export-1"))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*Distributed import progress state is unavailable*");
    }

    [UnitTest]
    public async Task UniversalProgressStore_WithMemoryDistributedCache_TracksActiveOperationIdsWithoutRedisBackplane()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var store = new UniversalProgressStore(cache, NullLogger<UniversalProgressStore>.Instance);
        var progress = ExportProgress.CreateInitial("export-1", "csv", "svc", 1, 10);

        await store.SetProgressAsync("export-1", progress, TimeSpan.FromMinutes(5));

        var activeIds = await store.GetActiveOperationIdsAsync(OperationType.Export);
        activeIds.Should().Contain("export-1");

        var loaded = await store.GetProgressAsync<ExportProgress>("export-1");
        loaded.Should().NotBeNull();

        await store.DeleteProgressAsync("export-1");
        (await store.GetActiveOperationIdsAsync(OperationType.Export)).Should().NotContain("export-1");
    }

    [UnitTest]
    public async Task RedisProgressStore_WithConfiguredDistributedCache_WhenCacheWriteFails_ThrowsInsteadOfUsingNodeLocalState()
    {
        var cache = new ThrowingDistributedCache();
        var store = new RedisProgressStore<GeoservicesImportRequest>(
            cache,
            NullLogger.Instance,
            "test:request:",
            GeoservicesImportJsonContext.Default.GeoservicesImportRequest);

        var request = new GeoservicesImportRequest
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Test/FeatureServer",
            LayerId = 0,
            TableName = "geoservices_fail_closed_test",
            AutoPublish = false
        };

        await FluentActions
            .Invoking(() => store.SetProgressAsync("job-1", request, TimeSpan.FromMinutes(5)))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*Distributed import state is unavailable*");

        await FluentActions
            .Invoking(() => store.GetProgressAsync("job-1"))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*Distributed import state is unavailable*");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Tier", "Fast")]
    public async Task RecoveryProbe_IsBoundedThrottledAndSkippedWhenHealthy(bool universal)
    {
        var cache = Substitute.For<IDistributedCache>();
        cache.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<byte[]?>(new InvalidOperationException("Cache unavailable")));
        IProgressStoreRecovery recovery;
        object backplane;
        Func<Task> read;
        Func<bool> isUnavailable;
        string healthKey;
        if (universal)
        {
            var store = new UniversalProgressStore(cache, NullLogger<UniversalProgressStore>.Instance);
            recovery = new DistributedProgressStoreAdapter<ExportProgress>(store);
            backplane = store;
            read = () => store.GetProgressAsync("job");
            isUnavailable = () => store.IsUsingFallback;
            healthKey = "universal:progress:health";
        }
        else
        {
            var store = new RedisProgressStore<GeoservicesImportRequest>(
                cache, NullLogger.Instance, "test:request:",
                GeoservicesImportJsonContext.Default.GeoservicesImportRequest);
            recovery = store;
            backplane = store;
            read = () => store.GetProgressAsync("job");
            isUnavailable = () => store.IsUsingFallback;
            healthKey = "test:request:__health_check__";
        }

        await recovery.ProbeRecoveryAsync();
        cache.ReceivedCalls().Should().BeEmpty("healthy components need no recovery I/O");
        await FluentActions.Invoking(read).Should().ThrowAsync<InvalidOperationException>();
        isUnavailable().Should().BeTrue();
        cache.ClearReceivedCalls();

        await recovery.ProbeRecoveryAsync();
        cache.ReceivedCalls().Should().BeEmpty("the existing retry interval must be respected");

        // Advance the existing wall-clock retry deadline without sleeping for 30 seconds.
        var failureTime = backplane.GetType().GetField("_lastRedisFailure", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing recovery retry timestamp.");
        failureTime.SetValue(backplane, DateTime.MinValue);
        await recovery.ProbeRecoveryAsync();
        await recovery.ProbeRecoveryAsync();
        isUnavailable().Should().BeTrue();
        await cache.Received(1).GetAsync(healthKey, Arg.Any<CancellationToken>());
        cache.ReceivedCalls().Should().ContainSingle("a failed probe must throttle later attempts");

        cache.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<byte[]?>(null));
        cache.ClearReceivedCalls();
        failureTime.SetValue(backplane, DateTime.MinValue);
        await recovery.ProbeRecoveryAsync();
        await recovery.ProbeRecoveryAsync();
        isUnavailable().Should().BeFalse();
        await cache.Received(1).GetAsync(healthKey, Arg.Any<CancellationToken>());
        cache.ReceivedCalls().Should().ContainSingle("recovery reads only a health key, never active jobs");
    }

    private sealed class ThrowingDistributedCache : IDistributedCache
    {
        public byte[]? Get(string key) => throw CreateException();

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
            => Task.FromException<byte[]?>(CreateException());

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
            => throw CreateException();

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
            => Task.FromException(CreateException());

        public void Refresh(string key)
            => throw CreateException();

        public Task RefreshAsync(string key, CancellationToken token = default)
            => Task.FromException(CreateException());

        public void Remove(string key)
            => throw CreateException();

        public Task RemoveAsync(string key, CancellationToken token = default)
            => Task.FromException(CreateException());

        private static InvalidOperationException CreateException()
            => new("Simulated distributed cache failure.");
    }
}
