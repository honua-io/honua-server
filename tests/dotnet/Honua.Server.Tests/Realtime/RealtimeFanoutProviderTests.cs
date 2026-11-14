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

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed partial class RealtimeFanoutProviderTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    private static readonly TimeSpan _deliveryTimeout = TimeSpan.FromSeconds(15);

    [IntegrationTest]
    public async Task McpStream_TwoTasksOverPostgres_OneOwnerUntilDisconnect()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        var nodeA = new InProcessRealtimeFanoutProvider(harness.Log, harness.CreateDispatcher(),
            new RealtimePresenceBatcher(harness.Log), harness.CreateLeaseStore());
        var nodeB = new PostgresRealtimeFanoutProvider(harness.Log, harness.CreateDispatcher(),
            new RealtimePresenceBatcher(harness.Log), harness.CreateLeaseStore());
        await using var first = await nodeA.TryAcquireMcpStreamAsync("session-a", CancellationToken.None);
        first.Should().NotBeNull();
        first!.IsHeld.Should().BeTrue();
        (await nodeB.TryAcquireMcpStreamAsync("session-a", CancellationToken.None)).Should().BeNull();
        await using var otherSession = await nodeB.TryAcquireMcpStreamAsync("session-b", CancellationToken.None);
        otherSession.Should().NotBeNull();

        await first.DisposeAsync();
        await using var successor = await nodeB.TryAcquireMcpStreamAsync("session-a", CancellationToken.None);
        successor.Should().NotBeNull();
        await first.DisposeAsync();
        (await nodeA.TryAcquireMcpStreamAsync("session-a", CancellationToken.None)).Should().BeNull();
    }

    [IntegrationTest]
    public async Task PresenceSnapshot_PagesBeyondResumeLimit_StaysScopedAndStopsAtCapturedHead()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        var dispatcher = harness.CreateDispatcher(new RealtimeOptions { ResumeMaxEvents = 2 });
        var provider = new InProcessRealtimeFanoutProvider(harness.Log, dispatcher, new RealtimePresenceBatcher(harness.Log));
        await AppendAsync(harness, RealtimeLogFamilies.CollabPresence, "other-map", 3);
        await AppendAsync(harness, RealtimeLogFamilies.CollabPresence, "map-1", 600);
        var window = await provider.GetWindowAsync(RealtimeFamily.CollabPresence, CancellationToken.None);
        await AppendAsync(harness, RealtimeLogFamilies.CollabPresence, "map-1", 2);
        var cursor = window.Oldest - 1;
        var events = new List<RealtimeEvent>();
        while (true)
        {
            var page = await provider.ReadPresencePageAsync("map-1", cursor, window.Head, CancellationToken.None);
            page.Count.Should().BeLessThanOrEqualTo(256);
            if (page.Count == 0)
            {
                break;
            }

            events.AddRange(page);
            cursor = page[^1].Sequence;
        }

        events.Select(item => item.Sequence).Should().Equal(Enumerable.Range(4, 600).Select(value => (long)value));
        events.Should().OnlyContain(item => item.ScopeKey == "map-1");
    }

    [IntegrationTest]
    public async Task PresenceSnapshot_SweptRowsDoNotResetOrRestartPaging()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        var provider = new InProcessRealtimeFanoutProvider(harness.Log, harness.CreateDispatcher(), new RealtimePresenceBatcher(harness.Log));
        await AppendAsync(harness, RealtimeLogFamilies.CollabPresence, "map-1", 300);
        var first = await provider.ReadPresencePageAsync("map-1", 0, 300, CancellationToken.None);
        await harness.ExecuteAsync($"DELETE FROM {harness.Table("realtime_events")} WHERE seq <= 280");

        var next = await provider.ReadPresencePageAsync("map-1", first[^1].Sequence, 300, CancellationToken.None);

        next.Select(item => item.Sequence).Should().Equal(Enumerable.Range(281, 20).Select(value => (long)value));
    }

    [IntegrationTest]
    public async Task Subscribe_LiveOnly_ReceivesOnlyEventsAppendedAfterItWentLive()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        var dispatcher = harness.CreateDispatcher();
        await AppendAsync(harness, RealtimeLogFamilies.Admin, "proposals", 2);
        var channel = Channel.CreateBounded<RealtimeDelivery>(64);

        await using var subscription = (RealtimeSubscription)dispatcher.Subscribe(
            new RealtimeSubscriptionRequest(RealtimeFamily.Admin, [], null, channel.Writer));
        await WaitUntilLiveAsync(subscription);
        await AppendAsync(harness, RealtimeLogFamilies.Admin, "proposals", 3);
        await dispatcher.DispatchOnceAsync(CancellationToken.None);

        var received = await ReadEventsAsync(channel.Reader, 3);
        received.Select(delivered => delivered.Sequence).Should().Equal(3L, 4L, 5L);
        channel.Reader.TryRead(out _).Should().BeFalse();
    }

    [IntegrationTest]
    public async Task Subscribe_AfterPosition_ReplaysThenFollowsInOrderWithoutGapsOrDuplicates()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        // Small pages and a tiny hand-off buffer, so replay and live delivery overlap and overflow.
        var options = new RealtimeOptions { ReplayBatchSize = 7, ReadBatchSize = 5, ReplayHandoffCapacity = 3 };
        var dispatcher = harness.CreateDispatcher(options);
        await dispatcher.InitializeAsync(CancellationToken.None);
        for (var i = 0; i < 30; i++)
        {
            await AppendAsync(harness, RealtimeLogFamilies.Jobs, i % 2 == 0 ? "job-a" : "job-b", 1);
        }

        var channel = Channel.CreateBounded<RealtimeDelivery>(1_000);
        await using var subscription = dispatcher.Subscribe(
            new RealtimeSubscriptionRequest(RealtimeFamily.Jobs, ["job-a"], After: 5, channel.Writer));
        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < 30; i++)
            {
                await AppendAsync(harness, RealtimeLogFamilies.Jobs, i % 2 == 0 ? "job-a" : "job-b", 1);
                await dispatcher.DispatchOnceAsync(CancellationToken.None);
            }
        });
        await writer;
        await dispatcher.DispatchOnceAsync(CancellationToken.None);

        // job-a holds the odd sequences; after 5 that is 7, 9, ..., 59.
        var expected = Enumerable.Range(0, 60).Select(i => (long)i + 1).Where(seq => seq > 5 && seq % 2 == 1).ToArray();
        var received = await ReadEventsAsync(channel.Reader, expected.Length);
        received.Select(delivered => delivered.Sequence).Should().Equal(expected);
        received.Should().OnlyContain(delivered => delivered.ScopeKey == "job-a");
    }

    [IntegrationTest]
    public async Task Subscribe_AfterAPositionNoLongerRetained_GetsResetRetentionThenFollowsLive()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        var dispatcher = harness.CreateDispatcher();
        await AppendAsync(harness, RealtimeLogFamilies.Alerts, "svc-1", 5);
        await harness.ExecuteAsync($"DELETE FROM {harness.Table("realtime_events")} WHERE seq <= 3");
        var channel = Channel.CreateBounded<RealtimeDelivery>(64);

        await using var subscription = (RealtimeSubscription)dispatcher.Subscribe(
            new RealtimeSubscriptionRequest(RealtimeFamily.Alerts, [], After: 1, channel.Writer));

        var first = await ReadOneAsync(channel.Reader);
        first.Should().Be(new RealtimeDelivery.Reset(RealtimeResetReasons.Retention, 5));
        await WaitUntilLiveAsync(subscription);
        await AppendAsync(harness, RealtimeLogFamilies.Alerts, "svc-1", 1);
        await dispatcher.DispatchOnceAsync(CancellationToken.None);
        (await ReadEventsAsync(channel.Reader, 1)).Single().Sequence.Should().Be(6);
    }

    [IntegrationTest]
    public async Task Dispatch_RowsSweptBeforeThisTaskReadThem_SendsResetRetentionAndResumesAfterTheHead()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        var dispatcher = harness.CreateDispatcher();
        await dispatcher.InitializeAsync(CancellationToken.None);
        var channel = Channel.CreateBounded<RealtimeDelivery>(64);
        await using var subscription = (RealtimeSubscription)dispatcher.Subscribe(
            new RealtimeSubscriptionRequest(RealtimeFamily.CollabPresence, [], null, channel.Writer));
        await WaitUntilLiveAsync(subscription);
        await AppendAsync(harness, RealtimeLogFamilies.CollabPresence, "map-1", 5);
        await harness.ExecuteAsync($"DELETE FROM {harness.Table("realtime_events")} WHERE seq <= 3");

        await dispatcher.DispatchOnceAsync(CancellationToken.None);

        (await ReadOneAsync(channel.Reader)).Should().Be(new RealtimeDelivery.Reset(RealtimeResetReasons.Retention, 5));
        channel.Reader.TryRead(out _).Should().BeFalse("the client resynchronizes to the head, so 4 and 5 are not sent again");
        await AppendAsync(harness, RealtimeLogFamilies.CollabPresence, "map-1", 1);
        await dispatcher.DispatchOnceAsync(CancellationToken.None);
        (await ReadEventsAsync(channel.Reader, 1)).Single().Sequence.Should().Be(6);
    }

    [IntegrationTest]
    public async Task Dispatch_AppendAndSweepAfterHeadProbe_ResetsToTheCurrentHead()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        var dispatcher = harness.CreateDispatcher();
        await dispatcher.InitializeAsync(CancellationToken.None);
        var channel = Channel.CreateBounded<RealtimeDelivery>(64);
        await using var subscription = (RealtimeSubscription)dispatcher.Subscribe(
            new RealtimeSubscriptionRequest(RealtimeFamily.Admin, [], null, channel.Writer));
        await WaitUntilLiveAsync(subscription);
        await AppendAsync(harness, RealtimeLogFamilies.Admin, "proposals", 1);

        await using var connection = await harness.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(
            $"LOCK TABLE {harness.Table("realtime_events")} IN ACCESS EXCLUSIVE MODE", connection, transaction);
        await command.ExecuteNonQueryAsync();
        using var timeout = new CancellationTokenSource(_deliveryTimeout);
        var dispatch = dispatcher.DispatchOnceAsync(timeout.Token);
        await WaitForBlockedReaderAsync(harness, connection.ProcessID);
        await harness.Log.AppendAsync(RealtimeLogFamilies.Admin,
            Enumerable.Range(0, 4).Select(n => new RealtimeLogRow("proposals", "test", RealtimeLogHarness.Payload(new { n }))).ToArray(),
            transaction, timeout.Token);
        command.CommandText = $"DELETE FROM {harness.Table("realtime_events")} WHERE seq <= 3";
        await command.ExecuteNonQueryAsync(timeout.Token);
        await transaction.CommitSafelyAsync(timeout.Token);
        await dispatch;

        (await ReadOneAsync(channel.Reader)).Should().Be(new RealtimeDelivery.Reset(RealtimeResetReasons.Retention, 5));
        channel.Reader.TryRead(out _).Should().BeFalse();
        await AppendAsync(harness, RealtimeLogFamilies.Admin, "proposals", 1);
        await dispatcher.DispatchOnceAsync(timeout.Token);
        (await ReadEventsAsync(channel.Reader, 1)).Single().Sequence.Should().Be(6);
    }

    private static async Task WaitForBlockedReaderAsync(RealtimeLogHarness harness, int blocker)
    {
        using var timeout = new CancellationTokenSource(_deliveryTimeout);
        while (await harness.ScalarAsync<bool>(
            $"SELECT EXISTS (SELECT FROM pg_stat_activity WHERE {blocker} = ANY(pg_blocking_pids(pid)))") is not true)
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    [IntegrationTest]
    public async Task Dispatch_GapBelowASubscribersPosition_SendsItNoReset()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        var dispatcher = harness.CreateDispatcher();
        await dispatcher.InitializeAsync(CancellationToken.None);
        await AppendAsync(harness, RealtimeLogFamilies.Admin, "proposals", 5);
        await harness.ExecuteAsync($"DELETE FROM {harness.Table("realtime_events")} WHERE seq <= 3");
        // Joins while dispatch is behind: its replay reads straight to the head.
        var channel = Channel.CreateBounded<RealtimeDelivery>(64);
        await using var subscription = (RealtimeSubscription)dispatcher.Subscribe(
            new RealtimeSubscriptionRequest(RealtimeFamily.Admin, [], null, channel.Writer));
        await WaitUntilLiveAsync(subscription);

        await dispatcher.DispatchOnceAsync(CancellationToken.None);

        channel.Reader.TryRead(out _).Should().BeFalse();
    }

    [IntegrationTest]
    public async Task Initialize_WithASubscriptionThatHasNoPositionYet_StartsAtTheHead()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        await AppendAsync(harness, RealtimeLogFamilies.Jobs, "job-1", 3);
        var dispatcher = harness.CreateDispatcher();
        var channel = Channel.CreateBounded<RealtimeDelivery>(64);
        await using var subscription = (RealtimeSubscription)dispatcher.Subscribe(
            new RealtimeSubscriptionRequest(RealtimeFamily.Jobs, [], null, channel.Writer));
        await WaitUntilLiveAsync(subscription);
        await AppendAsync(harness, RealtimeLogFamilies.Jobs, "job-1", 1);

        await dispatcher.DispatchOnceAsync(CancellationToken.None);

        (await ReadEventsAsync(channel.Reader, 1)).Single().Sequence.Should().Be(4);
        channel.Reader.TryRead(out _).Should().BeFalse();
    }

    [IntegrationTest]
    public async Task Subscribe_TooFarBehind_GetsResetTooFarBehind()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        var dispatcher = harness.CreateDispatcher(new RealtimeOptions { ResumeMaxEvents = 2 });
        await AppendAsync(harness, RealtimeLogFamilies.Mcp, "*", 5);
        var channel = Channel.CreateBounded<RealtimeDelivery>(64);

        await using var subscription = dispatcher.Subscribe(
            new RealtimeSubscriptionRequest(RealtimeFamily.Mcp, [], After: 0, channel.Writer));

        (await ReadOneAsync(channel.Reader)).Should().Be(new RealtimeDelivery.Reset(RealtimeResetReasons.TooFarBehind, 5));
    }

    [IntegrationTest]
    public async Task Dispatch_ToAFullChannel_ClosesTheSubscriptionAsSlowConsumerWithoutBlocking()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        var dispatcher = harness.CreateDispatcher();
        var channel = Channel.CreateBounded<RealtimeDelivery>(1);
        await using var subscription = (RealtimeSubscription)dispatcher.Subscribe(
            new RealtimeSubscriptionRequest(RealtimeFamily.Admin, [], null, channel.Writer));
        await WaitUntilLiveAsync(subscription);
        await AppendAsync(harness, RealtimeLogFamilies.Admin, "ops-health", 3);

        await dispatcher.DispatchOnceAsync(CancellationToken.None);

        (await ReadOneAsync(channel.Reader)).Should().BeOfType<RealtimeDelivery.Event>();
        var drain = async () =>
        {
            await foreach (var _ in channel.Reader.ReadAllAsync())
            {
            }
        };
        (await drain.Should().ThrowAsync<RealtimeSubscriptionClosedException>()).Which.Reason.Should().Be(RealtimeResetReasons.SlowConsumer);
        dispatcher.SubscriberCount(RealtimeLogFamilies.Admin).Should().Be(0);
    }

    [IntegrationTest]
    public async Task Subscribe_FilterRejects_EventIsNotWrittenButPositionAdvances()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        var dispatcher = harness.CreateDispatcher();
        var channel = Channel.CreateBounded<RealtimeDelivery>(64);
        await using var subscription = (RealtimeSubscription)dispatcher.Subscribe(
            new RealtimeSubscriptionRequest(RealtimeFamily.Admin, [], null, channel.Writer, Filter: realtimeEvent => realtimeEvent.Sequence % 2 == 0));
        await WaitUntilLiveAsync(subscription);
        await AppendAsync(harness, RealtimeLogFamilies.Admin, "proposals", 4);

        await dispatcher.DispatchOnceAsync(CancellationToken.None);

        (await ReadEventsAsync(channel.Reader, 2)).Select(delivered => delivered.Sequence).Should().Equal(2L, 4L);
        subscription.Position.Should().Be(4);
    }

}
