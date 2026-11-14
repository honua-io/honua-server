// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Threading.Channels;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Health;
using Honua.Core.Features.Realtime;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Server.Features.Realtime;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Honua.Server.Tests.Realtime;

public sealed partial class RealtimeFanoutProviderTests
{
    [IntegrationTest]
    public async Task PostgresProvider_EventAppendedOnTaskA_ReachesSubscriberOnTaskBThroughTheNotification()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        // A safety poll far beyond the timeout: only the notification can deliver in time.
        var options = new RealtimeOptions { SafetyPollInterval = TimeSpan.FromMinutes(5) };
        await using var listenerB = harness.CreateListener(options);
        var dispatcherB = harness.CreateDispatcher(options);
        using var tailB = harness.CreateTail(dispatcherB, listenerB, options);
        var taskA = CreateProvider(harness);
        await tailB.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(() => listenerB.IsConnected, "the listener connects");
            var channel = Channel.CreateBounded<RealtimeDelivery>(64);
            await using var subscription = (RealtimeSubscription)dispatcherB.Subscribe(
                new RealtimeSubscriptionRequest(RealtimeFamily.CollabOps, ["map-1"], null, channel.Writer));
            await WaitUntilLiveAsync(subscription);

            var stopwatch = Stopwatch.StartNew();
            var position = await taskA.AppendAsync(
                new RealtimeEventDraft(RealtimeFamily.CollabOps, "map-1", "operation", RealtimeLogHarness.Payload(new { op = 1 })),
                null,
                CancellationToken.None);

            var received = (await ReadEventsAsync(channel.Reader, 1)).Single();
            received.Position.Should().Be(position);
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
            taskA.Capabilities.Should().Be(new RealtimeProviderCapabilities(CrossNode: true, OrderedPerFamily: true, Resumable: true));
        }
        finally
        {
            await tailB.StopAsync(CancellationToken.None);
        }
    }

    [IntegrationTest]
    public async Task Listener_KilledWithPgTerminateBackend_ReconnectsAndNothingIsLost()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        var options = new RealtimeOptions
        {
            SafetyPollInterval = TimeSpan.FromMinutes(5),
            DegradedPollInterval = TimeSpan.FromMilliseconds(200),
            ListenerReconnectMinDelay = TimeSpan.FromMilliseconds(200),
            ListenerReconnectMaxDelay = TimeSpan.FromMilliseconds(400)
        };
        await using var listener = harness.CreateListener(options);
        var dispatcher = harness.CreateDispatcher(options);
        using var tail = harness.CreateTail(dispatcher, listener, options);
        await tail.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(() => listener.IsConnected, "the listener connects");
            var channel = Channel.CreateBounded<RealtimeDelivery>(256);
            await using var subscription = (RealtimeSubscription)dispatcher.Subscribe(
                new RealtimeSubscriptionRequest(RealtimeFamily.Observations, [], null, channel.Writer));
            await WaitUntilLiveAsync(subscription);

            await AppendAsync(harness, RealtimeLogFamilies.Observations, "ds-1", 5);
            await harness.ExecuteAsync($"SELECT pg_terminate_backend({listener.ProcessId})");
            await AppendAsync(harness, RealtimeLogFamilies.Observations, "ds-1", 5);
            await WaitForAsync(() => listener.Reconnects >= 1 && listener.IsConnected, "the listener reconnects");
            await AppendAsync(harness, RealtimeLogFamilies.Observations, "ds-1", 5);

            var received = await ReadEventsAsync(channel.Reader, 15);
            received.Select(delivered => delivered.Sequence).Should().Equal(Enumerable.Range(1, 15).Select(seq => (long)seq));
        }
        finally
        {
            await tail.StopAsync(CancellationToken.None);
        }
    }

    [IntegrationTest]
    public async Task LostNotification_IsCaughtBySafetyPoll()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        var options = new RealtimeOptions { SafetyPollInterval = TimeSpan.FromMilliseconds(300) };
        await using var listener = harness.CreateListener(options);
        var dispatcher = harness.CreateDispatcher(options);
        using var tail = harness.CreateTail(dispatcher, listener, options);
        await tail.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(() => listener.IsConnected, "the listener connects");
            var channel = Channel.CreateBounded<RealtimeDelivery>(64);
            await using var subscription = (RealtimeSubscription)dispatcher.Subscribe(
                new RealtimeSubscriptionRequest(RealtimeFamily.Admin, [], null, channel.Writer));
            await WaitUntilLiveAsync(subscription);

            // Written without realtime_append, so no notification is sent.
            await harness.ExecuteAsync(
                $"""
                WITH h AS (UPDATE {harness.Table("realtime_log_heads")} SET head = head + 1 WHERE family = 'admin' RETURNING head)
                INSERT INTO {harness.Table("realtime_events")} (family, seq, scope_key, kind, payload, expires_at)
                SELECT 'admin', h.head, 'proposals', 'silent', jsonb_build_object(), now() + interval '1 hour' FROM h
                """);

            (await ReadEventsAsync(channel.Reader, 1)).Single().Kind.Should().Be("silent");
        }
        finally
        {
            await tail.StopAsync(CancellationToken.None);
        }
    }

    [IntegrationTest]
    public async Task InProcessProvider_AppendWakesItsOwnDispatcher()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        var options = new RealtimeOptions { LocalPollInterval = TimeSpan.FromMinutes(5) };
        var dispatcher = harness.CreateDispatcher(options);
        using var tail = harness.CreateTail(dispatcher, null, options);
        var provider = new InProcessRealtimeFanoutProvider(harness.Log, dispatcher, new RealtimePresenceBatcher(harness.Log));
        await tail.StartAsync(CancellationToken.None);
        try
        {
            var channel = Channel.CreateBounded<RealtimeDelivery>(64);
            await using var subscription = (RealtimeSubscription)provider.Subscribe(
                new RealtimeSubscriptionRequest(RealtimeFamily.Catalog, [], null, channel.Writer));
            await WaitUntilLiveAsync(subscription);

            await provider.AppendAsync(
                new RealtimeEventDraft(RealtimeFamily.Catalog, "default", "etag-changed", RealtimeLogHarness.Payload(new { etag = "b" })),
                null,
                CancellationToken.None);

            (await ReadEventsAsync(channel.Reader, 1)).Single().Kind.Should().Be("etag-changed");
            provider.Capabilities.CrossNode.Should().BeFalse();
        }
        finally
        {
            await tail.StopAsync(CancellationToken.None);
        }
    }

    [IntegrationTest]
    public async Task Enqueue_KeepsTheLatestPerCoalesceKeyAndAppendsOneBatchPerFamily()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        var presence = new RealtimePresenceBatcher(harness.Log);
        for (var x = 0; x < 3; x++)
        {
            presence.Enqueue(new RealtimeEventDraft(RealtimeFamily.CollabPresence, "map-1", "moved", RealtimeLogHarness.Payload(new { x }), CoalesceKey: "p-1"));
        }

        presence.Enqueue(new RealtimeEventDraft(RealtimeFamily.CollabPresence, "map-1", "joined", RealtimeLogHarness.Payload(new { x = 9 }), CoalesceKey: "p-2"));

        (await presence.FlushAsync(CancellationToken.None)).Should().Be(2);
        (await presence.FlushAsync(CancellationToken.None)).Should().Be(0);
        (await harness.ScalarAsync<string>(
                $"SELECT string_agg(payload->>'x', ',' ORDER BY seq) FROM {harness.Table("realtime_events")} WHERE family = 'collab-presence'"))
            .Should().Be("2,9");
    }

}
