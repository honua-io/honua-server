// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Infrastructure.SharedCaching;
using Honua.TestKit.Attributes;
using static Honua.Hosting.UnitTests.Features.SharedCaching.PurgeBackedFixtures;

namespace Honua.Hosting.UnitTests.Features.SharedCaching;

public sealed class PurgeCauseCoalescingTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly RecordingLogger _logger = new();

    [UnitTest]
    public async Task FailedAdminSettle_NewEditCoalesces_ExemptionAndNewRepeatSurvive()
    {
        var cdn = new ScriptedInvalidator(attempt => attempt == 2
            ? CdnInvalidationOutcome.Failed : CdnInvalidationOutcome.Submitted);
        var service = Service(cdn);

        await service.InvalidateNowAsync(["/*"], CancellationToken.None, CdnPurgeCause.Admin("admin-123"));
        await _time.TimerIsWaiting();
        _time.Advance(PurgeBackedDelivery.SettleDelay);
        await Eventually(() => cdn.Attempts == 2 && _time.WaitingTimers == 1);
        await service.InvalidateAfterEditAsync(["/*"], CancellationToken.None);
        _time.Advance(CdnInvalidationService.CoalescingWindow);
        // The old failure timer remains, alongside the new change's settle timer.
        await Eventually(() => cdn.Attempts == 3 && _time.WaitingTimers == 2);
        _time.Advance(PurgeBackedDelivery.SettleDelay);

        await Eventually(() => PurgeRecords("admin-123") == 4);
        cdn.Attempts.Should().Be(4);
    }

    [UnitTest]
    public async Task BusyAdminSettle_RetryCompletes_NextAdminChangeGetsItsOwnRepeat()
    {
        var cdn = new ScriptedInvalidator(attempt => attempt is >= 2 and <= 5
            ? CdnInvalidationOutcome.Busy : CdnInvalidationOutcome.Submitted);
        var service = Service(cdn);

        await service.InvalidateNowAsync(["/*"], CancellationToken.None, CdnPurgeCause.Admin("admin-123"));
        await _time.TimerIsWaiting();
        _time.Advance(PurgeBackedDelivery.SettleDelay);
        var attempts = 2;
        foreach (var delay in CdnInvalidationService.BusyRetryDelays)
        {
            await Eventually(() => cdn.Attempts == attempts && _time.WaitingTimers == 1);
            _time.Advance(delay);
            attempts++;
        }

        await Eventually(() => cdn.Attempts == 5 && _time.WaitingTimers == 1);
        _time.Advance(CdnInvalidationService.CoalescingWindow);
        await Eventually(() => cdn.Attempts == 6 && _time.WaitingTimers == 0);
        await service.InvalidateNowAsync(["/*"], CancellationToken.None, CdnPurgeCause.Admin("admin-456"));
        await _time.TimerIsWaiting();
        _time.Advance(PurgeBackedDelivery.SettleDelay);

        await Eventually(() => PurgeRecords("admin-456") == 2);
        cdn.Attempts.Should().Be(8);
    }

    private CdnInvalidationService Service(ICdnInvalidator cdn)
        => new(cdn, _logger, new CdnPurgeCoordination
        {
            TimeProvider = _time,
            Options = new StaticOptions(Options(monthlyLimit: 1)),
            Budget = new FixedBudget(1),
        });

    private int PurgeRecords(string changeId)
        => _logger.Messages.Count(message => message.Contains("change=" + changeId, StringComparison.Ordinal));

    private sealed class ScriptedInvalidator(Func<int, CdnInvalidationOutcome> outcome) : ICdnInvalidator
    {
        private int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);

        public Task<CdnInvalidationResult> InvalidateAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
            => Task.FromResult(new CdnInvalidationResult(outcome(Interlocked.Increment(ref _attempts)), "test"));
    }
}
