// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Server.Features.Streaming;
using Honua.Core.Queries.Filters.Cql2;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Streaming;

/// <summary>
/// Unit tests for the feature-stream session manager covering session lifecycle,
/// heartbeat broadcast, slow-consumer disconnect, and admin visibility.
/// </summary>
public sealed class FeatureStreamSessionManagerTests : IDisposable
{
    private readonly FeatureStreamSessionManager _manager;

    public FeatureStreamSessionManagerTests()
    {
        var options = Options.Create(new FeatureStreamOptions
        {
            HeartbeatInterval = TimeSpan.FromSeconds(30),
            MaxBufferPerConnection = 4,
            ReplayBatchSize = 100
        });
        _manager = new FeatureStreamSessionManager(options, NullLogger<FeatureStreamSessionManager>.Instance);
    }

    public void Dispose() => _manager.Dispose();

    [UnitTest]
    public void CreateSession_ReturnsSessionWithReader()
    {
        using var session = _manager.CreateSession("WebSocket", "test-client");

        Assert.NotEqual(Guid.Empty, session.SessionId);
        Assert.NotNull(session.Reader);
        Assert.False(session.DisconnectToken.IsCancellationRequested);
    }

    [UnitTest]
    public async Task CreateSession_EnforcesConcurrentCapUnderLoad()
    {
        using var manager = new FeatureStreamSessionManager(
            Options.Create(new FeatureStreamOptions
            {
                HeartbeatInterval = TimeSpan.FromSeconds(30),
                MaxBufferPerConnection = 4,
                MaxConcurrentSessions = 1,
                ReplayBatchSize = 100
            }),
            NullLogger<FeatureStreamSessionManager>.Instance);

        var startGate = new ManualResetEventSlim(false);
        var sessions = new ConcurrentBag<FeatureStreamSession>();

        var tasks = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() =>
            {
                startGate.Wait();
                var session = manager.TryCreateSession("WebSocket", "load-test");
                if (session is not null)
                {
                    sessions.Add(session);
                }
            }))
            .ToArray();

        startGate.Set();
        await Task.WhenAll(tasks);

        Assert.Single(sessions);
        Assert.Equal(1, manager.SessionCount);

        foreach (var session in sessions)
        {
            session.Dispose();
        }
    }

    [UnitTest]
    public void GetSessions_ReturnsActiveSessionInfo()
    {
        using var session = _manager.CreateSession("SSE", "my-label");

        var sessions = _manager.GetSessions();

        Assert.Single(sessions);
        Assert.Equal(session.SessionId, sessions[0].SessionId);
        Assert.Equal("SSE", sessions[0].Transport);
        Assert.Equal("my-label", sessions[0].ClientLabel);
    }

    [UnitTest]
    public void Broadcast_DeliversMessageToSession()
    {
        using var session = _manager.CreateSession("WebSocket", null);

        var envelope = CreateEnvelope(cursor: 1);
        _manager.Broadcast(FeatureStreamMessage.Data(envelope));

        Assert.True(session.Reader.TryRead(out var msg));
        Assert.False(msg.IsHeartbeat);
        Assert.Equal(1L, msg.Envelope.Cursor);
    }

    [UnitTest]
    public void Broadcast_DeliversToMultipleSessions()
    {
        using var session1 = _manager.CreateSession("WebSocket", null);
        using var session2 = _manager.CreateSession("SSE", null);

        _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 5)));

        Assert.True(session1.Reader.TryRead(out _));
        Assert.True(session2.Reader.TryRead(out _));
    }

    [UnitTest]
    public void BroadcastHeartbeat_DeliversHeartbeatFrame()
    {
        using var session = _manager.CreateSession("WebSocket", null);

        _manager.BroadcastHeartbeat();

        Assert.True(session.Reader.TryRead(out var msg));
        Assert.True(msg.IsHeartbeat);
    }

    [UnitTest]
    public void BroadcastHeartbeat_IncrementsHeartbeatsSent()
    {
        using var session = _manager.CreateSession("WebSocket", null);
        Assert.Equal(0, _manager.HeartbeatsSent);

        _manager.BroadcastHeartbeat();

        Assert.Equal(1, _manager.HeartbeatsSent);
    }

    [UnitTest]
    public void SlowConsumer_DisconnectedWhenBufferIsFull()
    {
        // Fill the bounded channel (MaxBufferPerConnection=4) with Wait mode.
        // Once full and drain is active, TryWrite failure disconnects the session.
        using var session = _manager.CreateSession("WebSocket", null);
        _manager.MarkDrainStarted(session.SessionId);

        // Fill the buffer to capacity.
        for (var i = 0; i < 4; i++)
        {
            _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: i)));
        }

        // Buffer is now full. Next broadcast should disconnect the slow consumer.
        _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 5)));

        Assert.Empty(_manager.GetSessions());
        Assert.True(session.DisconnectToken.IsCancellationRequested);
        Assert.Equal(1, _manager.SlowConsumerDrops);
    }

    [UnitTest]
    public void Broadcast_PreDrain_DoesNotDisconnectOnFullChannel()
    {
        // Before the drain loop starts, a full channel should silently drop events
        // rather than disconnecting a healthy reconnecting client during replay.
        using var session = _manager.CreateSession("WebSocket", null);

        // Fill the buffer to capacity (MaxBufferPerConnection=4).
        for (var i = 0; i < 4; i++)
        {
            _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: i)));
        }

        // Buffer is full but drain hasn't started — should NOT disconnect.
        _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 5)));
        _manager.BroadcastHeartbeat();

        Assert.Single(_manager.GetSessions());
        Assert.False(session.DisconnectToken.IsCancellationRequested);
        Assert.Equal(0, _manager.SlowConsumerDrops);
    }

    [UnitTest]
    public void Broadcast_PostDrain_GracePreventsEarlyDisconnectOnFullChannel()
    {
        // When the drain starts on a full channel, a grace window equal to the
        // channel depth absorbs overflow events from the inherited replay-era
        // backlog. Only after grace is exhausted is the session disconnected.
        using var session = _manager.CreateSession("WebSocket", null);

        // Fill the buffer to capacity (MaxBufferPerConnection=4).
        for (var i = 1; i <= 4; i++)
        {
            _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: i)));
        }

        // Simulate first drain dequeue creating headroom.
        Assert.True(session.Reader.TryRead(out _));

        // Activate drain. Grace = 3 (current channel depth after one dequeue).
        _manager.MarkDrainStarted(session.SessionId);

        // Re-fill the freed slot.
        _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 5)));

        // Overflow 3 times — all absorbed by grace.
        _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 6)));
        _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 7)));
        _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 8)));

        Assert.Single(_manager.GetSessions());
        Assert.False(session.DisconnectToken.IsCancellationRequested);
        Assert.Equal(0, _manager.SlowConsumerDrops);

        // Grace exhausted — next overflow is a genuine slow consumer.
        _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 9)));

        Assert.Empty(_manager.GetSessions());
        Assert.True(session.DisconnectToken.IsCancellationRequested);
        Assert.Equal(1, _manager.SlowConsumerDrops);
    }

    [UnitTest]
    public void ClearDrainGrace_OverflowAfterClear_DisconnectsImmediately()
    {
        // After replay handoff completes, ClearDrainGrace resets the grace window
        // so any overflow during live delivery is treated as a genuine slow consumer.
        using var session = _manager.CreateSession("WebSocket", null);

        // Fill buffer and activate drain with grace.
        for (var i = 1; i <= 4; i++)
        {
            _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: i)));
        }

        Assert.True(session.Reader.TryRead(out _));
        _manager.MarkDrainStarted(session.SessionId);

        // Overflow consumed by grace — session stays alive.
        _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 5)));
        _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 6)));
        Assert.Single(_manager.GetSessions());

        // Clear grace (simulates handoff complete).
        _manager.ClearDrainGrace(session.SessionId);

        // Drain to create headroom, then fill again.
        Assert.True(session.Reader.TryRead(out _));
        Assert.True(session.Reader.TryRead(out _));
        _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 7)));
        _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 8)));

        // Overflow with zero grace — immediate slow-consumer disconnect.
        _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 9)));

        Assert.Empty(_manager.GetSessions());
        Assert.True(session.DisconnectToken.IsCancellationRequested);
        Assert.Equal(1, _manager.SlowConsumerDrops);
    }

    [UnitTest]
    public void RemoveSession_FiresDisconnectToken()
    {
        var session = _manager.CreateSession("WebSocket", null);

        _manager.RemoveSession(session.SessionId, FeatureStreamDisconnectReason.ClientClosed);

        Assert.True(session.DisconnectToken.IsCancellationRequested);
    }

    [UnitTest]
    public void DisconnectSession_RemovesAndSignals()
    {
        var session = _manager.CreateSession("WebSocket", null);

        var result = _manager.DisconnectSession(session.SessionId);

        Assert.True(result);
        Assert.True(session.DisconnectToken.IsCancellationRequested);
        Assert.Empty(_manager.GetSessions());
    }

    [UnitTest]
    public void DisconnectSession_ReturnsFalseForUnknownId()
    {
        Assert.False(_manager.DisconnectSession(Guid.NewGuid()));
    }

    [UnitTest]
    public void DisposeSession_RemovesFromManager()
    {
        var session = _manager.CreateSession("SSE", null);
        session.Dispose();

        Assert.Empty(_manager.GetSessions());
    }

    [UnitTest]
    public void TryWriteToSession_WritesToSpecificSession()
    {
        using var session = _manager.CreateSession("WebSocket", null);

        var result = _manager.TryWriteToSession(session.SessionId, FeatureStreamMessage.Data(CreateEnvelope(cursor: 42)));

        Assert.True(result);
        Assert.True(session.Reader.TryRead(out var msg));
        Assert.Equal(42L, msg.Envelope.Cursor);
    }

    [UnitTest]
    public void TryWriteToSession_ReturnsFalseForUnknownSession()
    {
        Assert.False(_manager.TryWriteToSession(Guid.NewGuid(), FeatureStreamMessage.Data(CreateEnvelope(cursor: 1))));
    }

    [UnitTest]
    public void Broadcast_DuringReplayWindow_QueuesEventsForDedupDrain()
    {
        // Simulates the replay-to-live handoff: events broadcast while replay
        // writes directly to the transport must be queued in the channel so the
        // drain loop can deliver them (skipping duplicates via replayCursor).
        using var session = _manager.CreateSession("WebSocket", null);

        // Simulate replay cursor at 10 — events 1-10 were replayed directly to transport.
        const long replayCursor = 10;

        // Events broadcast during replay (overlap + new).
        _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 8)));   // overlap
        _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 10)));  // overlap
        _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 11)));  // new

        // All three are in the channel — dedup is the drain loop's responsibility.
        var messages = new List<FeatureStreamMessage>();
        while (session.Reader.TryRead(out var msg))
        {
            messages.Add(msg);
        }

        Assert.Equal(3, messages.Count);

        // Simulate what the drain loop does: skip events <= replayCursor.
        var delivered = messages.Where(m => !m.IsHeartbeat && m.Envelope.Cursor > replayCursor).ToList();
        Assert.Single(delivered);
        Assert.Equal(11L, delivered[0].Envelope.Cursor);
    }

    // Regression for review finding "WebSocket per-subscription replay races
    // the live writer". When a subscribe handler is replaying old events
    // directly to the socket, live broadcasts must not queue events for the
    // paused subscription — replay covers the cursor range itself, and
    // queueing would create concurrent SendAsync + duplicate delivery.
    [UnitTest]
    public void Broadcast_WithPausedSubscription_SkipsQueueing()
    {
        using var session = _manager.CreateSession("WebSocket", "paused-subscribe");

        Assert.True(_manager.TryAddSubscription(session.SessionId, "subB", filter: null, paused: true));

        _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 11)));
        _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 12)));

        // Default subscription still receives broadcasts; only the paused one is skipped.
        // The events appear once each (default), not twice (default + subB).
        var messages = new List<FeatureStreamMessage>();
        while (session.Reader.TryRead(out var msg))
        {
            messages.Add(msg);
        }

        Assert.Equal(2, messages.Count);
        Assert.All(messages, m => Assert.Equal(FeatureStreamSessionManager.DefaultSubscriptionId, m.Envelope.SubscriptionId));
    }

    [UnitTest]
    public void Broadcast_AfterUnpauseSubscription_QueuesForBothSubscriptions()
    {
        using var session = _manager.CreateSession("WebSocket", "post-unpause");

        Assert.True(_manager.TryAddSubscription(session.SessionId, "subB", filter: null, paused: true));

        // While paused, broadcasts are skipped for subB (only default delivers).
        _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 21)));

        Assert.True(_manager.TryUnpauseSubscription(session.SessionId, "subB"));

        // After unpause, broadcasts deliver to both default and subB.
        _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 22)));

        var messages = new List<FeatureStreamMessage>();
        while (session.Reader.TryRead(out var msg))
        {
            messages.Add(msg);
        }

        // Cursor 21: 1 message (default only). Cursor 22: 2 messages (default + subB).
        Assert.Equal(3, messages.Count);
        Assert.Single(messages, m => m.Envelope.Cursor == 21);
        Assert.Equal(2, messages.Count(m => m.Envelope.Cursor == 22));
        Assert.Contains(messages, m => m.Envelope.Cursor == 22 && m.Envelope.SubscriptionId == "subB");
    }

    // Regression for review finding "Per-subscription replay can drop events
    // published before unpause". The replay path claims a (event, subscription)
    // delivery slot through TryRememberSubscriptionDelivery; once claimed, a
    // subsequent broadcast for the same event must skip queueing for that
    // subscription so the client sees the event exactly once. This test runs
    // the replay-then-broadcast path explicitly.
    [UnitTest]
    public void Broadcast_AfterReplayMarkedSubscription_SkipsDuplicateQueueing()
    {
        using var session = _manager.CreateSession("WebSocket", "replay-then-broadcast");

        Assert.True(_manager.TryAddSubscription(session.SessionId, "subB", filter: null, paused: false));

        // Simulate the per-subscription replay claiming the (event, subB) slot.
        Assert.True(_manager.TryRememberSubscriptionDelivery(session.SessionId, "subB", "evt-101"));

        // Now broadcast the same event id — broadcast must skip queueing for subB
        // because replay already claimed delivery; default still receives.
        var envelope = CreateEnvelope(cursor: 101) with { EventId = "evt-101" };
        _manager.Broadcast(FeatureStreamMessage.Data(envelope));

        var messages = new List<FeatureStreamMessage>();
        while (session.Reader.TryRead(out var msg))
        {
            messages.Add(msg);
        }

        // Exactly one message (default), none for subB.
        Assert.Single(messages);
        Assert.Equal(FeatureStreamSessionManager.DefaultSubscriptionId, messages[0].Envelope.SubscriptionId);
    }

    // Regression: broadcast first, then replay's TryRememberSubscriptionDelivery
    // must observe the claim and skip — i.e., the slot is owned by whichever
    // path arrives first.
    [UnitTest]
    public void TryRememberSubscriptionDelivery_AfterBroadcastClaimedSlot_ReturnsFalse()
    {
        using var session = _manager.CreateSession("WebSocket", "broadcast-then-replay");

        Assert.True(_manager.TryAddSubscription(session.SessionId, "subB", filter: null, paused: false));

        var envelope = CreateEnvelope(cursor: 200) with { EventId = "evt-200" };
        _manager.Broadcast(FeatureStreamMessage.Data(envelope));

        // Broadcast claimed (evt-200, subB) when it queued. Replay's later
        // attempt to claim the same slot must observe that and skip its send.
        Assert.False(_manager.TryRememberSubscriptionDelivery(session.SessionId, "subB", "evt-200"));

        // First-time delivery of a different (event, subscription) succeeds.
        Assert.True(_manager.TryRememberSubscriptionDelivery(session.SessionId, "subB", "evt-201"));
    }

    [UnitTest]
    public void Broadcast_WithLayerFilter_OnlyDeliversMatchingEvents()
    {
        var filter = new StreamSubscriptionFilter(layerIds: [1, 2]);
        using var filtered = _manager.CreateSession("WebSocket", "filtered", filter);
        using var unfiltered = _manager.CreateSession("WebSocket", "unfiltered");

        _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 1, layerId: 0)));
        _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 2, layerId: 1)));
        _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 3, layerId: 2)));
        _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 4, layerId: 3)));

        // Filtered session should only get layer 1 and 2.
        var filteredMessages = new List<FeatureStreamMessage>();
        while (filtered.Reader.TryRead(out var msg))
        {
            filteredMessages.Add(msg);
        }

        Assert.Equal(2, filteredMessages.Count);
        Assert.Equal(1, filteredMessages[0].Envelope.LayerId);
        Assert.Equal(2, filteredMessages[1].Envelope.LayerId);

        // Unfiltered session should get all 4.
        var unfilteredCount = 0;
        while (unfiltered.Reader.TryRead(out _))
        {
            unfilteredCount++;
        }

        Assert.Equal(4, unfilteredCount);
    }

    [UnitTest]
    public void Broadcast_WithServiceFilter_OnlyDeliversMatchingEvents()
    {
        var filter = new StreamSubscriptionFilter(serviceId: "target-svc");
        using var filtered = _manager.CreateSession("WebSocket", "svc-filtered", filter);

        _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelopeForService(cursor: 1, serviceId: "other-svc")));
        _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelopeForService(cursor: 2, serviceId: "target-svc")));
        _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelopeForService(cursor: 3, serviceId: "target-svc")));

        var count = 0;
        while (filtered.Reader.TryRead(out var msg))
        {
            Assert.Equal("target-svc", msg.Envelope.ServiceId);
            count++;
        }

        Assert.Equal(2, count);
    }

    [UnitTest]
    public void Broadcast_WithBboxFilter_OnlyDeliversIntersectingEvents()
    {
        var filter = new StreamSubscriptionFilter(bbox: [0d, 0d, 10d, 10d]);
        using var filtered = _manager.CreateSession("WebSocket", "bbox-filtered", filter);

        _manager.Broadcast(FeatureStreamMessage.Data(
            CreateEnvelope(cursor: 1),
            geometryEnvelope: [20d, 20d, 30d, 30d]));
        _manager.Broadcast(FeatureStreamMessage.Data(
            CreateEnvelope(cursor: 2),
            geometryEnvelope: [5d, 5d, 15d, 15d]));

        Assert.True(filtered.Reader.TryRead(out var msg));
        Assert.Equal(2L, msg.Envelope.Cursor);
        Assert.False(filtered.Reader.TryRead(out _));
    }

    [UnitTest]
    public void Broadcast_WithBboxFilter_DropsNonDeleteEventsWithoutGeometry()
    {
        var filter = new StreamSubscriptionFilter(bbox: [0d, 0d, 10d, 10d]);
        using var filtered = _manager.CreateSession("WebSocket", "bbox-null-geometry", filter);

        _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 1), geometryEnvelope: null));
        _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 2), geometryEnvelope: [5d, 5d, 6d, 6d]));

        Assert.True(filtered.Reader.TryRead(out var msg));
        Assert.Equal(2L, msg.Envelope.Cursor);
        Assert.False(filtered.Reader.TryRead(out _));
    }

    [UnitTest]
    public void Broadcast_WithBboxFilter_DeleteEventsWithoutGeometryStillPass()
    {
        var filter = new StreamSubscriptionFilter(bbox: [0d, 0d, 10d, 10d]);
        using var filtered = _manager.CreateSession("WebSocket", "bbox-delete-no-geometry", filter);

        _manager.Broadcast(FeatureStreamMessage.Data(
            CreateEnvelope(cursor: 1) with { Operation = "delete" },
            geometryEnvelope: null));

        Assert.True(filtered.Reader.TryRead(out var msg));
        Assert.Equal(1L, msg.Envelope.Cursor);
        Assert.Equal("delete", msg.Envelope.Operation);
        Assert.False(filtered.Reader.TryRead(out _));
    }

    [UnitTest]
    public void Broadcast_WithAttributeFilter_OnlyDeliversMatchingEvents()
    {
        var filter = new StreamSubscriptionFilter(attributeFilter: new Cql2Parser().Parse("status = 'active'"));
        using var filtered = _manager.CreateSession("WebSocket", "attribute-filtered", filter);

        _manager.Broadcast(FeatureStreamMessage.Data(
            CreateEnvelope(cursor: 1),
            propertiesJson: """{"status":"inactive"}"""));
        _manager.Broadcast(FeatureStreamMessage.Data(
            CreateEnvelope(cursor: 2),
            propertiesJson: """{"status":"active"}"""));

        Assert.True(filtered.Reader.TryRead(out var msg));
        Assert.Equal(2L, msg.Envelope.Cursor);
        Assert.False(filtered.Reader.TryRead(out _));
    }

    [UnitTest]
    public void Broadcast_WithCombinedFilters_OnlyDeliversEventsMatchingAllCriteria()
    {
        var filter = new StreamSubscriptionFilter(
            serviceId: "target-svc",
            layerIds: [2],
            bbox: [0d, 0d, 10d, 10d],
            attributeFilter: new Cql2Parser().Parse("status = 'active'"));
        using var filtered = _manager.CreateSession("WebSocket", "combined-filtered", filter);

        _manager.Broadcast(FeatureStreamMessage.Data(
            CreateEnvelope(cursor: 1, layerId: 2, serviceId: "other-svc"),
            geometryEnvelope: [5d, 5d, 6d, 6d],
            propertiesJson: """{"status":"active"}"""));
        _manager.Broadcast(FeatureStreamMessage.Data(
            CreateEnvelope(cursor: 2, layerId: 2, serviceId: "target-svc"),
            geometryEnvelope: [20d, 20d, 30d, 30d],
            propertiesJson: """{"status":"active"}"""));
        _manager.Broadcast(FeatureStreamMessage.Data(
            CreateEnvelope(cursor: 3, layerId: 2, serviceId: "target-svc"),
            geometryEnvelope: [5d, 5d, 6d, 6d],
            propertiesJson: """{"status":"inactive"}"""));
        _manager.Broadcast(FeatureStreamMessage.Data(
            CreateEnvelope(cursor: 4, layerId: 2, serviceId: "target-svc"),
            geometryEnvelope: [5d, 5d, 6d, 6d],
            propertiesJson: """{"status":"active"}"""));

        Assert.True(filtered.Reader.TryRead(out var msg));
        Assert.Equal(4L, msg.Envelope.Cursor);
        Assert.False(filtered.Reader.TryRead(out _));
    }

    [UnitTest]
    public void Broadcast_WithEmptyLayerFilter_MatchesNothing()
    {
        // Simulates malformed layerIds (e.g. ?layerIds=abc) where no valid IDs were parsed.
        var filter = new StreamSubscriptionFilter(layerIds: []);
        using var filtered = _manager.CreateSession("WebSocket", "empty-filter", filter);

        _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 1, layerId: 0)));
        _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 2, layerId: 1)));

        Assert.False(filtered.Reader.TryRead(out _));
    }

    [UnitTest]
    public void Broadcast_HeartbeatBypassesFilter()
    {
        var filter = new StreamSubscriptionFilter(layerIds: [99]);
        using var filtered = _manager.CreateSession("WebSocket", "hb-filter", filter);

        // Heartbeats should always be delivered regardless of filter.
        _manager.BroadcastHeartbeat();

        Assert.True(filtered.Reader.TryRead(out var msg));
        Assert.True(msg.IsHeartbeat);
    }

    [UnitTest]
    public void Broadcast_WithRedisSubscription_FanOutsToOtherManagers()
    {
        var subscriber = Substitute.For<ISubscriber>();
        var redis = Substitute.For<IConnectionMultiplexer>();
        var handlers = new List<Action<RedisChannel, RedisValue>>();

        redis.GetSubscriber().Returns(subscriber);
        subscriber.Subscribe(Arg.Any<RedisChannel>(), Arg.Do<Action<RedisChannel, RedisValue>>(handler => handlers.Add(handler)));
        subscriber.Publish(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(callInfo =>
            {
                var channel = callInfo.Arg<RedisChannel>();
                var value = callInfo.Arg<RedisValue>();
                foreach (var handler in handlers)
                {
                    handler(channel, value);
                }

                return handlers.Count;
            });

        var options = Options.Create(new FeatureStreamOptions
        {
            HeartbeatInterval = TimeSpan.FromSeconds(30),
            MaxBufferPerConnection = 4,
            ReplayBatchSize = 100
        });

        using var localManager = new FeatureStreamSessionManager(options, NullLogger<FeatureStreamSessionManager>.Instance, redis);
        using var remoteManager = new FeatureStreamSessionManager(options, NullLogger<FeatureStreamSessionManager>.Instance, redis);
        using var localSession = localManager.CreateSession("WebSocket", "local");
        using var remoteSession = remoteManager.CreateSession("WebSocket", "remote");

        var localDelivered = localManager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 42)));

        Assert.True(localSession.Reader.TryRead(out var localMessage));
        Assert.True(remoteSession.Reader.TryRead(out var remoteMessage));
        Assert.Equal(1, localDelivered);
        Assert.Equal(42, localMessage.Envelope.Cursor);
        Assert.Equal(localMessage.Envelope.Cursor, remoteMessage.Envelope.Cursor);
        Assert.Equal(localMessage.Envelope.ServiceId, remoteMessage.Envelope.ServiceId);
    }

    [UnitTest]
    public void Broadcast_DropsDuplicateEventIdForSession()
    {
        using var session = _manager.CreateSession("WebSocket", null);

        const string eventId = "evt-duplicate";
        _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 77, eventId: eventId)));
        _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 12, eventId: eventId)));

        Assert.True(session.Reader.TryRead(out var firstMessage));
        Assert.False(session.Reader.TryRead(out _));
        Assert.Equal(77L, firstMessage.Envelope.Cursor);
    }

    [UnitTest]
    public void Broadcast_AllowsOutOfOrderLowerCursorWithDifferentEventId()
    {
        using var session = _manager.CreateSession("WebSocket", null);

        _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 22, eventId: "evt-22")));
        _manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 21, eventId: "evt-21")));

        Assert.True(session.Reader.TryRead(out var firstMessage));
        Assert.True(session.Reader.TryRead(out var secondMessage));
        Assert.Equal(22L, firstMessage.Envelope.Cursor);
        Assert.Equal(21L, secondMessage.Envelope.Cursor);
    }

    [UnitTest]
    public void Broadcast_RecoversClusterSubscriptionAfterInitialFailure()
    {
        var subscriber = Substitute.For<ISubscriber>();
        var redis = Substitute.For<IConnectionMultiplexer>();
        var handlers = new List<Action<RedisChannel, RedisValue>>();
        var subscribeAttempts = 0;

        redis.GetSubscriber().Returns(subscriber);
        subscriber.When(x => x.Subscribe(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>()))
            .Do(callInfo =>
            {
                subscribeAttempts++;
                if (subscribeAttempts == 1)
                {
                    throw new InvalidOperationException("subscribe failed");
                }

                handlers.Add((Action<RedisChannel, RedisValue>)callInfo.Args()[1]);
            });
        subscriber.Publish(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(callInfo =>
            {
                var channel = callInfo.Arg<RedisChannel>();
                var value = callInfo.Arg<RedisValue>();
                foreach (var handler in handlers)
                {
                    handler(channel, value);
                }

                return handlers.Count;
            });

        var options = Options.Create(new FeatureStreamOptions
        {
            HeartbeatInterval = TimeSpan.FromSeconds(30),
            MaxBufferPerConnection = 4,
            ReplayBatchSize = 100
        });

        using var localManager = new FeatureStreamSessionManager(options, NullLogger<FeatureStreamSessionManager>.Instance, redis);
        using var remoteManager = new FeatureStreamSessionManager(options, NullLogger<FeatureStreamSessionManager>.Instance, redis);
        using var localSession = localManager.CreateSession("WebSocket", "local");
        using var remoteSession = remoteManager.CreateSession("WebSocket", "remote");

        var delivered = localManager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 91)));

        Assert.Equal(1, delivered);
        Assert.True(localSession.Reader.TryRead(out var localMessage));
        Assert.True(remoteSession.Reader.TryRead(out var remoteMessage));
        Assert.Equal(91L, localMessage.Envelope.Cursor);
        Assert.Equal(localMessage.Envelope.Cursor, remoteMessage.Envelope.Cursor);
        Assert.Equal(3, subscribeAttempts);
    }

    [UnitTest]
    public void Broadcast_QueuesClusterPayloadWhenRedisPublishFails_ThenFlushesOnNextBroadcast()
    {
        var subscriber = Substitute.For<ISubscriber>();
        var redis = Substitute.For<IConnectionMultiplexer>();
        var handlers = new List<Action<RedisChannel, RedisValue>>();
        var publishAttempts = 0;

        redis.GetSubscriber().Returns(subscriber);
        subscriber.Subscribe(Arg.Any<RedisChannel>(), Arg.Do<Action<RedisChannel, RedisValue>>(handler => handlers.Add(handler)));
        subscriber.Publish(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(callInfo =>
            {
                publishAttempts++;
                if (publishAttempts == 1)
                {
                    throw new InvalidOperationException("publish failed");
                }

                var channel = callInfo.Arg<RedisChannel>();
                var value = callInfo.Arg<RedisValue>();
                foreach (var handler in handlers)
                {
                    handler(channel, value);
                }

                return handlers.Count;
            });

        var options = Options.Create(new FeatureStreamOptions
        {
            HeartbeatInterval = TimeSpan.FromSeconds(30),
            MaxBufferPerConnection = 4,
            ReplayBatchSize = 100
        });

        using var localManager = new FeatureStreamSessionManager(options, NullLogger<FeatureStreamSessionManager>.Instance, redis);
        using var remoteManager = new FeatureStreamSessionManager(options, NullLogger<FeatureStreamSessionManager>.Instance, redis);
        using var localSession = localManager.CreateSession("WebSocket", "local");
        using var remoteSession = remoteManager.CreateSession("WebSocket", "remote");

        localManager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 101)));
        localManager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: 102)));

        Assert.True(localSession.Reader.TryRead(out var firstLocal));
        Assert.True(localSession.Reader.TryRead(out var secondLocal));
        Assert.Equal(101L, firstLocal.Envelope.Cursor);
        Assert.Equal(102L, secondLocal.Envelope.Cursor);

        Assert.True(remoteSession.Reader.TryRead(out var firstRemote));
        Assert.True(remoteSession.Reader.TryRead(out var secondRemote));
        Assert.Equal(101L, firstRemote.Envelope.Cursor);
        Assert.Equal(102L, secondRemote.Envelope.Cursor);
    }

    private static FeatureStreamEnvelope CreateEnvelope(long cursor) => CreateEnvelope(cursor, layerId: 0, serviceId: "test-svc");

    private static FeatureStreamEnvelope CreateEnvelope(long cursor, int layerId) => CreateEnvelope(cursor, layerId, serviceId: "test-svc");

    private static FeatureStreamEnvelope CreateEnvelope(long cursor, string eventId) => CreateEnvelope(cursor, layerId: 0, serviceId: "test-svc", eventId: eventId);

    private static FeatureStreamEnvelope CreateEnvelope(long cursor, int layerId, string serviceId, string? eventId = null) => new()
    {
        EventId = eventId ?? Guid.NewGuid().ToString(),
        Cursor = cursor,
        Timestamp = DateTimeOffset.UtcNow,
        ServiceId = serviceId,
        LayerId = layerId,
        ObjectId = 1,
        Operation = "create",
        Protocol = "rest",
        RequestId = "req-1"
    };

    private static FeatureStreamEnvelope CreateEnvelopeForService(long cursor, string serviceId) => CreateEnvelope(cursor, layerId: 0, serviceId: serviceId);
}
