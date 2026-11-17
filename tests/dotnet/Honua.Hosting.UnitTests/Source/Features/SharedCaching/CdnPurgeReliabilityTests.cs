// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Infrastructure.SharedCaching;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using static Honua.Hosting.UnitTests.Features.SharedCaching.PurgeBackedFixtures;

namespace Honua.Hosting.UnitTests.Features.SharedCaching;

// When an edit made now would reach the CDN: budget, failures on any task, and how fresh that answer is.
public sealed class CdnPurgeReliabilityTests
{
    private readonly ManualTimeProvider _time = new();

    private Task<CdnPurgeReliability> PrimedReliabilityAsync(
        SharedCacheOptions options, ICdnInvalidator? invalidator = null, ICdnPurgeBudget? budget = null)
        => PurgeBackedFixtures.PrimedReliabilityAsync(_time, options, invalidator, budget);

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
        failingTask.RecordDeliveredPurge();
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

        // No request reaches the failing task: its renewal runs on the clock alone.
        for (var minute = 1; minute <= 20; minute++)
        {
            await _time.TimerIsWaiting();
            _time.Advance(CdnPurgeReliability.SharedReadInterval);
            await leases.RenewedAtLeast(minute);
        }

        await otherTask.ReadSharedSignalsAsync(options.PurgeBudget, CancellationToken.None);
        otherTask.PurgesReachCdn().Should().BeFalse("the failing task keeps renewing the shared lease");
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

    [UnitTheory]
    [InlineData(nameof(CdnInvalidationOutcome.Busy), true)]
    [InlineData(nameof(CdnInvalidationOutcome.Failed), false)]
    public async Task ShutdownFlush_OnlyARefusal_HoldsOffLongLifetimes(string outcome, bool expectedReach)
    {
        var options = Options();
        var reliability = await PrimedReliabilityAsync(options);
        var service = new CdnInvalidationService(
            new FixedOutcomeInvalidator(Enum.Parse<CdnInvalidationOutcome>(outcome)),
            NullLogger<CdnInvalidationService>.Instance,
            new CdnPurgeCoordination { TimeProvider = _time, Options = new StaticOptions(options), Reliability = reliability });
        service.InvalidateWithNextWindow(["#honua-layer:3"]);

        await service.FlushPendingAsync();

        reliability.PurgesReachCdn().Should().Be(expectedReach, "a busy answer to a send without retries says too little");
    }

    [UnitTest]
    public async Task PurgeSubmittedAfterTheShutdownFlush_LogsItsSettleInsteadOfSchedulingIt()
    {
        var logger = new RecordingLogger();
        var service = new CdnInvalidationService(
            new FixedOutcomeInvalidator(CdnInvalidationOutcome.Submitted), logger,
            new CdnPurgeCoordination { TimeProvider = _time, Options = new StaticOptions(Options(monthlyLimit: 0)) });
        await service.FlushPendingAsync();

        await service.InvalidateNowAsync(["#honua-layer:3"], CancellationToken.None);

        _time.WaitingTimers.Should().Be(0);
        logger.Messages.Should().Contain(message => message.StartsWith("A stopping task could not send", StringComparison.Ordinal));
    }
}
