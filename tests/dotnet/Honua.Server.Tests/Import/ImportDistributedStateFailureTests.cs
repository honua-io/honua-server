// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Server.Features.Import;
using Honua.Server.Features.Infrastructure.Progress;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;

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
