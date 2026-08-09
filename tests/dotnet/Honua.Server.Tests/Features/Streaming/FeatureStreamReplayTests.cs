// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Events;
using Honua.Server.Features.Streaming;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Streaming;

/// <summary>
/// Unit tests for durable feature-stream replay continuity.
/// </summary>
public sealed class FeatureStreamReplayTests
{
    [UnitTest]
    public void HasReplayWindowGap_ContiguousAndSkippedCursors_DistinguishesWindowLoss()
    {
        Assert.False(FeatureStreamEndpoints.HasReplayWindowGap(41, 42));
        Assert.True(FeatureStreamEndpoints.HasReplayWindowGap(41, 43));
    }

    [UnitTest]
    public void TryFindReplayWindowGap_InteriorGap_ReturnsMissingBoundary()
    {
        IReadOnlyList<FeatureChangeEvent> events =
        [
            CreateEvent(42),
            CreateEvent(44)
        ];

        var found = FeatureStreamEndpoints.TryFindReplayWindowGap(
            events,
            requestedCursor: 41,
            out var previousCursor,
            out var firstAvailableCursor);

        Assert.True(found);
        Assert.Equal(42, previousCursor);
        Assert.Equal(44, firstAvailableCursor);
    }

    [UnitTest]
    public async Task ValidateReplayTailAsync_AdvancedDurableHead_ReturnsRetryBoundary()
    {
        var store = new FixedRetentionWindowEventStore(
            FeatureChangeRetentionWindow.Retained(currentCursor: 43, oldestRetainedCursor: 1));

        var requiredThroughCursor = await FeatureStreamEndpoints.ValidateReplayTailAsync(
            store,
            replayCursor: 42,
            requiredThroughCursor: 42,
            NullLogger.Instance,
            Guid.NewGuid(),
            subscriptionId: "test",
            CancellationToken.None);

        Assert.Equal(43, requiredThroughCursor);
    }

    [UnitTest]
    public async Task ValidateReplayTailAsync_RetryStillBelowObservedHead_FailsClosed()
    {
        var store = new FixedRetentionWindowEventStore(
            FeatureChangeRetentionWindow.Retained(currentCursor: 43, oldestRetainedCursor: 1));

        await Assert.ThrowsAsync<FeatureStreamEndpoints.FeatureStreamReplayWindowGapException>(
            () => FeatureStreamEndpoints.ValidateReplayTailAsync(
                store,
                replayCursor: 42,
                requiredThroughCursor: 43,
                NullLogger.Instance,
                Guid.NewGuid(),
                subscriptionId: "test",
                CancellationToken.None));
    }

    private static FeatureChangeEvent CreateEvent(long cursor)
        => new()
        {
            EventId = $"event-{cursor}",
            Cursor = cursor,
            Timestamp = DateTimeOffset.UtcNow,
            SourceId = "test",
            ServiceId = "test",
            LayerId = 0,
            ObjectId = cursor,
            Operation = "update",
            Protocol = "test",
            RequestId = $"request-{cursor}"
        };

    private sealed class FixedRetentionWindowEventStore(FeatureChangeRetentionWindow window)
        : IFeatureChangeEventStore
    {
        public Task<FeatureChangeEvent> AppendAsync(
            FeatureChangeEventRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<long> GetCurrentCursorAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(window.CurrentCursor);

        public Task<FeatureChangeRetentionWindow> GetRetentionWindowAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(window);

        public Task<IReadOnlyList<FeatureChangeEvent>> QueryAsync(
            long? cursor,
            DateTimeOffset? from,
            DateTimeOffset? to,
            int limit,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FeatureChangeEvent>>([]);
    }
}
