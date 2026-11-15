// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.SharedCaching;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using static Honua.Hosting.UnitTests.Features.SharedCaching.PurgeBackedFixtures;

namespace Honua.Hosting.UnitTests.Features.SharedCaching;

// Retries, settle repeats and the shutdown flush that purge-backed lifetimes add to purges.
public sealed class PurgeBackedDeliveryTests
{
    private readonly ManualTimeProvider _time = new();


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
}
