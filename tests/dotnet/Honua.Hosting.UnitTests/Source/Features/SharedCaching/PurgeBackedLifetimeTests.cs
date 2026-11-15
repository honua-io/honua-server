// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.SharedCaching;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Hosting.UnitTests.Features.SharedCaching;

public sealed class PurgeBackedLifetimeTests
{
    private const string PurgeBackedTile = "public, max-age=60, s-maxage=86400, stale-while-revalidate=60, stale-if-error=86400";
    private const string OrdinaryTile = "public, max-age=60, s-maxage=300, stale-while-revalidate=60, stale-if-error=86400";
    private const string PurgeBackedQuery = "public, max-age=0, s-maxage=300, stale-while-revalidate=30, stale-if-error=86400";
    private const string OrdinaryQuery = "public, max-age=0, s-maxage=30, stale-while-revalidate=30, stale-if-error=86400";

    private readonly ManualTimeProvider _time = new();

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
    public async Task FailedPurge_HoldsOffLongLifetimes_ForFifteenMinutes()
    {
        var options = Options();
        var reliability = await PrimedReliabilityAsync(options, new FailingInvalidator());
        var service = new CdnInvalidationService(
            new FailingInvalidator(),
            NullLogger<CdnInvalidationService>.Instance,
            new CdnPurgeCoordination { TimeProvider = _time, Options = new StaticOptions(options) },
            reliability);

        await service.InvalidateNowAsync(["#honua-layer:3"], CancellationToken.None);

        reliability.PurgesReachCdn().Should().BeFalse();
        _time.Advance(CdnPurgeReliability.FailureHoldOff);
        await reliability.ReadBudgetAsync(options.PurgeBudget, CancellationToken.None);
        reliability.PurgesReachCdn().Should().BeTrue();
    }

