// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Honua.Server.Tests.Features.Caching;

[Protocol(ProtocolNames.TestQuality)]
public sealed class DistributedCacheKeyIndexTests
{
    private readonly TransitionClock _clock = new();
    private const string Prefix = "generic-index-test:";
    private const string IndexKey = Prefix + "scope:default:__cache_key_index__";

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task SetAsync_ExpiredGenericEntries_AreNotCarriedIntoSubsequentWrites()
    {
        var backend = CreateBackend();
        await using var service = Create(backend);
        for (var index = 0; index < 100; index++)
        {
            await service.SetAsync("expired:" + index, "value", TimeSpan.FromMilliseconds(50));
        }
        _clock.Advance(TimeSpan.FromMilliseconds(200));
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
        var backend = CreateBackend();
        await using var service = Create(backend);
        await service.SetAsync("anchor", "value", TimeSpan.FromMinutes(1));
        await service.SetAsync("expired", "value", TimeSpan.FromMilliseconds(50));
        _clock.Advance(TimeSpan.FromMilliseconds(200));
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
        var backend = CreateBackend();
        await using var service = Create(backend);
        var lifetime = TimeSpan.FromMilliseconds(50);
        await service.SetAsync("short", "value", lifetime);
        _clock.Advance(lifetime - TimeSpan.FromTicks(1));
        Assert.Equal("value", await service.GetAsync<string>("short"));
        Assert.NotNull(await backend.GetAsync(IndexKey));
        _clock.Advance(TimeSpan.FromTicks(1));

        Assert.Null(await service.GetAsync<string>("short"));
        Assert.Null(await backend.GetAsync(IndexKey));
        Assert.False(service.IsUsingFallback);
    }

    private MemoryDistributedCache CreateBackend()
    {
        var backendClock = Substitute.For<ISystemClock>();
        backendClock.UtcNow.Returns(_ => _clock.GetUtcNow());
        return new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions { Clock = backendClock }));
    }

    private RedisCacheService Create(IDistributedCache backend) => new(
        backend,
        Options.Create(new CacheOptions { Enabled = true, EnableFallback = false, KeyPrefix = Prefix }),
        NullLogger<RedisCacheService>.Instance,
        Substitute.For<IPerformanceMonitor>(),
        timeProvider: _clock);
}
