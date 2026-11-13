// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Caching;

public sealed partial class InMemoryCacheServiceTests
{
    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetWithMetadataAsync_WhenKeyExists_ReturnsValueWithPositiveRemainingTtl()
    {
        // Arrange
        var item = new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false };
        var ttl = TimeSpan.FromSeconds(60);
        await _cacheService.SetAsync("meta:1", item, ttl);

        // Act
        var result = await _cacheService.GetWithMetadataAsync<MetadataV2Field>("meta:1");

        // Assert
        result.HasValue.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Name.Should().Be("objectid");
        result.RemainingTtl.Should().BeGreaterThan(TimeSpan.Zero);
        result.RemainingTtl.Should().BeLessOrEqualTo(ttl);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetWithMetadataAsync_WhenKeyNotFound_ReturnsMiss()
    {
        // Act
        var result = await _cacheService.GetWithMetadataAsync<MetadataV2Field>("meta:nonexistent");

        // Assert
        result.HasValue.Should().BeFalse();
        result.Value.Should().BeNull();
        result.RemainingTtl.Should().Be(TimeSpan.Zero);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetWithMetadataAsync_WhenExpired_ReturnsMiss()
    {
        // Arrange
        var item = new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false };
        await _cacheService.SetAsync("meta:short", item, TimeSpan.FromMilliseconds(50));

        // Wait for expiration
        await Task.Delay(100);

        // Act
        var result = await _cacheService.GetWithMetadataAsync<MetadataV2Field>("meta:short");

        // Assert
        result.HasValue.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetWithMetadataAsync_CacheDisabled_ReturnsMiss()
    {
        // Arrange
        var options = new CacheOptions { Enabled = false };
        using var disabledCache = new InMemoryCacheService(
            Options.Create(options),
            NullLogger<InMemoryCacheService>.Instance,
            _performanceMonitor);

        // Act
        var result = await disabledCache.GetWithMetadataAsync<MetadataV2Field>("meta:1");

        // Assert
        result.HasValue.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetWithMetadataAsync_AfterRemove_ReturnsMiss()
    {
        // Arrange: set and then remove to verify write metadata is cleaned up
        var item = new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false };
        await _cacheService.SetAsync("meta:removed", item, TimeSpan.FromSeconds(60));
        await _cacheService.RemoveAsync("meta:removed");

        // Act
        var result = await _cacheService.GetWithMetadataAsync<MetadataV2Field>("meta:removed");

        // Assert
        result.HasValue.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetWithMetadataAsync_RemainingTtl_FollowsTheClock()
    {
        var clock = new ManualClock();
        using var cache = new InMemoryCacheService(
            Options.Create(_options),
            NullLogger<InMemoryCacheService>.Instance,
            _performanceMonitor,
            clock);
        await cache.SetAsync("meta:clock", new MetadataV2Field { Name = "clocked", Type = MetadataV2FieldType.String, Length = 10 }, TimeSpan.FromSeconds(60));

        clock.Advance(TimeSpan.FromSeconds(45));
        var result = await cache.GetWithMetadataAsync<MetadataV2Field>("meta:clock");

        result.HasValue.Should().BeTrue();
        result.RemainingTtl.Should().Be(TimeSpan.FromSeconds(15));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task CleanupTimer_RemovesExpiredEntriesAndKeepsLiveOnes()
    {
        var clock = new ManualClock();
        using var cache = new InMemoryCacheService(
            Options.Create(_options),
            NullLogger<InMemoryCacheService>.Instance,
            _performanceMonitor,
            clock);
        await cache.SetAsync("item:short", new MetadataV2Field { Name = "short", Type = MetadataV2FieldType.String, Length = 10 }, TimeSpan.FromSeconds(30));
        await cache.SetAsync("item:long", new MetadataV2Field { Name = "long", Type = MetadataV2FieldType.String, Length = 10 }, TimeSpan.FromMinutes(5));

        clock.Advance(TimeSpan.FromMinutes(1));
        clock.FireTimers();

        GetEntryKeys(cache).Should().ContainSingle().Which.Should().EndWith("item:long");
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly List<(TimerCallback Callback, object? State)> _timers = [];
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;

        public void FireTimers()
        {
            foreach (var (callback, state) in _timers)
            {
                callback(state);
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _timers.Add((callback, state));
            return Substitute.For<ITimer>();
        }
    }
}
