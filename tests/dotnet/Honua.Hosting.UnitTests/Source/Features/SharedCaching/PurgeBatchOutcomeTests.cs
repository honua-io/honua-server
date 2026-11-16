// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Infrastructure.SharedCaching;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using static Honua.Hosting.UnitTests.Features.SharedCaching.PurgeBackedFixtures;

namespace Honua.Hosting.UnitTests.Features.SharedCaching;

public sealed class PurgeBatchOutcomeTests
{
    [UnitTheory]
    [InlineData(CdnInvalidationOutcome.Busy)]
    [InlineData(CdnInvalidationOutcome.Failed)]
    public async Task InvalidateAfterEdit_MixedBatchOutcomes_RepeatsSuccessfulBatchAndRetriesUnsentBatch(CdnInvalidationOutcome unsent)
    {
        var time = new ManualTimeProvider();
        var cdn = new MixedBatchInvalidator(unsent);
        var service = Service(cdn, time);
        var initial = service.InvalidateAfterEditAsync(Tags(), CancellationToken.None);
        if (unsent is CdnInvalidationOutcome.Busy)
        {
            var attempts = 2;
            foreach (var delay in CdnInvalidationService.BusyRetryDelays)
            {
                await Eventually(() => cdn.Calls.Count == attempts && time.WaitingTimers == 1);
                time.Advance(delay);
                attempts++;
            }
        }

        await initial;
        await Eventually(() => time.WaitingTimers == 2);
        if (unsent is CdnInvalidationOutcome.Busy)
        {
            time.Advance(CdnInvalidationService.CoalescingWindow);
            await Eventually(() => cdn.Calls.Count == 6 && time.WaitingTimers == 2);
            time.Advance(PurgeBackedDelivery.SettleDelay - CdnInvalidationService.CoalescingWindow);
        }
        else
        {
            time.Advance(PurgeBackedDelivery.SettleDelay);
        }

        await Eventually(() => cdn.Calls.Count(paths => paths.Length == CdnInvalidationService.MaxTagsPerInvalidation) == 2);
        if (unsent is CdnInvalidationOutcome.Failed)
        {
            time.Advance(PurgeBackedDelivery.FirstRetryDelay - PurgeBackedDelivery.SettleDelay);
            await Eventually(() => cdn.Calls.Count == 4);
            cdn.Calls.Last().Should().Equal(Tags().TakeLast(1));
        }

        cdn.Calls.Last(paths => paths.Length == CdnInvalidationService.MaxTagsPerInvalidation).Should().Equal(Tags().Take(CdnInvalidationService.MaxTagsPerInvalidation));
    }

    [UnitTest]
    public async Task InvalidateNow_OneBatchFails_RepeatsSuccessfulBatch()
    {
        var time = new ManualTimeProvider();
        var cdn = new MixedBatchInvalidator(CdnInvalidationOutcome.Failed);
        var service = Service(cdn, time);

        var result = await service.InvalidateNowAsync(Tags(), CancellationToken.None, CdnPurgeCause.Admin("admin-123"));
        result.Outcome.Should().Be(CdnInvalidationOutcome.Failed);
        await time.TimerIsWaiting();
        time.Advance(PurgeBackedDelivery.SettleDelay);

        await Eventually(() => cdn.Calls.Count == 3);
        cdn.Calls.Last().Should().Equal(Tags().Take(CdnInvalidationService.MaxTagsPerInvalidation));
    }

    private static string[] Tags() => Enumerable.Range(0, CdnInvalidationService.MaxTagsPerInvalidation + 1)
        .Select(index => $"#honua-scope:tiles/{index}").ToArray();

    private static CdnInvalidationService Service(ICdnInvalidator cdn, TimeProvider time)
        => new(cdn, NullLogger<CdnInvalidationService>.Instance, new CdnPurgeCoordination
        {
            TimeProvider = time,
            Options = new StaticOptions(Options()),
        });

    private sealed class MixedBatchInvalidator(CdnInvalidationOutcome unsent) : ICdnInvalidator
    {
        public ConcurrentQueue<string[]> Calls { get; } = new();

        public Task<CdnInvalidationResult> InvalidateAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue([.. paths]);
            var lastUnsentAttempt = unsent is CdnInvalidationOutcome.Busy ? 5 : 2;
            var outcome = Calls.Count is >= 2 && Calls.Count <= lastUnsentAttempt ? unsent : CdnInvalidationOutcome.Submitted;
            return Task.FromResult(new CdnInvalidationResult(outcome, "test"));
        }
    }
}
