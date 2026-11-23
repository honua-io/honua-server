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

    [UnitTest]
    public async Task Replay_PendingMaskRefresh_PrecedesDelivery()
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var security = CreateSecurityWithPendingRefresh(denied: null);

        var replayed = await FeatureStreamReplayHarness.ReplayAsync(
            new(context.Response, FeatureStreamReplayHarness.CreateStoreWithOneChange("{\"secret\":\"private\"}"),
                new StreamSubscriptionFilter(subscriberSecurity: security)), CancellationToken.None);

        Assert.Equal(1, replayed);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();
        Assert.Contains("feature-change", body);
        Assert.DoesNotContain("private", body);
    }

    [UnitTest]
    public async Task Replay_PendingRevocation_DeliversNothing()
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var security = CreateSecurityWithPendingRefresh(denied: new HashSet<(string, int)> { ("test", 0) });

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => FeatureStreamReplayHarness.ReplayAsync(
            new(context.Response, FeatureStreamReplayHarness.CreateStoreWithOneChange("{\"secret\":\"private\"}"),
                new StreamSubscriptionFilter(subscriberSecurity: security)), CancellationToken.None));

        Assert.Equal(0, context.Response.Body.Length);
    }

    // The admitted policy is unmasked; the refresh replay must run first masks "secret" or denies the route.
    private static StreamSubscriberSecurity CreateSecurityWithPendingRefresh(IReadOnlySet<(string Service, int Layer)>? denied)
    {
        var security = new StreamSubscriberSecurity(["test"],
            new Dictionary<(string, int), StreamLayerReadPolicy> { [("test", 0)] = new([], []) },
            new Dictionary<(string, int), StreamLayerReadPolicy>());
        security.EnableEventDrivenRefresh();
        security.EnableRefresh(_ => Task.FromResult(new StreamSubscriberSecurity(["test"],
            new Dictionary<(string, int), StreamLayerReadPolicy>
            {
                [("test", 0)] = new([], new HashSet<string>(["secret"], StringComparer.OrdinalIgnoreCase))
            }, new Dictionary<(string, int), StreamLayerReadPolicy>(), denied)));
        return security;
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
