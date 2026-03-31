// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Streaming;
using Honua.Core.Queries.Filters.Cql2;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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

    private static FeatureStreamEnvelope CreateEnvelope(long cursor) => CreateEnvelope(cursor, layerId: 0, serviceId: "test-svc");

    private static FeatureStreamEnvelope CreateEnvelope(long cursor, int layerId) => CreateEnvelope(cursor, layerId, serviceId: "test-svc");

    private static FeatureStreamEnvelope CreateEnvelope(long cursor, int layerId, string serviceId) => new()
    {
        EventId = Guid.NewGuid().ToString(),
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
