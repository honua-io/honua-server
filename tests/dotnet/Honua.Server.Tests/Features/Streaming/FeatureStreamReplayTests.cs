// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Events;
using Honua.Server.Features.Streaming;
using Honua.TestKit.Attributes;

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
}
