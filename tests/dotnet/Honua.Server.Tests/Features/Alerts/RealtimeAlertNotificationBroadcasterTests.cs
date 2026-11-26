// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using Honua.Alerts;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Realtime;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Alerts;

public sealed class RealtimeAlertNotificationBroadcasterTests
{
    [UnitTest]
    public async Task Subscription_DisposeAsync_WaitsForInFlightHandlerCompletionBeforeReturning()
    {
        ChannelWriter<RealtimeDelivery>? capturedWriter = null;
        var fanout = CreateCapturingFanout(Substitute.For<IRealtimeSubscription>(), writer => capturedWriter = writer);

        await using var broadcaster = new RealtimeAlertNotificationBroadcaster(
            fanout,
            NullLogger<RealtimeAlertNotificationBroadcaster>.Instance);

        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowHandlerExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerCompleted = false;

        var subscription = await broadcaster.SubscribeAsync(
            async (_, _, _) =>
            {
                handlerEntered.TrySetResult();
                await allowHandlerExit.Task;
                Volatile.Write(ref handlerCompleted, true);
            });

        var payload = JsonSerializer.SerializeToElement(CreateSampleAlert(), StreamingOperationsJsonContext.Default.AlertEventEnvelope);
        capturedWriter!.TryWrite(new RealtimeDelivery.Event(
            new RealtimeEvent(RealtimeFamily.Alerts, 1, "svc-1", "alert", payload, DateTimeOffset.UtcNow)))
            .Should().BeTrue();

        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var disposeTask = subscription.DisposeAsync().AsTask();
        await Task.Delay(50);
        disposeTask.IsCompleted.Should().BeFalse("DisposeAsync must wait for the in-flight alert handler to finish");

        allowHandlerExit.TrySetResult();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
        Volatile.Read(ref handlerCompleted).Should().BeTrue();
    }

    private static IRealtimeFanoutProvider CreateCapturingFanout(
        IRealtimeSubscription subscription,
        Action<ChannelWriter<RealtimeDelivery>> onSubscribe)
    {
        var fanout = Substitute.For<IRealtimeFanoutProvider>();
        fanout.Capabilities.Returns(new RealtimeProviderCapabilities(CrossNode: false, OrderedPerFamily: false, Resumable: false));
#pragma warning disable CA2012 // NSubstitute setup for a ValueTask-returning member.
        fanout.GetWindowAsync(Arg.Any<RealtimeFamily>(), Arg.Any<CancellationToken>())
            .Returns(call => ValueTask.FromResult(new RealtimeLogWindow(call.Arg<RealtimeFamily>(), 0, 0)));
#pragma warning restore CA2012
        fanout.Subscribe(Arg.Any<RealtimeSubscriptionRequest>()).Returns(call =>
        {
            onSubscribe(call.Arg<RealtimeSubscriptionRequest>().Writer);
            return subscription;
        });
        return fanout;
    }

    private static AlertEventEnvelope CreateSampleAlert() => new()
    {
        DedupeKey = "dedupe-1",
        RuleId = 1,
        ServiceId = "svc-1",
        LayerId = 1,
        ObjectId = 10,
        TriggerType = AlertTriggerType.Enter,
        Generation = 1,
        Severity = AlertSeverity.Warning,
        OccurredAt = DateTimeOffset.UtcNow
    };
}
