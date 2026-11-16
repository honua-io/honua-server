// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Events;
using Honua.Server.Features.Streaming;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Streaming;

public sealed class FeatureStreamReplayTests
{
    [UnitTest]
    public async Task EnsureReplayableAfterAsync_CursorBeforeOldestRetained_ThrowsReplayWindowGap()
    {
        var store = FakeFeatureChangeEventStore.WithRetentionWindow(
            FeatureChangeRetentionWindow.Retained(currentCursor: 60, oldestRetainedCursor: 50));

        await Assert.ThrowsAsync<FeatureStreamEndpoints.FeatureStreamReplayWindowGapException>(
            () => EnsureReplayableAfterAsync(store, replayCursor: 41));
    }

    [UnitTest]
    public async Task EnsureReplayableAfterAsync_CursorJustBeforeOldestRetained_Returns()
    {
        var store = FakeFeatureChangeEventStore.WithRetentionWindow(
            FeatureChangeRetentionWindow.Retained(currentCursor: 60, oldestRetainedCursor: 50));

        await EnsureReplayableAfterAsync(store, replayCursor: 49);
    }

    [UnitTest]
    public async Task EnsureReplayableAfterAsync_GenerationGapsInsideTheWindow_Returns()
    {
        var store = new FakeFeatureChangeEventStore();
        store.Append(CreateRequest(1), cursorGap: 3);
        store.Append(CreateRequest(2), cursorGap: 5);

        await EnsureReplayableAfterAsync(store, replayCursor: 0);
        await EnsureReplayableAfterAsync(store, replayCursor: 5);
    }

    [UnitTest]
    public async Task EnsureReplayableAfterAsync_CursorAtHead_Returns()
    {
        var store = FakeFeatureChangeEventStore.WithRetentionWindow(
            FeatureChangeRetentionWindow.Retained(currentCursor: 60, oldestRetainedCursor: 50));

        await EnsureReplayableAfterAsync(store, replayCursor: 60);
    }

    [UnitTest]
    public async Task EnsureReplayableAfterAsync_IndeterminateWindow_ThrowsReplayWindowGap()
    {
        var store = FakeFeatureChangeEventStore.WithRetentionWindow(
            FeatureChangeRetentionWindow.Indeterminate(currentCursor: 60));

        await Assert.ThrowsAsync<FeatureStreamEndpoints.FeatureStreamReplayWindowGapException>(
            () => EnsureReplayableAfterAsync(store, replayCursor: 60));
    }

    [UnitTest]
    public async Task EnsureReplayableAfterAsync_EmptyLogBehindHead_ThrowsReplayWindowGap()
    {
        var store = FakeFeatureChangeEventStore.WithRetentionWindow(
            FeatureChangeRetentionWindow.KnownEmpty(currentCursor: 60));

        await Assert.ThrowsAsync<FeatureStreamEndpoints.FeatureStreamReplayWindowGapException>(
            () => EnsureReplayableAfterAsync(store, replayCursor: 10));
    }

    private static Task EnsureReplayableAfterAsync(IFeatureChangeEventStore store, long replayCursor)
        => FeatureStreamEndpoints.EnsureReplayableAfterAsync(
            new FeatureStreamEndpoints.FeatureStreamReplayContext(store, NullLogger.Instance, Guid.NewGuid(), "test"),
            replayCursor,
            CancellationToken.None);

    private static FeatureChangeEventRequest CreateRequest(long objectId)
        => new()
        {
            ServiceId = "test",
            LayerId = 0,
            ObjectId = objectId,
            Operation = "update",
            Protocol = "test",
            RequestId = $"request-{objectId}"
        };
}
