// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using System.Data.Common;
using System.Text.Json;
using Honua.Alerts;
using Honua.Infrastructure.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Realtime;
using Honua.Server.Features.Realtime;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Tests.Infrastructure;
using Honua.Server.Tests.Realtime;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Alerts;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class RealtimeAlertDeliveryTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task BroadcastAsync_SubscriberOnAnotherTask_ReceivesEnvelopeAndSupportsDisconnect()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        var dispatcher = harness.CreateDispatcher();
        var consumer = new ObservedProvider(new PostgresRealtimeFanoutProvider(
            harness.Log, dispatcher, new RealtimePresenceBatcher(harness.Log)));
        await using var receiver = new RealtimeAlertNotificationBroadcaster(
            consumer, NullLogger<RealtimeAlertNotificationBroadcaster>.Instance);
        await using var sender = new RealtimeAlertNotificationBroadcaster(
            new PostgresRealtimeFanoutProvider(harness.Log, harness.CreateDispatcher(), new RealtimePresenceBatcher(harness.Log)),
            NullLogger<RealtimeAlertNotificationBroadcaster>.Instance);
        var received = new TaskCompletionSource<AlertEventEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = await receiver.SubscribeAsync((alert, _) =>
        {
            received.TrySetResult(alert);
            return Task.CompletedTask;
        }, new SubscriptionOptions("remote-task"));
        consumer.Request!.After.Should().NotBeNull("subscription initialization must preserve events committed before replay becomes live");
        await consumer.WaitUntilLiveAsync();
        var expected = AlertTestFixtures.CreateAlertEvent(dedupeKey: "remote-receiver");

        await sender.BroadcastAsync(expected);
        await dispatcher.DispatchOnceAsync(CancellationToken.None);

        (await received.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(expected);
        receiver.GetSubscriptions().Should().ContainSingle().Which.ClientLabel.Should().Be("remote-task");
        receiver.DisconnectSubscriber(subscription.SubscriberId).Should().BeTrue();
        subscription.DisconnectToken.IsCancellationRequested.Should().BeTrue();
        receiver.GetSubscriptions().Should().BeEmpty();
    }

    [IntegrationTest]
    public async Task Subscribe_RealtimeReset_DisconnectsInsteadOfSilentlyLosingAlerts()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        var consumer = new ObservedProvider(new PostgresRealtimeFanoutProvider(
            harness.Log, harness.CreateDispatcher(), new RealtimePresenceBatcher(harness.Log)));
        await using var receiver = new RealtimeAlertNotificationBroadcaster(
            consumer, NullLogger<RealtimeAlertNotificationBroadcaster>.Instance);
        using var subscription = await receiver.SubscribeAsync((_, _) => Task.CompletedTask);
        await consumer.WaitUntilLiveAsync();
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = subscription.DisconnectToken.Register(() => disconnected.TrySetResult());

        consumer.Request!.Writer.TryWrite(new RealtimeDelivery.Reset(RealtimeResetReasons.Retention, 42)).Should().BeTrue();

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        receiver.GetSubscriptions().Should().BeEmpty();
    }

    [IntegrationTest]
    public async Task Subscribe_SourceClosesDuringBlockedDelivery_CancelsSubscriberImmediately()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        var consumer = new ObservedProvider(new PostgresRealtimeFanoutProvider(
            harness.Log, harness.CreateDispatcher(), new RealtimePresenceBatcher(harness.Log)));
        await using var receiver = new RealtimeAlertNotificationBroadcaster(
            consumer, NullLogger<RealtimeAlertNotificationBroadcaster>.Instance);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = await receiver.SubscribeAsync(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        });
        await consumer.WaitUntilLiveAsync();
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = subscription.DisconnectToken.Register(() => disconnected.TrySetResult());
        var payload = JsonSerializer.SerializeToElement(
            AlertTestFixtures.CreateAlertEvent(), StreamingOperationsJsonContext.Default.AlertEventEnvelope);
        consumer.Request!.Writer.TryWrite(new RealtimeDelivery.Event(
            new RealtimeEvent(RealtimeFamily.Alerts, 1, "service", "alert", payload, DateTimeOffset.UtcNow))).Should().BeTrue();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            consumer.Request.Writer.TryWrite(new RealtimeDelivery.Event(
                new RealtimeEvent(RealtimeFamily.Alerts, 2, "service", "alert", payload, DateTimeOffset.UtcNow))).Should().BeTrue();
            consumer.Request.Writer.TryComplete(new InvalidOperationException("overflow")).Should().BeTrue();
            await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [IntegrationTest]
    public async Task DeliverAsync_OnOneTask_PersistsEnvelopeForAnotherTask()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        var producer = new PostgresRealtimeFanoutProvider(
            harness.Log, harness.CreateDispatcher(), new RealtimePresenceBatcher(harness.Log));
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IRealtimeFanoutProvider>(producer)
            .AddSingleton<IAlertNotificationBroadcaster, RealtimeAlertNotificationBroadcaster>()
            .BuildServiceProvider();
        var sink = ActivatorUtilities.CreateInstance<WebSocketAlertDeliverySink>(services);
        var alert = AlertTestFixtures.CreateAlertEvent(dedupeKey: "cross-task-alert");

        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.WebSocket), alert);

        result.Succeeded.Should().BeTrue();
        var events = await harness.Log.ReadAfterAsync(new RealtimeLogRead("alerts", 0, 10), CancellationToken.None);
        var persisted = events.Should().ContainSingle().Subject;
        persisted.ScopeKey.Should().Be(alert.ServiceId);
        persisted.Kind.Should().Be("alert");
        persisted.Payload.GetProperty("dedupeKey").GetString().Should().Be(alert.DedupeKey);
        persisted.Payload.GetProperty("serviceId").GetString().Should().Be(alert.ServiceId);
        persisted.Payload.GetProperty("layerId").GetInt32().Should().Be(alert.LayerId);
    }

    private sealed class ObservedProvider(IRealtimeFanoutProvider inner) : IRealtimeFanoutProvider
    {
        private RealtimeSubscription? _subscription;
        public RealtimeSubscriptionRequest? Request { get; private set; }
        public RealtimeProviderCapabilities Capabilities => inner.Capabilities;

        public ValueTask<RealtimePosition> AppendAsync(RealtimeEventDraft draft, DbTransaction? transaction, CancellationToken cancellationToken)
            => inner.AppendAsync(draft, transaction, cancellationToken);

        public void Enqueue(RealtimeEventDraft draft) => inner.Enqueue(draft);

        public IRealtimeSubscription Subscribe(RealtimeSubscriptionRequest request)
        {
            Request = request;
            _subscription = (RealtimeSubscription)inner.Subscribe(request);
            return _subscription;
        }

        public ValueTask<RealtimeLogWindow> GetWindowAsync(RealtimeFamily family, CancellationToken cancellationToken)
            => inner.GetWindowAsync(family, cancellationToken);

        public async Task WaitUntilLiveAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (_subscription?.IsLive != true)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
            }
        }
    }
}
