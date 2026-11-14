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
public sealed class RealtimeFanoutProviderTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
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

    [UnitTheory]
    [InlineData(null, null, false)]
    [InlineData(null, "MultiNode", true)]
    [InlineData("auto", "multinode", true)]
    [InlineData("auto", "SingleNode", false)]
    [InlineData("in-process", "MultiNode", false)]
    [InlineData("postgres", null, true)]
    public void ProviderChoice_FollowsRealtimeProviderAndDeploymentMode(string? provider, string? mode, bool postgres)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Realtime:Provider"] = provider, ["Deployment:Mode"] = mode })
            .Build();

        RealtimeServiceCollectionExtensions.UsesPostgresProvider(configuration).Should().Be(postgres);
    }

    [UnitTest]
    public void ProviderChoice_UnknownProvider_IsRefused()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Realtime:Provider"] = "redis" })
            .Build();

        var choose = () => RealtimeServiceCollectionExtensions.UsesPostgresProvider(configuration);

        choose.Should().Throw<InvalidOperationException>().WithMessage("*auto, in-process or postgres*");
    }

    [UnitTest]
    public async Task Registration_MultiNode_ResolvesTheCrossNodeProviderAndTheTail()
    {
        await using var provider = BuildServices(new() { ["Deployment:Mode"] = "MultiNode" }, hostsDatabasePollingLoops: true);

        provider.GetRequiredService<IRealtimeFanoutProvider>().Capabilities.CrossNode.Should().BeTrue();
        provider.GetServices<IHostedService>().OfType<RealtimeTailBackgroundService>().Should().ContainSingle();
    }

    [Theory]
    [InlineData("SingleNode")]
    [InlineData("MultiNode")]
    [Trait("Tier", "Fast")]
    public async Task Registration_WithoutDataSource_HostedServicesStartWithoutDatabaseDependencies(string mode)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Deployment:Mode"] = mode }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddRealtimeFanout(configuration, hostsDatabasePollingLoops: true);
        await using var provider = services.BuildServiceProvider();

        var hosted = provider.GetServices<IHostedService>().ToArray();
        hosted.OfType<RealtimeTailBackgroundService>().Should().ContainSingle();
        foreach (var service in hosted)
        {
            await service.StartAsync(CancellationToken.None);
            await service.StopAsync(CancellationToken.None);
        }
    }

    [UnitTest]
    public async Task Registration_WithoutDatabasePollingLoops_RegistersNoTail()
    {
        await using var provider = BuildServices(new() { ["Deployment:Mode"] = "MultiNode" }, hostsDatabasePollingLoops: false);

        provider.GetRequiredService<IRealtimeFanoutProvider>().Capabilities.CrossNode.Should().BeTrue();
        provider.GetServices<IHostedService>().OfType<RealtimeTailBackgroundService>().Should().BeEmpty();
    }

    [UnitTest]
    public void Listener_ProxyHost_RunsPollOnly()
    {
        var listener = CreateListener(null, "Host=honua.proxy-abc.example.test;Database=honua;Username=u");

        listener.PollOnlyReason.Should().Contain("proxy");
        listener.IsConnected.Should().BeFalse();
    }

    [UnitTest]
    public void Listener_WithoutItsOwnConnectionString_UsesTheResolvedSystemConnectionString()
    {
        var listener = CreateListener(null, "Host=db.example.test;Database=honua;Username=u;Password=resolved");

        listener.PollOnlyReason.Should().BeNull();
    }

    [UnitTest]
    public void Listener_UnparseableConnectionString_RunsPollOnlyInsteadOfFailingStartup()
    {
        var listener = CreateListener("arn:aws:secretsmanager:region:account:secret:name", "Host=db.example.test;Database=honua");

        listener.PollOnlyReason.Should().Contain("could not be parsed");
    }

    [UnitTest]
    public async Task Registration_ListenerUsesTheDataSourceConnectionStringNotTheRawSetting()
    {
        // The raw setting is a secret reference; only the data source holds the resolved string.
        await using var provider = BuildServices(
            new() { ["Deployment:Mode"] = "MultiNode", ["ConnectionStrings:DefaultConnection"] = "secret-reference:honua/db" },
            hostsDatabasePollingLoops: true,
            rawConnectionString: "secret-reference:honua/db");

        provider.GetRequiredService<RealtimeListener>().PollOnlyReason.Should().BeNull();
    }

    [IntegrationTest]
    public async Task Registration_ResolvedPasswordFromProductionDataSource_ListenerAuthenticates()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Deployment:Mode"] = "MultiNode",
            ["ConnectionStrings:DefaultConnection"] = "secret-reference:honua/db"
        }).Build();
        await using var dataSource = PostgresDataSourceFactory.Create(
            database.ConnectionString, schemaHeadersEnabled: false, new ConnectionLimits());
        new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).Password.Should().NotBeNullOrEmpty();
        var services = new ServiceCollection().AddLogging().AddSingleton(dataSource);
        services.AddRealtimeFanout(configuration, hostsDatabasePollingLoops: true);
        await using var provider = services.BuildServiceProvider();
        var listener = provider.GetRequiredService<RealtimeListener>();
        using var stopping = new CancellationTokenSource();
        var running = listener.RunAsync(() => { }, stopping.Token);
        try
        {
            await WaitForAsync(() => listener.IsConnected, "the production data source preserves resolved authentication for LISTEN");
        }
        finally
        {
            await stopping.CancelAsync();
            await running;
        }
    }

    private static RealtimeListener CreateListener(string? listenerConnectionString, string? systemConnectionString)
        => RealtimeListener.Create(
            string.IsNullOrWhiteSpace(listenerConnectionString) ? systemConnectionString : listenerConnectionString,
            Microsoft.Extensions.Options.Options.Create(new RealtimeOptions()),
            NullLogger<RealtimeListener>.Instance);

    private static ServiceProvider BuildServices(
        Dictionary<string, string?> settings,
        bool hostsDatabasePollingLoops,
        string rawConnectionString = "Host=localhost;Database=honua;Username=u")
    {
        settings["ConnectionStrings:DefaultConnection"] = rawConnectionString;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddLogging()
            .AddSingleton(NpgsqlDataSource.Create("Host=localhost;Database=honua;Username=u"));
        services.AddSingleton(provider => new JobStoreDatabase(provider.GetRequiredService<NpgsqlDataSource>()));
        services.AddRealtimeFanout(configuration, hostsDatabasePollingLoops);
        return services.BuildServiceProvider();
    }

    // Another task: its own dispatcher, which nobody here runs; its appends notify every task.
    private static PostgresRealtimeFanoutProvider CreateProvider(RealtimeLogHarness harness)
        => new(harness.Log, harness.CreateDispatcher(), new RealtimePresenceBatcher(harness.Log));

    private static async Task AppendAsync(RealtimeLogHarness harness, string family, string scope, int count)
        => await harness.Log.AppendAsync(
            family,
            Enumerable.Range(0, count).Select(n => new RealtimeLogRow(scope, "test", RealtimeLogHarness.Payload(new { n }))).ToArray(),
            null,
            CancellationToken.None);

    private static async Task WaitUntilLiveAsync(RealtimeSubscription subscription)
        => await WaitForAsync(() => subscription.IsLive, "the subscription goes live");

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            deadline.Elapsed.Should().BeLessThan(_deliveryTimeout, $"{what} in time");
            await Task.Delay(20);
        }
    }

    private static async Task<RealtimeDelivery> ReadOneAsync(ChannelReader<RealtimeDelivery> reader)
    {
        using var timeout = new CancellationTokenSource(_deliveryTimeout);
        return await reader.ReadAsync(timeout.Token);
    }

    private static async Task<IReadOnlyList<RealtimeEvent>> ReadEventsAsync(ChannelReader<RealtimeDelivery> reader, int count)
    {
        using var timeout = new CancellationTokenSource(_deliveryTimeout);
        var events = new List<RealtimeEvent>();
        while (events.Count < count)
        {
            var delivery = await reader.ReadAsync(timeout.Token);
            delivery.Should().BeOfType<RealtimeDelivery.Event>();
            events.Add(((RealtimeDelivery.Event)delivery).Value);
        }

        return events;
    }
}