    [UnitTest]
    public async Task SpentBudgetSeenByAnEditPurge_StopsLongLifetimesWithoutWaitingForTheNextRead()
    {
        var options = Options(monthlyLimit: 1);
        var reliability = await PrimedReliabilityAsync(options, budget: new FixedBudget(0));
        var service = new CdnInvalidationService(
            new CloudFrontLimits(),
            NullLogger<CdnInvalidationService>.Instance,
            new CdnPurgeCoordination { TimeProvider = _time, Options = new StaticOptions(options), Budget = new FixedBudget(1) },
            reliability);

        await service.InvalidateAfterEditAsync(["#honua-layer:3"], CancellationToken.None);

        reliability.PurgesReachCdn().Should().BeFalse();
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

    [UnitTest]
    public async Task FailedEditPurge_WithPurgeBackedLayers_IsSentAgainAfterTheRetryDelay()
    {
        var cdn = new FlakyInvalidator(failures: 1);
        var service = new CdnInvalidationService(
            cdn,
            NullLogger<CdnInvalidationService>.Instance,
            new CdnPurgeCoordination { TimeProvider = _time, Options = new StaticOptions(Options(monthlyLimit: 0)) });

        await service.InvalidateAfterEditAsync(["#honua-layer:3"], CancellationToken.None);
        await _time.TimerIsWaiting();
        _time.Advance(CdnInvalidationService.FailedEditPurgeRetryDelay);
        await cdn.Submitted.WaitAsync(TimeSpan.FromSeconds(10));

        cdn.Attempts.Should().Be(2);
    }

    [UnitTest]
    public async Task FailedEditPurge_WithoutPurgeBackedLayers_KeepsTheLifetimeAsTheBackstop()
    {
        var cdn = new FlakyInvalidator(failures: 1);
        var service = new CdnInvalidationService(
            cdn,
            NullLogger<CdnInvalidationService>.Instance,
            new CdnPurgeCoordination { TimeProvider = _time, Options = new StaticOptions(Options(monthlyLimit: 0, layers: [])) });

        await service.InvalidateAfterEditAsync(["#honua-layer:3"], CancellationToken.None);
        _time.Advance(CdnInvalidationService.FailedEditPurgeRetryDelay);

        cdn.Attempts.Should().Be(1);
    }

    private static SharedCacheOptions Options(
        string? tagHeader = "x-cache-tag",
        int monthlyLimit = 1000,
        string[]? layers = null,
        Action<SharedCachePurgeBacked>? configure = null)
    {
        var options = new SharedCacheOptions
        {
            CacheTagHeader = tagHeader,
            PurgeBudget = new CdnPurgeBudgetOptions { MonthlyEditSubmissions = monthlyLimit },
        };
        options.PurgeBacked.Layers.AddRange(layers ?? ["parcels/3"]);
        configure?.Invoke(options.PurgeBacked);
        return options;
    }

    private CdnPurgeReliability Reliability(SharedCacheOptions options, ICdnInvalidator invalidator, ICdnPurgeBudget? budget)
        => new(invalidator, NullLogger<CdnPurgeReliability>.Instance, new CdnPurgeCoordination
        {
            TimeProvider = _time,
            Options = new StaticOptions(options),
            Budget = budget,
        });

    private async Task<CdnPurgeReliability> PrimedReliabilityAsync(
        SharedCacheOptions options, ICdnInvalidator? invalidator = null, ICdnPurgeBudget? budget = null)
    {
        var reliability = Reliability(options, invalidator ?? new CloudFrontLimits(), budget ?? new FixedBudget(0));
        await reliability.ReadBudgetAsync(options.PurgeBudget, CancellationToken.None);
        return reliability;
    }

    private static DefaultHttpContext Granted(
        ResponseCacheClass cacheClass, SharedCacheOptions options, CdnPurgeReliability reliability, string? layerId = "3")
    {
        var services = new ServiceCollection()
            .AddSingleton<IOptionsMonitor<SharedCacheOptions>>(new StaticOptions(options))
            .AddSingleton(reliability)
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = HttpMethods.Get;
        context.Request.RouteValues["serviceId"] = "parcels";
        if (layerId is not null)
        {
            context.Request.RouteValues["layerId"] = layerId;
        }

        context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new SharedCacheClassMetadata(cacheClass)), "declared"));
        ResponseAccessVerdict.Record(context, AccessDecision.Allowed());
        return context;
    }

    private static OutputCacheContext OutputCacheContext(HttpContext context)
        => new() { HttpContext = context, AllowCacheStorage = true, ResponseExpirationTimeSpan = TimeSpan.FromHours(1) };

    private sealed class FixedBudget(int submitted) : ICdnPurgeBudget
    {
        public bool Unreadable { get; init; }

        public Task<CdnPurgeAdmission> TryAdmitEditAsync(int monthlyLimit, decimal unitCost, CancellationToken cancellationToken = default)
            => Task.FromResult(submitted >= monthlyLimit
                ? new CdnPurgeAdmission(CdnPurgeBudgetDecision.Exceeded, submitted, 0)
                : new CdnPurgeAdmission(CdnPurgeBudgetDecision.Admitted, submitted + 1, 0));

        public Task<CdnPurgeBudgetMonth?> ReadCurrentMonthAsync(CancellationToken cancellationToken = default)
            => Unreadable
                ? throw new InvalidOperationException("budget store down")
                : Task.FromResult<CdnPurgeBudgetMonth?>(new CdnPurgeBudgetMonth(submitted, 0, CdnPurgeBudgetDecision.Admitted));
    }

    private sealed class FlakyInvalidator(int failures) : ICdnInvalidator
    {
        private readonly TaskCompletionSource _submitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public Task Submitted => _submitted.Task;

        public Task<CdnInvalidationResult> InvalidateAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _attempts) <= failures)
            {
                return Task.FromResult(new CdnInvalidationResult(CdnInvalidationOutcome.Failed, "refused"));
            }

            _submitted.TrySetResult();
            return Task.FromResult(new CdnInvalidationResult(CdnInvalidationOutcome.Submitted, "I1"));
        }
    }

    private sealed class FailingInvalidator : ICdnInvalidator
    {
        public Task<CdnInvalidationResult> InvalidateAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
            => Task.FromResult(new CdnInvalidationResult(CdnInvalidationOutcome.Failed, "refused"));
    }
}
