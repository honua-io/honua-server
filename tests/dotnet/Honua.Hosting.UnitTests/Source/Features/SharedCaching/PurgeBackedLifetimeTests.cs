// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.SharedCaching;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using static Honua.Hosting.UnitTests.Features.SharedCaching.PurgeBackedFixtures;

namespace Honua.Hosting.UnitTests.Features.SharedCaching;

// Which responses get a purge-backed CDN lifetime, and that no in-process cache keeps one.
public sealed class PurgeBackedLifetimeTests
{
    private const string PurgeBackedTile = "public, max-age=60, s-maxage=86400, stale-while-revalidate=60, stale-if-error=86400";
    private const string OrdinaryTile = "public, max-age=60, s-maxage=300, stale-while-revalidate=60, stale-if-error=86400";
    private const string PurgeBackedQuery = "public, max-age=0, s-maxage=300, stale-while-revalidate=30, stale-if-error=86400";
    private const string OrdinaryQuery = "public, max-age=0, s-maxage=30, stale-while-revalidate=30, stale-if-error=86400";

    private readonly ManualTimeProvider _time = new();

    private CdnPurgeReliability Reliability(SharedCacheOptions options, ICdnInvalidator invalidator, ICdnPurgeBudget? budget)
        => PurgeBackedFixtures.Reliability(_time, options, invalidator, budget);

    private Task<CdnPurgeReliability> PrimedReliabilityAsync(
        SharedCacheOptions options, ICdnInvalidator? invalidator = null, ICdnPurgeBudget? budget = null)
        => PurgeBackedFixtures.PrimedReliabilityAsync(_time, options, invalidator, budget);

    [UnitTheory]
    [InlineData(nameof(ResponseCacheClass.Tile), PurgeBackedTile)]
    [InlineData(nameof(ResponseCacheClass.FeatureQuery), PurgeBackedQuery)]
    public async Task ListedLayer_PurgesReachCdn_GetsTheLongerCdnLifetimeOnly(string cacheClass, string expected)
    {
        var reliability = await PrimedReliabilityAsync(Options());
        var context = Granted(Enum.Parse<ResponseCacheClass>(cacheClass), Options(), reliability);

        SharedCacheResponse.Apply(context);
        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(expected);
        SharedCacheResponse.IsPurgeBacked(context).Should().BeTrue();
    }

    [UnitTest]
    public async Task AllLayers_AppliesToAnyOneLayerResponse_ButNotToAServiceWideOne()
    {
        var options = Options(configure: purgeBacked => purgeBacked.AllLayers = true, layers: []);
        var reliability = await PrimedReliabilityAsync(options);
        var layer = Granted(ResponseCacheClass.Tile, options, reliability, layerId: "9");
        var service = Granted(ResponseCacheClass.Tile, options, reliability, layerId: null);

        SharedCacheResponse.Apply(layer);
        SharedCacheResponse.Apply(service);

        layer.Response.Headers.CacheControl.ToString().Should().Be(PurgeBackedTile);
        service.Response.Headers.CacheControl.ToString().Should().Be(OrdinaryTile);
    }

    [UnitTest]
    public async Task UnlistedLayer_KeepsTheOrdinaryLifetime()
    {
        var reliability = await PrimedReliabilityAsync(Options());
        var context = Granted(ResponseCacheClass.Tile, Options(), reliability, layerId: "4");

        SharedCacheResponse.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(OrdinaryTile);
        SharedCacheResponse.IsPurgeBacked(context).Should().BeFalse();
    }

    [UnitTest]
    public async Task NoCdnConfigured_KeepsTheOrdinaryLifetime()
    {
        var reliability = await PrimedReliabilityAsync(Options(), new NoCdnInvalidator());
        var context = Granted(ResponseCacheClass.Tile, Options(), reliability);

        SharedCacheResponse.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(OrdinaryTile);
    }

    [UnitTest]
    public async Task CacheTagsOff_KeepsTheOrdinaryLifetime()
    {
        var options = Options(tagHeader: null);
        var reliability = await PrimedReliabilityAsync(options);
        var context = Granted(ResponseCacheClass.Tile, options, reliability);

        SharedCacheResponse.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(OrdinaryTile);
    }

    [UnitTest]
    public void BudgetNotYetRead_KeepsTheOrdinaryLifetime()
    {
        var options = Options();
        var reliability = Reliability(options, new CloudFrontLimits(), new FixedBudget(0));
        var context = Granted(ResponseCacheClass.Tile, options, reliability);

        SharedCacheResponse.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(OrdinaryTile);
    }

    [UnitTheory]
    [InlineData(8, PurgeBackedTile)]
    [InlineData(9, OrdinaryTile)]
    [InlineData(10, OrdinaryTile)]
    public async Task BudgetWithinTheLastTenPercent_KeepsTheOrdinaryLifetime(int submitted, string expected)
    {
        var options = Options(monthlyLimit: 10);
        var reliability = await PrimedReliabilityAsync(options, budget: new FixedBudget(submitted));
        var context = Granted(ResponseCacheClass.Tile, options, reliability);

        SharedCacheResponse.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(expected);
    }

