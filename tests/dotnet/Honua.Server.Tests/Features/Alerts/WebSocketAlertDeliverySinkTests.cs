// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;
using Honua.Server.Features.Alerts;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Alerts;

public sealed class WebSocketAlertDeliverySinkTests
{
    [UnitTest]
    public async Task DeliverAsync_BroadcastsToSubscriber()
    {
        var broadcaster = new InMemoryAlertNotificationBroadcaster();
        var sink = new WebSocketAlertDeliverySink(broadcaster);

        AlertEventEnvelope? received = null;
        using var sub = broadcaster.Subscribe((evt, _) =>
        {
            received = evt;
            return Task.CompletedTask;
        });

        var alertEvent = AlertTestFixtures.CreateAlertEvent(dedupeKey: "evt-ws-1");
        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.WebSocket), alertEvent);

        Assert.True(result.Succeeded);
        Assert.NotNull(received);
        Assert.Equal(alertEvent.DedupeKey, received.DedupeKey);
    }

    [UnitTest]
    public async Task DeliverAsync_WithNoSubscribers_StillSucceeds()
    {
        var broadcaster = new InMemoryAlertNotificationBroadcaster();
        var sink = new WebSocketAlertDeliverySink(broadcaster);

        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.WebSocket),
            AlertTestFixtures.CreateAlertEvent());

        Assert.True(result.Succeeded);
    }

    [UnitTest]
    public async Task DeliverAsync_SubscriberFailure_DoesNotBlockOthers()
    {
        var broadcaster = new InMemoryAlertNotificationBroadcaster();
        var sink = new WebSocketAlertDeliverySink(broadcaster);

        AlertEventEnvelope? received = null;

        // First subscriber throws
        using var sub1 = broadcaster.Subscribe((_, _) => throw new InvalidOperationException("boom"));

        // Second subscriber should still receive the event
        using var sub2 = broadcaster.Subscribe((evt, _) =>
        {
            received = evt;
            return Task.CompletedTask;
        });

        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.WebSocket),
            AlertTestFixtures.CreateAlertEvent());

        Assert.True(result.Succeeded);
        Assert.NotNull(received);
    }

    [UnitTest]
    public async Task Subscribe_DisposedSubscription_NoLongerReceives()
    {
        var broadcaster = new InMemoryAlertNotificationBroadcaster();
        var sink = new WebSocketAlertDeliverySink(broadcaster);

        var callCount = 0;
        var sub = broadcaster.Subscribe((_, _) =>
        {
            callCount++;
            return Task.CompletedTask;
        });

        await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.WebSocket),
            AlertTestFixtures.CreateAlertEvent());
        Assert.Equal(1, callCount);

        sub.Dispose();

        await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.WebSocket),
            AlertTestFixtures.CreateAlertEvent());
        Assert.Equal(1, callCount); // Not incremented after dispose
    }

    [UnitTest]
    public void ChannelType_ReturnsWebSocket()
    {
        var broadcaster = new InMemoryAlertNotificationBroadcaster();
        var sink = new WebSocketAlertDeliverySink(broadcaster);
        Assert.Equal(AlertChannelType.WebSocket, sink.ChannelType);
    }
}
