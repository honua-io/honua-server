// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Infrastructure.Coordination;
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
    public async Task FailedPurge_HoldsOffLongLifetimes_UntilOneIsDeliveredAndForFifteenMinutes()
    {
        var options = Options();
        var reliability = await PrimedReliabilityAsync(options, new FailingInvalidator());
        var service = new CdnInvalidationService(
            new FailingInvalidator(),
            NullLogger<CdnInvalidationService>.Instance,
            new CdnPurgeCoordination { TimeProvider = _time, Options = new StaticOptions(options), Reliability = reliability });

        await service.InvalidateNowAsync(["#honua-layer:3"], CancellationToken.None);

        reliability.PurgesReachCdn().Should().BeFalse();
        _time.Advance(CdnPurgeReliability.FailureHoldOff);
        await reliability.ReadSharedSignalsAsync(options.PurgeBudget, CancellationToken.None);
        reliability.PurgesReachCdn().Should().BeFalse("no purge has been delivered since the failure");

        reliability.RecordDeliveredPurge();
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
            new CdnPurgeCoordination { TimeProvider = _time, Options = new StaticOptions(options), Budget = new FixedBudget(1), Reliability = reliability });

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
        _time.Advance(PurgeBackedDelivery.FirstRetryDelay);
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

        // A retry is scheduled synchronously inside the awaited call, so its timer would already be waiting.
        _time.WaitingTimers.Should().Be(0);
        cdn.Attempts.Should().Be(1);
    }

    [UnitTheory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(4, 40)]
    [InlineData(5, 60)]
    [InlineData(30, 60)]
    public void RetryDelay_DoublesFromFiveMinutes_UpToHourly(int attempts, int expectedMinutes)
        => PurgeBackedDelivery.RetryDelay(attempts).Should().Be(TimeSpan.FromMinutes(expectedMinutes));

    [UnitTest]
    public async Task FailingEditPurge_StopsRetrying_OnceEveryPurgeBackedCopyHasExpired()
    {
        var cdn = new FlakyInvalidator(failures: int.MaxValue);
        var options = Options(monthlyLimit: 0, configure: purgeBacked =>
        {
            purgeBacked.Tile = new SharedCacheLifetime { SharedMaxAgeSeconds = 600 };
            purgeBacked.FeatureQuery = new SharedCacheLifetime { SharedMaxAgeSeconds = 60 };
        });
        var logger = new RecordingLogger();
        var service = new CdnInvalidationService(
            cdn, logger, new CdnPurgeCoordination { TimeProvider = _time, Options = new StaticOptions(options) });

        await service.InvalidateAfterEditAsync(["#honua-layer:3"], CancellationToken.None);
        await _time.TimerIsWaiting();
        _time.Advance(PurgeBackedDelivery.RetryDelay(1));
        await cdn.AttemptsReach(2);
        await _time.TimerIsWaiting();
        _time.Advance(PurgeBackedDelivery.RetryDelay(2));
        await cdn.AttemptsReach(3);

        // Fifteen minutes after the first failure the 600 s copies have expired: the third failure is given up.
        await Eventually(() => logger.Messages.Any(message => message.StartsWith("Stopped retrying", StringComparison.Ordinal)));
        _time.Advance(PurgeBackedDelivery.MaximumRetryDelay);
        cdn.Attempts.Should().Be(3);
    }

    [UnitTest]
    public async Task SentEditPurge_WithPurgeBackedLayers_IsRepeatedOnceAfterTheSettleDelay()
    {
        var cdn = new FlakyInvalidator(failures: 0);
        var logger = new RecordingLogger();
        var service = new CdnInvalidationService(
            cdn, logger, new CdnPurgeCoordination { TimeProvider = _time, Options = new StaticOptions(Options(monthlyLimit: 0)) });

        await service.InvalidateAfterEditAsync(["#honua-layer:3"], CancellationToken.None);
        await _time.TimerIsWaiting();
        _time.Advance(PurgeBackedDelivery.SettleDelay);
        await cdn.AttemptsReach(2);
        await Eventually(() => logger.Messages.Any(message => message.Contains("change=settle", StringComparison.Ordinal)));

        _time.Advance(PurgeBackedDelivery.SettleDelay * 2);
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        cdn.Attempts.Should().Be(2, "the settle repeat is not repeated again");
        cdn.Sent.Should().AllSatisfy(paths => paths.Should().Equal("#honua-layer:3"));
    }

    [UnitTest]
    public async Task SentEditPurge_WithoutCacheTags_IsNotRepeated()
    {
        var cdn = new FlakyInvalidator(failures: 0);
        var service = new CdnInvalidationService(
            cdn, NullLogger<CdnInvalidationService>.Instance,
            new CdnPurgeCoordination { TimeProvider = _time, Options = new StaticOptions(Options(tagHeader: null, monthlyLimit: 0)) });

        await service.InvalidateAfterEditAsync(["/rest/services/parcels/*"], CancellationToken.None);

        _time.WaitingTimers.Should().Be(0, "a settle timer is created inside the awaited call when one is scheduled");
        cdn.Attempts.Should().Be(1);
    }

    [UnitTest]
    public async Task SubmittedAdminPurge_WithPurgeBackedLayers_AlsoSettles()
    {
        var cdn = new FlakyInvalidator(failures: 0);
        var service = new CdnInvalidationService(
            cdn, NullLogger<CdnInvalidationService>.Instance, new CdnPurgeCoordination { TimeProvider = _time, Options = new StaticOptions(Options(monthlyLimit: 0)) });

        await service.InvalidateNowAsync(["/*"], CancellationToken.None);
        await _time.TimerIsWaiting();
        _time.Advance(PurgeBackedDelivery.SettleDelay);
        await cdn.AttemptsReach(2);

        cdn.Sent[^1].Should().Equal("/*");
    }

    [UnitTest]
    public async Task StoppingTask_SendsTheEditPurgesItStillHolds()
    {
        var cdn = new FlakyInvalidator(failures: 1);
        var service = new CdnInvalidationService(
            cdn, NullLogger<CdnInvalidationService>.Instance, new CdnPurgeCoordination { TimeProvider = _time, Options = new StaticOptions(Options(monthlyLimit: 0)) });
        await service.InvalidateAfterEditAsync(["#honua-layer:3"], CancellationToken.None);

        await new CdnPendingPurgeFlush(service).StopAsync(CancellationToken.None);

        cdn.Attempts.Should().Be(2);
        cdn.Sent[^1].Should().Equal("#honua-layer:3");
    }

    [UnitTest]
    public async Task FailureOnAnotherTask_StopsLongLifetimesHereAtTheNextRead()
    {
        var options = Options();
        var leases = new ReleasableLeases(_time);
        var failingTask = new CdnPurgeReliability(new CloudFrontLimits(), NullLogger<CdnPurgeReliability>.Instance,
            new CdnPurgeCoordination { TimeProvider = _time, Options = new StaticOptions(options), LeaseStore = leases });
        var otherTask = new CdnPurgeReliability(new CloudFrontLimits(), NullLogger<CdnPurgeReliability>.Instance,
            new CdnPurgeCoordination { TimeProvider = _time, Options = new StaticOptions(options), LeaseStore = leases, Budget = new FixedBudget(0) });
        await otherTask.ReadSharedSignalsAsync(options.PurgeBudget, CancellationToken.None);
        otherTask.PurgesReachCdn().Should().BeTrue();

        failingTask.RecordFailedPurge();
        await leases.Held(CdnPurgeReliability.FailureLeaseKey);
        await otherTask.ReadSharedSignalsAsync(options.PurgeBudget, CancellationToken.None);

        otherTask.PurgesReachCdn().Should().BeFalse();
        _time.Advance(CdnPurgeReliability.FailureHoldOff);
        await otherTask.ReadSharedSignalsAsync(options.PurgeBudget, CancellationToken.None);
        otherTask.PurgesReachCdn().Should().BeTrue();
    }

    [UnitTest]
    public async Task TaskStillFailing_KeepsOtherTasksHeldOff_PastFifteenMinutes()
    {
        var options = Options();
        var leases = new ReleasableLeases(_time);
        var failingTask = new CdnPurgeReliability(new CloudFrontLimits(), NullLogger<CdnPurgeReliability>.Instance,
            new CdnPurgeCoordination { TimeProvider = _time, Options = new StaticOptions(options), LeaseStore = leases, Budget = new FixedBudget(0) });
        var otherTask = new CdnPurgeReliability(new CloudFrontLimits(), NullLogger<CdnPurgeReliability>.Instance,
            new CdnPurgeCoordination { TimeProvider = _time, Options = new StaticOptions(options), LeaseStore = leases, Budget = new FixedBudget(0) });
        failingTask.RecordFailedPurge();
        await leases.Held(CdnPurgeReliability.FailureLeaseKey);

        _time.Advance(TimeSpan.FromMinutes(10));
        await failingTask.ReadSharedSignalsAsync(options.PurgeBudget, CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(10));
        await otherTask.ReadSharedSignalsAsync(options.PurgeBudget, CancellationToken.None);

        otherTask.PurgesReachCdn().Should().BeFalse("the failing task renewed the shared lease on its own read");
        failingTask.PurgesReachCdn().Should().BeFalse();
    }

    [UnitTest]
    public async Task SharedSignalsNotReadForThreeIntervals_KeepTheOrdinaryLifetime()
    {
        var options = Options();
        var reliability = await PrimedReliabilityAsync(options);

        _time.Advance(CdnPurgeReliability.SharedReadInterval * 3);

        reliability.PurgesReachCdn().Should().BeFalse();
    }

    [UnitTest]
    public async Task UnavailableBudgetDecision_KeepsTheOrdinaryLifetime()
    {
        var options = Options(monthlyLimit: 10);
        var reliability = await PrimedReliabilityAsync(options, budget: new FixedBudget(0) { Decision = CdnPurgeBudgetDecision.Unavailable });

        reliability.PurgesReachCdn().Should().BeFalse();
    }

    [UnitTest]
    public async Task ReadStartedBeforeTheBudgetWasSpent_DoesNotRestoreLongLifetimes()
    {
        var options = Options(monthlyLimit: 10);
        var budget = new GatedBudget();
        var reliability = await PrimedReliabilityAsync(options, budget: budget);
        reliability.PurgesReachCdn().Should().BeTrue();

        budget.Hold();
        var read = reliability.ReadSharedSignalsAsync(options.PurgeBudget, CancellationToken.None);
        reliability.RecordBudgetSpent();
        budget.Release();
        await read;

        reliability.PurgesReachCdn().Should().BeFalse();
    }

    [UnitTheory]
    [InlineData(5, 1)]
    [InlineData(10, 1)]
    [InlineData(11, 2)]
    [InlineData(1000, 100)]
    public void BudgetReserve_RoundsUp(int limit, int expected)
        => SharedCachePurgeBacked.BudgetReserve(limit).Should().Be(expected);

    [UnitTest]
    public void Delivery_FailureRecord_EndsOnAnyOutcomeButFailure()
    {
        var delivery = new PurgeBackedDelivery(_time);
        delivery.Failed(["#a"], TimeSpan.FromHours(1));
        _time.Advance(TimeSpan.FromHours(2));

        delivery.Ended(["#a"]);

        delivery.Failed(["#a"], TimeSpan.FromHours(1)).Paths.Should().Equal("#a");
    }

    [UnitTest]
    public void Delivery_EditOfAFailingPath_RestartsItsGiveUpClock()
    {
        var delivery = new PurgeBackedDelivery(_time);
        delivery.Failed(["#a"], TimeSpan.FromHours(1));
        _time.Advance(TimeSpan.FromMinutes(50));
        delivery.EditQueued(["#a"]);
        _time.Advance(TimeSpan.FromMinutes(50));

        var plan = delivery.Failed(["#a"], TimeSpan.FromHours(1));

        plan.Paths.Should().Equal("#a");
        plan.Abandoned.Should().Be(0);
        plan.Delay.Should().Be(PurgeBackedDelivery.RetryDelay(2));
    }

    [UnitTest]
    public void Delivery_SettleOnlyPath_DoesNotSettleAgain_UnlessAnEditQueuesItAgain()
    {
        var delivery = new PurgeBackedDelivery(_time);
        delivery.SettleOnly(["#a", "#b"]);
        delivery.EditQueued(["#b"]);

        var due = delivery.ScheduleSettle(["#a", "#b"]);
        _time.Advance(PurgeBackedDelivery.SettleDelay);

        due.Should().NotBeNull();
        delivery.TakeDueSettles(due!.Value).Due.Should().Equal("#b");
    }

    [UnitTest]
    public void Delivery_PathPurgedAgainBeforeItsSettle_WaitsForItsOwnDelay()
    {
        var delivery = new PurgeBackedDelivery(_time);
        var first = delivery.ScheduleSettle(["#a"]);
        _time.Advance(TimeSpan.FromSeconds(40));
        delivery.ScheduleSettle(["#a", "#b"]).Should().BeNull("one timer is already waiting");
        _time.Advance(TimeSpan.FromSeconds(20));

        var batch = delivery.TakeDueSettles(first!.Value);

        batch.Due.Should().BeEmpty();
        batch.Next.Should().Be(_time.GetUtcNow() + TimeSpan.FromSeconds(40));
        _time.Advance(TimeSpan.FromSeconds(40));
        delivery.TakeDueSettles(batch.Next!.Value).Due.Should().BeEquivalentTo(["#a", "#b"]);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the expected outcome did not happen in time");
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
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
        await reliability.ReadSharedSignalsAsync(options.PurgeBudget, CancellationToken.None);
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

        public CdnPurgeBudgetDecision Decision { get; init; } = CdnPurgeBudgetDecision.Admitted;

        public Task<CdnPurgeAdmission> TryAdmitEditAsync(int monthlyLimit, decimal unitCost, CancellationToken cancellationToken = default)
            => Task.FromResult(submitted >= monthlyLimit
                ? new CdnPurgeAdmission(CdnPurgeBudgetDecision.Exceeded, submitted, 0)
                : new CdnPurgeAdmission(CdnPurgeBudgetDecision.Admitted, submitted + 1, 0));

        public Task<CdnPurgeBudgetMonth?> ReadCurrentMonthAsync(CancellationToken cancellationToken = default)
            => Unreadable
                ? throw new InvalidOperationException("budget store down")
                : Task.FromResult<CdnPurgeBudgetMonth?>(new CdnPurgeBudgetMonth(submitted, 0, Decision));
    }

    private sealed class FlakyInvalidator(int failures) : ICdnInvalidator
    {
        private readonly Lock _gate = new();
        private readonly TaskCompletionSource _submitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string[]> _sent = [];
        private TaskCompletionSource _attempted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Attempts
        {
            get
            {
                lock (_gate)
                {
                    return _sent.Count;
                }
            }
        }

        public IReadOnlyList<string[]> Sent
        {
            get
            {
                lock (_gate)
                {
                    return [.. _sent];
                }
            }
        }

        public Task Submitted => _submitted.Task;

        public async Task AttemptsReach(int count)
        {
            while (true)
            {
                Task attempted;
                lock (_gate)
                {
                    if (_sent.Count >= count)
                    {
                        return;
                    }

                    attempted = _attempted.Task;
                }

                await attempted.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        public Task<CdnInvalidationResult> InvalidateAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
        {
            bool fail;
            TaskCompletionSource attempted;
            lock (_gate)
            {
                _sent.Add([.. paths]);
                fail = _sent.Count <= failures;
                attempted = _attempted;
                _attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            attempted.TrySetResult();
            if (fail)
            {
                return Task.FromResult(new CdnInvalidationResult(CdnInvalidationOutcome.Failed, "refused"));
            }

            _submitted.TrySetResult();
            return Task.FromResult(new CdnInvalidationResult(CdnInvalidationOutcome.Submitted, "I1"));
        }
    }

    private sealed class ReleasableLeases(TimeProvider clock) : ILeaseStore
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, (string Owner, DateTimeOffset ExpiresAt)> _leases = new(StringComparer.Ordinal);
        private TaskCompletionSource _acquired = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task Held(string key)
        {
            while (true)
            {
                Task acquired;
                lock (_gate)
                {
                    if (_leases.TryGetValue(key, out var lease) && lease.ExpiresAt > clock.GetUtcNow())
                    {
                        return;
                    }

                    acquired = _acquired.Task;
                }

                await acquired.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        public Task<bool> TryAcquireAsync(LeaseClaim claim, TimeSpan duration, CancellationToken cancellationToken = default)
        {
            TaskCompletionSource acquired;
            lock (_gate)
            {
                if (_leases.TryGetValue(claim.LeaseKey, out var lease) && lease.ExpiresAt > clock.GetUtcNow())
                {
                    return Task.FromResult(false);
                }

                _leases[claim.LeaseKey] = (claim.OwnerId, clock.GetUtcNow() + duration);
                acquired = _acquired;
                _acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            acquired.TrySetResult();
            return Task.FromResult(true);
        }

        public Task<bool> RenewAsync(LeaseClaim claim, TimeSpan duration, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_leases.TryGetValue(claim.LeaseKey, out var lease) && lease.Owner == claim.OwnerId && lease.ExpiresAt > clock.GetUtcNow())
                {
                    _leases[claim.LeaseKey] = (claim.OwnerId, clock.GetUtcNow() + duration);
                    return Task.FromResult(true);
                }

                return Task.FromResult(false);
            }
        }

        public Task ReleaseAsync(LeaseClaim claim, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_leases.TryGetValue(claim.LeaseKey, out var lease) && lease.Owner == claim.OwnerId)
                {
                    _leases.Remove(claim.LeaseKey);
                }
            }

            return Task.CompletedTask;
        }
    }

    // Reports an unspent month, optionally only once the test lets the read finish.
    private sealed class GatedBudget : ICdnPurgeBudget
    {
        private TaskCompletionSource _gate = CompletedGate();

        public void Hold() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        public Task<CdnPurgeAdmission> TryAdmitEditAsync(int monthlyLimit, decimal unitCost, CancellationToken cancellationToken = default)
            => Task.FromResult(new CdnPurgeAdmission(CdnPurgeBudgetDecision.Admitted, 1, 0));

        public async Task<CdnPurgeBudgetMonth?> ReadCurrentMonthAsync(CancellationToken cancellationToken = default)
        {
            await _gate.Task.WaitAsync(cancellationToken);
            return new CdnPurgeBudgetMonth(0, 0, CdnPurgeBudgetDecision.Admitted);
        }

        private static TaskCompletionSource CompletedGate()
        {
            var gate = new TaskCompletionSource();
            gate.SetResult();
            return gate;
        }
    }

    private sealed class FailingInvalidator : ICdnInvalidator
    {
        public Task<CdnInvalidationResult> InvalidateAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
            => Task.FromResult(new CdnInvalidationResult(CdnInvalidationOutcome.Failed, "refused"));
    }
}
