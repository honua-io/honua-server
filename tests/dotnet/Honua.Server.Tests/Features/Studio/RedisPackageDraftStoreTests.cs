// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Studio.Drafts;
using Honua.Server.Features.Studio;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Studio;

[Collection("Redis")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class RedisPackageDraftStoreTests(RedisFixture redis)
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Drafts_AreResolvableFromFreshStoreAndConnection()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var map = NewMapPackage($"map_{suffix}");
        var app = NewAppPackage($"app_{suffix}");
        var retention = new PackageDraftRetentionOptions();

        using (var firstConnection = ConnectionMultiplexer.Connect(redis.ConnectionString))
        {
            var firstReplica = new RedisPackageDraftStore(firstConnection, retention, TimeProvider.System);
            await firstReplica.SaveMapDraftAsync(map);
            await firstReplica.SaveAppDraftAsync(app);
        }

        using var secondConnection = ConnectionMultiplexer.Connect(redis.ConnectionString);
        var secondReplica = new RedisPackageDraftStore(secondConnection, retention, TimeProvider.System);

        (await secondReplica.GetMapDraftAsync(map.MapPackageId))?.MapPackageId.Should().Be(map.MapPackageId);
        (await secondReplica.GetAppDraftAsync(app.AppPackageId))?.AppPackageId.Should().Be(app.AppPackageId);
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Save_AppliesRedisTtlAndOldestFirstCapacityPerKind()
    {
        using var connection = ConnectionMultiplexer.Connect(redis.ConnectionString);
        var clock = new MutableClock(FixedNow);
        var retention = new PackageDraftRetentionOptions { Ttl = TimeSpan.FromMinutes(30), Capacity = 2 };
        var store = new RedisPackageDraftStore(connection, retention, clock);
        var suffix = Guid.NewGuid().ToString("N");
        var first = NewMapPackage($"map_{suffix}_1");
        var second = NewMapPackage($"map_{suffix}_2");
        var third = NewMapPackage($"map_{suffix}_3");

        await store.SaveMapDraftAsync(first);
        var ttl = await connection.GetDatabase().KeyTimeToLiveAsync($"honua:studio:drafts:{{map}}:item:{first.MapPackageId}");
        ttl.Should().NotBeNull();
        ttl.Should().BeCloseTo(retention.Ttl, TimeSpan.FromSeconds(5));

        clock.Advance(TimeSpan.FromSeconds(1));
        await store.SaveMapDraftAsync(second);
        clock.Advance(TimeSpan.FromSeconds(1));
        await store.SaveMapDraftAsync(third);

        (await store.GetMapDraftAsync(first.MapPackageId)).Should().BeNull();
        (await store.GetMapDraftAsync(second.MapPackageId)).Should().NotBeNull();
        (await store.GetMapDraftAsync(third.MapPackageId)).Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public void Registration_ReplacesFallbackWhenRedisIsAvailable()
    {
        using var connection = ConnectionMultiplexer.Connect(redis.ConnectionString);
        var services = new ServiceCollection();
        services.AddStudioDraftFactories();
        services.AddSingleton<IConnectionMultiplexer>(connection);

        services.UseDurableStudioPackageDraftStore();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IPackageDraftStore>().Should().BeOfType<RedisPackageDraftStore>();
    }

    private static MapPackage NewMapPackage(string id) => new()
    {
        MapPackageId = id,
        Format = MapPackageDraftFactory.MapPackageFormat,
        Status = PackageStatus.Draft,
        CreatedAt = FixedNow,
    };

    private static AppPackage NewAppPackage(string id) => new()
    {
        AppPackageId = id,
        TargetSdk = AppPackageDraftFactory.DefaultTargetSdk,
        Format = AppPackageDraftFactory.AppPackageFormat,
        Status = PackageStatus.Draft,
        CreatedAt = FixedNow,
    };

    private sealed class MutableClock(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
