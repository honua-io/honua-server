// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
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
        var sourceSub = Substitute.For<IRealtimeSubscription>();
        var fanout = new StubRealtimeFanoutProvider(sourceSub);

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

        var alert = new AlertEventEnvelope
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
        var payload = JsonSerializer.SerializeToElement(alert, StreamingOperationsJsonContext.Default.AlertEventEnvelope);
        fanout.CapturedWriter!.TryWrite(new RealtimeDelivery.Event(
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

    private sealed class StubRealtimeFanoutProvider(IRealtimeSubscription subscription) : IRealtimeFanoutProvider
    {
        public ChannelWriter<RealtimeDelivery>? CapturedWriter { get; private set; }

        public RealtimeProviderCapabilities Capabilities => new(
            CrossNode: false,
            OrderedPerFamily: false,
            Resumable: false);

        public ValueTask<RealtimePosition> AppendAsync(RealtimeEventDraft draft, DbTransaction? transaction, CancellationToken cancellationToken)
            => ValueTask.FromResult(new RealtimePosition(draft.Family, 0));

        public void Enqueue(RealtimeEventDraft draft)
        {
        }

        public IRealtimeSubscription Subscribe(RealtimeSubscriptionRequest request)
        {
            CapturedWriter = request.Writer;
            return subscription;
        }

        public ValueTask<RealtimeLogWindow> GetWindowAsync(RealtimeFamily family, CancellationToken cancellationToken)
            => ValueTask.FromResult(new RealtimeLogWindow(family, 0, 0));

        public ValueTask<IReadOnlyList<RealtimeEvent>> ReadPresencePageAsync(
            string scopeKey, long after, long through, CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<RealtimeEvent>>([]);

        public ValueTask<IRealtimeStreamLease?> TryAcquireMcpStreamAsync(string sessionId, CancellationToken cancellationToken)
            => ValueTask.FromResult<IRealtimeStreamLease?>(null);
    }
}
