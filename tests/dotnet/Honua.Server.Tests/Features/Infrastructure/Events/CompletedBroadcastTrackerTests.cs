// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Events;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Infrastructure.Events;

[Protocol(TestProtocols.TestQuality)]
public sealed class CompletedBroadcastTrackerTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void RemoveExpired_AfterIdleRetention_ReleasesOldIdsAndPreservesRecentDeduplication()
    {
        var clock = new ManualTimeProvider();
        var tracker = new CompletedBroadcastTracker(TimeSpan.FromHours(24), clock);
        for (var index = 0; index < 10000; index++)
        {
            tracker.MarkCompleted($"old-{index}");
        }

        clock.Advance(TimeSpan.FromHours(23));
        tracker.MarkCompleted("recent");
        tracker.RemoveExpired();
        Assert.Equal(10001, tracker.Count);

        clock.Advance(TimeSpan.FromHours(1));
        tracker.RemoveExpired();

        Assert.Equal(1, tracker.Count);
        Assert.True(tracker.IsCompleted("recent"));
        Assert.False(tracker.IsCompleted("old-0"));

        clock.Advance(TimeSpan.FromHours(23));
        tracker.RemoveExpired();
        Assert.Equal(0, tracker.Count);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void IsCompleted_AtExpiry_RemovesIdBeforeNextSweep()
    {
        var clock = new ManualTimeProvider();
        var tracker = new CompletedBroadcastTracker(TimeSpan.FromHours(24), clock);
        tracker.MarkCompleted("event");
        Assert.True(tracker.IsCompleted("event"));

        clock.Advance(TimeSpan.FromHours(24));

        Assert.False(tracker.IsCompleted("event"));
        Assert.Equal(0, tracker.Count);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void MarkCompleted_ExistingId_RenewsRetention()
    {
        var clock = new ManualTimeProvider();
        var tracker = new CompletedBroadcastTracker(TimeSpan.FromHours(24), clock);
        tracker.MarkCompleted("event");
        clock.Advance(TimeSpan.FromHours(23));
        tracker.MarkCompleted("event");
        clock.Advance(TimeSpan.FromHours(1));
        tracker.RemoveExpired();

        Assert.True(tracker.IsCompleted("event"));
        Assert.Equal(1, tracker.Count);

        tracker.Clear();
        Assert.Equal(0, tracker.Count);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