    [UnitTest]
    public async Task UnreadableBudget_KeepsTheOrdinaryLifetime()
    {
        var options = Options(monthlyLimit: 10);
        var reliability = await PrimedReliabilityAsync(options, budget: new FixedBudget(0) { Unreadable = true });
        var context = Granted(ResponseCacheClass.Tile, options, reliability);

        SharedCacheResponse.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(OrdinaryTile);
    }

    [UnitTest]
    public async Task ResponseFromThisTasksCache_KeepsTheOrdinaryLifetime()
    {
        var reliability = await PrimedReliabilityAsync(Options());
        var context = Granted(ResponseCacheClass.FeatureQuery, Options(), reliability);
        var inner = Substitute.For<IResponseCache>();
        inner.GetAsync<string>("query", Arg.Any<CancellationToken>()).Returns("cached");
        var cache = new ProcessCacheHitResponseCache(inner, new HttpContextAccessor { HttpContext = context });

        (await cache.GetAsync<string>("query")).Should().Be("cached");
        SharedCacheResponse.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(OrdinaryQuery);
    }

    [UnitTest]
    public async Task ResponseCacheMissOrFill_KeepsThePurgeBackedLifetime()
    {
        var reliability = await PrimedReliabilityAsync(Options());
        var context = Granted(ResponseCacheClass.FeatureQuery, Options(), reliability);
        var inner = Substitute.For<IResponseCache>();
        inner.GetAsync<string>("query", Arg.Any<CancellationToken>()).Returns((string?)null);
        inner.GetOrCreateAsync("query", Arg.Any<Func<Task<string>>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<Task<string>>>()());
        var cache = new ProcessCacheHitResponseCache(inner, new HttpContextAccessor { HttpContext = context });

        (await cache.GetAsync<string>("query")).Should().BeNull();
        (await cache.GetOrCreateAsync("query", () => Task.FromResult("rendered"), TimeSpan.FromSeconds(30))).Should().Be("rendered");
        SharedCacheResponse.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(PurgeBackedQuery);
    }

    [UnitTest]
    public async Task GetOrCreateServedWithoutTheFactory_CountsAsThisTasksCache()
    {
        var reliability = await PrimedReliabilityAsync(Options());
        var context = Granted(ResponseCacheClass.FeatureQuery, Options(), reliability);
        var inner = Substitute.For<IResponseCache>();
        inner.GetOrCreateAsync("query", Arg.Any<Func<Task<string>>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns("cached");
        var cache = new ProcessCacheHitResponseCache(inner, new HttpContextAccessor { HttpContext = context });

        await cache.GetOrCreateAsync("query", () => Task.FromResult("rendered"), TimeSpan.FromSeconds(30));
        SharedCacheResponse.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(OrdinaryQuery);
    }

    [UnitTest]
    public async Task OperatorLifetimes_WinOverThePurgeBackedOne()
    {
        var emergency = Options();
        emergency.Emergency.Layers.Add("parcels/3");
        var perLayer = Options();
        perLayer.Layers["parcels/3"] = new SharedCacheLifetime { MaxAgeSeconds = 5, SharedMaxAgeSeconds = 20 };
        var emergencyContext = Granted(ResponseCacheClass.Tile, emergency, await PrimedReliabilityAsync(emergency));
        var perLayerContext = Granted(ResponseCacheClass.Tile, perLayer, await PrimedReliabilityAsync(perLayer));

        SharedCacheResponse.Apply(emergencyContext);
        SharedCacheResponse.Apply(perLayerContext);

        emergencyContext.Response.Headers.CacheControl.ToString().Should().Contain("s-maxage=15");
        perLayerContext.Response.Headers.CacheControl.ToString().Should().Contain("s-maxage=20");
        SharedCacheResponse.IsPurgeBacked(perLayerContext).Should().BeFalse();
    }

    [UnitTest]
    public async Task OutputCache_RefusesToStoreAPurgeBackedResponse_ButStoresAnOrdinaryOne()
    {
        var reliability = await PrimedReliabilityAsync(Options());
        var purgeBacked = OutputCacheContext(Granted(ResponseCacheClass.Tile, Options(), reliability));
        var ordinary = OutputCacheContext(Granted(ResponseCacheClass.Tile, Options(), reliability, layerId: "4"));

        CapOutputCacheLifetimeAtCdnLifetimePolicy.Apply(purgeBacked);
        CapOutputCacheLifetimeAtCdnLifetimePolicy.Apply(ordinary);

        purgeBacked.AllowCacheStorage.Should().BeFalse();
        ordinary.AllowCacheStorage.Should().BeTrue();
        ordinary.ResponseExpirationTimeSpan.Should().Be(TimeSpan.FromSeconds(300));
    }

    [UnitTest]
    public async Task DecisionWritten_HoldsForTheRestOfTheRequest_EvenIfPurgesStopReachingTheCdn()
    {
        var options = Options();
        var reliability = await PrimedReliabilityAsync(options);
        var context = Granted(ResponseCacheClass.Tile, options, reliability);
        SharedCacheResponse.Apply(context);

        reliability.RecordFailedPurge();
        var outputCache = OutputCacheContext(context);
        CapOutputCacheLifetimeAtCdnLifetimePolicy.Apply(outputCache);

        context.Response.Headers.CacheControl.ToString().Should().Be(PurgeBackedTile);
        outputCache.AllowCacheStorage.Should().BeFalse();
    }
}
