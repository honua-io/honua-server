// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Honua.Server.Tests.Features.Caching;

[Protocol(ProtocolNames.TestQuality)]
public sealed class DistributedCacheKeyIndexTests
{
    private const string Prefix = "generic-index-test:";
    private const string IndexKey = Prefix + "scope:default:__cache_key_index__";

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task SetAsync_ExpiredGenericEntries_AreNotCarriedIntoSubsequentWrites()
    {
        var backend = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        await using var service = Create(backend);
        for (var index = 0; index < 100; index++)
        {
            await service.SetAsync("expired:" + index, "value", TimeSpan.FromMilliseconds(50));
        }
        await Task.Delay(200);
        await service.SetAsync("live", "value", TimeSpan.FromMinutes(1));

        using var document = JsonDocument.Parse((await backend.GetAsync(IndexKey))!);
        var keys = document.RootElement.GetProperty("keys").EnumerateArray()
            .Select(value => value.GetString()!).ToArray();
        Assert.Equal([Prefix + "scope:default:live"], keys);
        Assert.False(service.IsUsingFallback);
        Assert.Equal("value", await service.GetAsync<string>("live"));
        await service.RemoveByPatternAsync("live*");
        Assert.Null(await service.GetAsync<string>("live"));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task SetAsync_LongLivedMember_DoesNotKeepExpiredNeighborsIndexed()
    {
        var backend = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        await using var service = Create(backend);
        await service.SetAsync("anchor", "value", TimeSpan.FromMinutes(1));
        await service.SetAsync("expired", "value", TimeSpan.FromMilliseconds(50));
        await Task.Delay(200);
        await service.SetAsync("live", "value", TimeSpan.FromMinutes(1));

        using var document = JsonDocument.Parse((await backend.GetAsync(IndexKey))!);
        var keys = document.RootElement.GetProperty("keys").EnumerateArray()
            .Select(value => value.GetString()!).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { Prefix + "scope:default:anchor", Prefix + "scope:default:live" }, keys);
        Assert.Equal("value", await service.GetAsync<string>("anchor"));
        Assert.False(service.IsUsingFallback);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task SetAsync_AllGenericPayloadsExpired_IndexDoesNotHaveIndependentThirtyDayLifetime()
    {
        var backend = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        await using var service = Create(backend);
        await service.SetAsync("short", "value", TimeSpan.FromMilliseconds(50));
        await Task.Delay(200);

        Assert.Null(await service.GetAsync<string>("short"));
        Assert.Null(await backend.GetAsync(IndexKey));
        Assert.False(service.IsUsingFallback);
    }

    private static RedisCacheService Create(IDistributedCache backend) => new(
        backend,
        Options.Create(new CacheOptions { Enabled = true, EnableFallback = false, KeyPrefix = Prefix }),
        NullLogger<RedisCacheService>.Instance,
        Substitute.For<IPerformanceMonitor>());
}
