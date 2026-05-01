// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.WebSockets;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Streaming;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Streaming;

/// <summary>
/// Integration tests for feature-change streaming endpoints covering
/// WebSocket connect, SSE connect, heartbeat delivery, slow-consumer disconnect,
/// cursor replay on reconnect, and admin session visibility.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Streaming)]
[Operation(Operations.Streaming)]
public sealed class FeatureStreamEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    // ─── AC: Client can open a WebSocket connection ─────────────────────

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task WebSocket_Connect_AcceptsAndReceivesMessages()
    {
        var wsClient = _fixture.CreateWebSocketClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        using var ws = await wsClient.ConnectAsync(
            new Uri("ws://localhost/api/v1/streaming/features?clientLabel=ws-test"),
            cts.Token);

        ws.State.Should().Be(WebSocketState.Open);
        var connected = await ReceiveWebSocketJsonAsync(ws, cts.Token);
        connected.GetProperty("type").GetString().Should().Be("status");
        connected.GetProperty("status").GetString().Should().Be("connected");

        // Allow the handler to register the session after accepting the upgrade.
        var sessionManager = _fixture.GetService<FeatureStreamSessionManager>();
        await WaitForSessionAsync(sessionManager, "ws-test", cts.Token);

        sessionManager.GetSessions().Should().Contain(s => s.ClientLabel == "ws-test" && s.Transport == "WebSocket");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task WebSocket_SubscribeThenUnsubscribe_ReturnsStatusFrames()
    {
        var wsClient = _fixture.CreateWebSocketClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        using var ws = await wsClient.ConnectAsync(
            new Uri("ws://localhost/api/v1/streaming/features?clientLabel=ws-sub-test"),
            cts.Token);

        // Bare WebSocket clients get a status frame first; explicit subscriptions
        // are added through control messages.
        _ = await ReceiveWebSocketJsonAsync(ws, cts.Token);

        await SendWebSocketJsonAsync(ws, """{"type":"subscribe","subscriptionId":"layer-zero","layerId":0}""", cts.Token);
        var subscribed = await ReceiveWebSocketJsonAsync(ws, cts.Token);
        subscribed.GetProperty("type").GetString().Should().Be("status");
        subscribed.GetProperty("status").GetString().Should().Be("subscribed");
        subscribed.GetProperty("subscriptionId").GetString().Should().Be("layer-zero");

        await SendWebSocketJsonAsync(ws, """{"type":"unsubscribe","subscriptionId":"layer-zero"}""", cts.Token);
        var unsubscribed = await ReceiveWebSocketJsonAsync(ws, cts.Token);
        unsubscribed.GetProperty("type").GetString().Should().Be("status");
        unsubscribed.GetProperty("status").GetString().Should().Be("unsubscribed");
        unsubscribed.GetProperty("subscriptionId").GetString().Should().Be("layer-zero");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
    }

    // Regression for review finding "WebSocket control frames are read with no
    // size limit". A misbehaving client cannot exhaust server memory by sending
    // an oversized control frame; the server bounces it with a client-safe
    // error and closes the connection.
    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task WebSocket_OversizedControlFrame_ReturnsErrorAndCloses()
    {
        var fixture = new WebAppFixture().ConfigureWebHost(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FeatureStreaming:MaxControlFrameBytes"] = "256"
                });
            });
        });

        await fixture.InitializeAsync();
        try
        {
            var wsClient = fixture.CreateWebSocketClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            using var ws = await wsClient.ConnectAsync(
                new Uri("ws://localhost/api/v1/streaming/features?clientLabel=oversize-test"),
                cts.Token);

            // Drain the connect status frame.
            _ = await ReceiveWebSocketJsonAsync(ws, cts.Token);

            // Build a control frame >256 bytes by padding subscriptionId.
            var pad = new string('x', 512);
            var oversized = $$"""{"type":"subscribe","subscriptionId":"{{pad}}","layerId":0}""";
            await SendWebSocketJsonAsync(ws, oversized, cts.Token);

            // Server must respond with a control-frame-too-large error.
            var error = await ReceiveWebSocketJsonAsync(ws, cts.Token);
            error.GetProperty("type").GetString().Should().Be("error");
            error.GetProperty("code").GetString().Should().Be("control-frame-too-large");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    // Regression for review finding "WebSocket subscriptions are unbounded
    // per session". A bad client cannot inflate per-session work indefinitely
    // by piling on unique subscribe ids; the server replies with a typed
    // error frame and keeps the connection open so the client can recover.
    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task WebSocket_SubscriptionLimit_RejectsExcessSubscribesWithError()
    {
        var fixture = new WebAppFixture().ConfigureWebHost(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Default subscription occupies one slot; two more reaches the cap.
                    ["FeatureStreaming:MaxSubscriptionsPerSession"] = "3"
                });
            });
        });

        await fixture.InitializeAsync();
        try
        {
            var wsClient = fixture.CreateWebSocketClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            using var ws = await wsClient.ConnectAsync(
                new Uri("ws://localhost/api/v1/streaming/features?clientLabel=cap-test"),
                cts.Token);

            // Drain the connect status frame.
            _ = await ReceiveWebSocketJsonAsync(ws, cts.Token);

            // Two new ids fill the remaining slots.
            await SendWebSocketJsonAsync(ws, """{"type":"subscribe","subscriptionId":"sub-1","layerId":0}""", cts.Token);
            (await ReceiveWebSocketJsonAsync(ws, cts.Token)).GetProperty("status").GetString().Should().Be("subscribed");
            await SendWebSocketJsonAsync(ws, """{"type":"subscribe","subscriptionId":"sub-2","layerId":0}""", cts.Token);
            (await ReceiveWebSocketJsonAsync(ws, cts.Token)).GetProperty("status").GetString().Should().Be("subscribed");

            // Third new id is rejected with a typed error.
            await SendWebSocketJsonAsync(ws, """{"type":"subscribe","subscriptionId":"sub-3","layerId":0}""", cts.Token);
            var error = await ReceiveWebSocketJsonAsync(ws, cts.Token);
            error.GetProperty("type").GetString().Should().Be("error");
            error.GetProperty("code").GetString().Should().Be("subscription-limit-reached");

            // Connection stays open; replacing an existing id still works.
            await SendWebSocketJsonAsync(ws, """{"type":"subscribe","subscriptionId":"sub-1","layerId":0}""", cts.Token);
            (await ReceiveWebSocketJsonAsync(ws, cts.Token)).GetProperty("status").GetString().Should().Be("subscribed");

            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    // Regression for review finding "WebSocket subscriptions are unbounded
    // per session" (subscriptionId length cap): an oversize subscriptionId
    // is rejected with a client-safe error before it reaches the session
    // dictionary, where it would otherwise inflate per-message dedup keys
    // and log lines.
    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task WebSocket_OversizedSubscriptionId_ReturnsErrorAndStaysOpen()
    {
        var fixture = new WebAppFixture().ConfigureWebHost(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FeatureStreaming:MaxSubscriptionIdLength"] = "16"
                });
            });
        });

        await fixture.InitializeAsync();
        try
        {
            var wsClient = fixture.CreateWebSocketClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            using var ws = await wsClient.ConnectAsync(
                new Uri("ws://localhost/api/v1/streaming/features?clientLabel=long-id-test"),
                cts.Token);

            _ = await ReceiveWebSocketJsonAsync(ws, cts.Token);

            var longId = new string('a', 64);
            await SendWebSocketJsonAsync(ws, $$"""{"type":"subscribe","subscriptionId":"{{longId}}","layerId":0}""", cts.Token);
            var error = await ReceiveWebSocketJsonAsync(ws, cts.Token);
            error.GetProperty("type").GetString().Should().Be("error");
            error.GetProperty("code").GetString().Should().Be("invalid-subscription-id");

            // Connection stays open; a sanely-sized id still works.
            await SendWebSocketJsonAsync(ws, """{"type":"subscribe","subscriptionId":"ok","layerId":0}""", cts.Token);
            (await ReceiveWebSocketJsonAsync(ws, cts.Token)).GetProperty("status").GetString().Should().Be("subscribed");

            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    // Regression for review finding "WebSocket writer drops same-event frames
    // for additional subscriptions". When two subscriptions on a single
    // WebSocket both match the same event, both frames must arrive on the
    // wire — they share a cursor but are distinct (eventId, subscriptionId)
    // deliveries. The previous session-wide cursor watermark dropped the
    // second frame.
    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task WebSocket_TwoSubscriptionsMatchingSameEvent_DeliversBothFrames()
    {
        var wsClient = _fixture.CreateWebSocketClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var ws = await wsClient.ConnectAsync(
            new Uri("ws://localhost/api/v1/streaming/features?clientLabel=multi-sub-test"),
            cts.Token);

        // Drain the connect status frame.
        _ = await ReceiveWebSocketJsonAsync(ws, cts.Token);

        // Subscribe twice to the same layer with two distinct subscription ids.
        await SendWebSocketJsonAsync(ws, """{"type":"subscribe","subscriptionId":"sub-a","layerId":0}""", cts.Token);
        var subscribedA = await ReceiveWebSocketJsonAsync(ws, cts.Token);
        subscribedA.GetProperty("status").GetString().Should().Be("subscribed");

        await SendWebSocketJsonAsync(ws, """{"type":"subscribe","subscriptionId":"sub-b","layerId":0}""", cts.Token);
        var subscribedB = await ReceiveWebSocketJsonAsync(ws, cts.Token);
        subscribedB.GetProperty("status").GetString().Should().Be("subscribed");

        // Publish one event matching both subscriptions.
        var publisher = _fixture.GetService<IFeatureChangeEventPublisher>();
        var serviceId = $"multi-sub-{Guid.NewGuid():N}";
        await publisher.PublishAsync(new FeatureChangeEventRequest
        {
            ServiceId = serviceId,
            LayerId = 0,
            ObjectId = 1,
            Operation = "insert",
            Protocol = "rest",
            RequestId = "req-multi-sub"
        });

        // Expect a feature-change frame per matching subscription on this
        // session: default (no filter), sub-a, and sub-b. Pre-fix, the writer
        // task's session-wide cursor watermark dropped the second and third
        // frames because they shared a cursor with the first.
        var deliveredSubscriptions = new HashSet<string>(StringComparer.Ordinal);
        var deliveredEventId = (string?)null;
        while (!deliveredSubscriptions.Contains("sub-a") || !deliveredSubscriptions.Contains("sub-b"))
        {
            cts.Token.ThrowIfCancellationRequested();
            var frame = await ReceiveWebSocketJsonAsync(ws, cts.Token);
            if (frame.GetProperty("type").GetString() != "feature-change")
            {
                continue;
            }

            if (frame.GetProperty("serviceId").GetString() != serviceId)
            {
                continue;
            }

            deliveredEventId ??= frame.GetProperty("eventId").GetString();
            deliveredSubscriptions.Add(frame.GetProperty("subscriptionId").GetString()!);
        }

        deliveredSubscriptions.Should().Contain("sub-a").And.Contain("sub-b");
        deliveredEventId.Should().NotBeNull();

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
    }

    // Regression for review finding "Queued WebSocket frames survive subscription
    // removal or replacement". After unsubscribe, frames that the broadcast had
    // already queued for that subscription must not reach the wire — the writer
    // drain rejects them on the per-(event, subscription) generation check before
    // claiming dedup, so a future subscribe with the same id would also see
    // those events fresh from a replay (not pre-claimed and silently dropped).
    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task WebSocket_QueuedFrames_AfterUnsubscribe_AreNotDelivered()
    {
        var wsClient = _fixture.CreateWebSocketClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var ws = await wsClient.ConnectAsync(
            new Uri("ws://localhost/api/v1/streaming/features?clientLabel=stale-after-unsub"),
            cts.Token);

        // Drain the connect status frame.
        _ = await ReceiveWebSocketJsonAsync(ws, cts.Token);

        await SendWebSocketJsonAsync(ws, """{"type":"subscribe","subscriptionId":"sub-fence","layerId":0}""", cts.Token);
        (await ReceiveWebSocketJsonAsync(ws, cts.Token)).GetProperty("status").GetString().Should().Be("subscribed");

        // Unsubscribe before any matching events fire so the dedup state for
        // sub-fence is still empty, but the subscription's generation has
        // already been pinned by the live broadcaster's match snapshot.
        await SendWebSocketJsonAsync(ws, """{"type":"unsubscribe","subscriptionId":"sub-fence"}""", cts.Token);
        (await ReceiveWebSocketJsonAsync(ws, cts.Token)).GetProperty("status").GetString().Should().Be("unsubscribed");

        // Publish an event that would have matched sub-fence. Even if the
        // broadcast had raced ahead and queued a frame for sub-fence (the
        // unsubscribe and broadcast are not strongly ordered cross-thread),
        // the writer must drop any queued sub-fence frame as stale-generation.
        var publisher = _fixture.GetService<IFeatureChangeEventPublisher>();
        var serviceId = $"stale-fence-{Guid.NewGuid():N}";
        await publisher.PublishAsync(new FeatureChangeEventRequest
        {
            ServiceId = serviceId,
            LayerId = 0,
            ObjectId = 1,
            Operation = "insert",
            Protocol = "rest",
            RequestId = "req-stale-fence"
        });

        // The default subscription is still active and unfiltered, so the event
        // is delivered under the default subscriptionId. Receive frames until
        // we see the default delivery for our serviceId; assert no frame for
        // the unsubscribed sub-fence ever arrives.
        var sawDefault = false;
        var sawSubFence = false;
        using var observeCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        observeCts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            while (!sawDefault)
            {
                var frame = await ReceiveWebSocketJsonAsync(ws, observeCts.Token);
                if (frame.GetProperty("type").GetString() != "feature-change")
                {
                    continue;
                }

                if (frame.TryGetProperty("serviceId", out var svc) && svc.GetString() != serviceId)
                {
                    continue;
                }

                var subId = frame.GetProperty("subscriptionId").GetString();
                if (subId == "sub-fence")
                {
                    sawSubFence = true;
                }
                else if (subId == FeatureStreamSessionManager.DefaultSubscriptionId)
                {
                    sawDefault = true;
                }
            }

            // Give the writer a brief window to drain any stale-generation frame
            // (it should be dropped before send).
            using var grace = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            grace.CancelAfter(TimeSpan.FromMilliseconds(500));
            try
            {
                while (true)
                {
                    var frame = await ReceiveWebSocketJsonAsync(ws, grace.Token);
                    if (frame.GetProperty("type").GetString() != "feature-change")
                    {
                        continue;
                    }

                    if (!frame.TryGetProperty("serviceId", out var svc) || svc.GetString() != serviceId)
                    {
                        continue;
                    }

                    if (frame.GetProperty("subscriptionId").GetString() == "sub-fence")
                    {
                        sawSubFence = true;
                    }
                }
            }
            catch (OperationCanceledException) when (grace.Token.IsCancellationRequested)
            {
                // Expected — no further frames within the grace window.
            }
        }
        catch (OperationCanceledException) when (observeCts.Token.IsCancellationRequested)
        {
            // Expected — let the assertions below report the actual state.
        }

        sawDefault.Should().BeTrue("the default subscription must still receive the event");
        sawSubFence.Should().BeFalse("frames queued after unsubscribe must be fenced as stale-generation");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
    }

    // Regression for review finding "Client can replace the reserved default
    // subscription and break durable polling". The default subscription is
    // server-managed and the WebSocket writer pins its generation at session
    // setup; replacing it from the control frame would silently strand every
    // cross-node poll under the old generation.
    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task WebSocket_SubscribeWithReservedDefaultId_ReturnsErrorAndStaysOpen()
    {
        var wsClient = _fixture.CreateWebSocketClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using var ws = await wsClient.ConnectAsync(
            new Uri("ws://localhost/api/v1/streaming/features?clientLabel=reserved-default-sub"),
            cts.Token);

        _ = await ReceiveWebSocketJsonAsync(ws, cts.Token);

        // Exact reserved id is rejected with the canonical invalid-subscription-id code.
        await SendWebSocketJsonAsync(
            ws,
            $$"""{"type":"subscribe","subscriptionId":"{{FeatureStreamSessionManager.DefaultSubscriptionId}}","layerId":0}""",
            cts.Token);
        var error = await ReceiveWebSocketJsonAsync(ws, cts.Token);
        error.GetProperty("type").GetString().Should().Be("error");
        error.GetProperty("code").GetString().Should().Be("invalid-subscription-id");
        error.GetProperty("message").GetString().Should().Contain("reserved");

        // Case-insensitive guard: capitalisation variants of the reserved id
        // would create a sibling that looks reserved; reject them too for clarity.
        await SendWebSocketJsonAsync(ws, """{"type":"subscribe","subscriptionId":"DEFAULT","layerId":0}""", cts.Token);
        var caseError = await ReceiveWebSocketJsonAsync(ws, cts.Token);
        caseError.GetProperty("code").GetString().Should().Be("invalid-subscription-id");

        // Connection stays open and a non-reserved id still works.
        await SendWebSocketJsonAsync(ws, """{"type":"subscribe","subscriptionId":"client-sub","layerId":0}""", cts.Token);
        var subscribed = await ReceiveWebSocketJsonAsync(ws, cts.Token);
        subscribed.GetProperty("status").GetString().Should().Be("subscribed");
        subscribed.GetProperty("subscriptionId").GetString().Should().Be("client-sub");

        // The server-managed default subscription must still be alive — its
        // generation is what onPoll uses, and it must be unchanged across the
        // rejected control frames.
        var sessionManager = _fixture.GetService<FeatureStreamSessionManager>();
        await WaitForSessionAsync(sessionManager, "reserved-default-sub", cts.Token);
        var sessionInfo = sessionManager.GetSessions().Single(s => s.ClientLabel == "reserved-default-sub");
        sessionManager.GetDefaultSubscriptionGeneration(sessionInfo.SessionId).Should().BeGreaterThan(0);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
    }

    // Regression companion: unsubscribe of the reserved id must be rejected
    // with the same invariant. Without this guard the writer's pinned default-
    // subscription generation would be stranded — every onPoll claim would hit
    // SubscriptionDeliveryClaim.StaleGeneration and silently drop the event.
    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task WebSocket_UnsubscribeReservedDefaultId_ReturnsErrorAndPreservesPolling()
    {
        var wsClient = _fixture.CreateWebSocketClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using var ws = await wsClient.ConnectAsync(
            new Uri("ws://localhost/api/v1/streaming/features?clientLabel=reserved-default-unsub"),
            cts.Token);

        _ = await ReceiveWebSocketJsonAsync(ws, cts.Token);

        await SendWebSocketJsonAsync(
            ws,
            $$"""{"type":"unsubscribe","subscriptionId":"{{FeatureStreamSessionManager.DefaultSubscriptionId}}"}""",
            cts.Token);
        var error = await ReceiveWebSocketJsonAsync(ws, cts.Token);
        error.GetProperty("type").GetString().Should().Be("error");
        error.GetProperty("code").GetString().Should().Be("invalid-unsubscribe");
        error.GetProperty("message").GetString().Should().Contain("reserved");

        // Default subscription is still present after the rejection.
        var sessionManager = _fixture.GetService<FeatureStreamSessionManager>();
        await WaitForSessionAsync(sessionManager, "reserved-default-unsub", cts.Token);
        var sessionInfo = sessionManager.GetSessions().Single(s => s.ClientLabel == "reserved-default-unsub");
        sessionManager.GetDefaultSubscriptionGeneration(sessionInfo.SessionId).Should().BeGreaterThan(0);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
    }

    // Regression for review finding "High-volume WebSocket replay handoff can
    // duplicate the first queued default frame". The WebSocket writer drain
    // must apply a default-subscription cursor fence the same way SSE does:
    // queued default-subscription frames whose envelope cursor is at or below
    // the writer's running replayCursor are duplicates already delivered, and
    // the recent-event LRU (RecentEventIdCapacity = 128) cannot be relied on
    // alone to dedupe across large replay windows.
    //
    // This test broadcasts a synthetic envelope with a fresh eventId and a
    // cursor that is provably <= the writer's replayCursor. Without the fence,
    // dedup alone would let it through (the eventId was never claimed). With
    // the fence, the writer drops it before the dedup claim.
    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task WebSocket_DefaultSubscription_DoesNotDuplicateStaleQueuedFrameAfterReplay()
    {
        var wsClient = _fixture.CreateWebSocketClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var publisher = _fixture.GetService<IFeatureChangeEventPublisher>();
        var eventStore = _fixture.GetService<IFeatureChangeEventStore>();
        var sessionManager = _fixture.GetService<FeatureStreamSessionManager>();

        // Publish one event so the replay path advances the writer's replayCursor.
        var serviceId = $"fence-cursor-{Guid.NewGuid():N}";
        await publisher.PublishAsync(new FeatureChangeEventRequest
        {
            ServiceId = serviceId,
            LayerId = 0,
            ObjectId = 1,
            Operation = "insert",
            Protocol = "rest",
            RequestId = "req-fence-cursor-replay"
        });

        var stored = (await eventStore.QueryAsync(null, null, null, 500))
            .Single(e => e.ServiceId == serviceId);

        using var ws = await wsClient.ConnectAsync(
            new Uri($"ws://localhost/api/v1/streaming/features?clientLabel=fence-cursor&cursor={stored.Cursor - 1}"),
            cts.Token);

        // Drain handshake plus the replayed event to confirm replayCursor is past stored.Cursor.
        var sawReplayedEvent = false;
        while (!sawReplayedEvent)
        {
            cts.Token.ThrowIfCancellationRequested();
            var frame = await ReceiveWebSocketJsonAsync(ws, cts.Token);
            if (frame.GetProperty("type").GetString() != "feature-change")
            {
                continue;
            }

            if (frame.TryGetProperty("serviceId", out var svc) && svc.GetString() == serviceId)
            {
                sawReplayedEvent = true;
            }
        }

        // Locate the session we connected with so we can stage a synthetic broadcast.
        await WaitForSessionAsync(sessionManager, "fence-cursor", cts.Token);
        var sessionInfo = sessionManager.GetSessions().Single(s => s.ClientLabel == "fence-cursor");
        sessionInfo.Transport.Should().Be("WebSocket");

        // Synthetic envelope: cursor 1 (<= replayCursor), unique eventId never
        // recorded in the per-session dedup LRU. The default subscription has
        // no filter so the broadcast will queue this frame.
        var staleEventId = $"fence-stale-{Guid.NewGuid():N}";
        var staleEnvelope = new FeatureStreamEnvelope
        {
            EventId = staleEventId,
            Cursor = 1,
            Timestamp = DateTimeOffset.UtcNow,
            ServiceId = serviceId,
            LayerId = 0,
            ObjectId = 9999,
            Operation = "insert",
            Protocol = "rest",
            RequestId = "req-fence-stale"
        };
        sessionManager.Broadcast(FeatureStreamMessage.Data(staleEnvelope));

        // The writer must drop the synthetic envelope at the cursor fence —
        // wait briefly and confirm no feature-change frame for the stale id arrives.
        var sawStale = false;
        using var grace = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        grace.CancelAfter(TimeSpan.FromMilliseconds(750));
        try
        {
            while (true)
            {
                var frame = await ReceiveWebSocketJsonAsync(ws, grace.Token);
                if (frame.GetProperty("type").GetString() != "feature-change")
                {
                    continue;
                }

                if (frame.GetProperty("eventId").GetString() == staleEventId)
                {
                    sawStale = true;
                }
            }
        }
        catch (OperationCanceledException) when (grace.Token.IsCancellationRequested)
        {
            // Expected — no further frames in the grace window.
        }

        sawStale.Should().BeFalse(
            "the default-subscription cursor fence must drop queued frames whose cursor is at or below replayCursor");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Stream_WithoutUpgradeOrSseHeader_Returns400()
    {
        var response = await _client.GetAsync("/api/v1/streaming/features");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Stream_WithoutAuth_ReturnsUnauthorized()
    {
        var fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", "test-stream-admin-key");
            });

        await fixture.InitializeAsync();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/streaming/features?cursor=1");
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

            using var response = await fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Stream_CommunityEdition_ReturnsForbidden()
    {
        var fixture = new WebAppFixture()
            .ReplaceService<ILicenseStatusProvider>(new TestLicenseStatusProvider(HonuaEdition.Community));

        await fixture.InitializeAsync();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/streaming/features?layers=0");
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

            using var response = await fixture.CreateAdminClient().SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    // Regression for review finding "legacy layerIds stream subscriptions
    // bypass layer access checks". The legacy `?layerIds=...` alias and the
    // canonical `?layers=...` parameter must run through the same parser/
    // authorizer helper, so the same unknown-layer rejection (a side-effect
    // of the existence check) applies to both. Pre-fix, the legacy branch
    // skipped existence/access entirely and accepted unknown ids silently.
    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Stream_WithLegacyLayerIdsParam_AndUnknownLayer_ReturnsBadRequest()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/streaming/features?layerIds=99999");
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().Contain("Layer 99999 not found");
    }

    // Companion check: `?layers=...` must produce the same unknown-layer
    // outcome as `?layerIds=...`, confirming the unified helper.
    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Stream_WithLayersParam_AndUnknownLayer_ReturnsBadRequest()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/streaming/features?layers=99999");
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().Contain("Layer 99999 not found");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Stream_WithUnsupportedFunctionFilter_ReturnsBadRequest()
    {
        var encodedFilter = Uri.EscapeDataString("UPPER(name) = 'ALICE'");
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/streaming/features?layers=0&filter={encodedFilter}");
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().Contain("function calls");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Stream_WithUnsupportedFilterLanguage_ReturnsBadRequest()
    {
        var encodedFilter = Uri.EscapeDataString("name = 'alpha'");
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/streaming/features?layers=0&filter={encodedFilter}&filter-lang=unsupported");
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().Contain("Unsupported filter language");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Stream_WithBboxWithoutSingleLayer_ReturnsBadRequest()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/streaming/features?bbox=-122.5,37.5,-122.0,38.0");
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().Contain("exactly one layer");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Stream_WithProjectedBboxLayer_AllowsSseHandshake()
    {
        var fixture = new WebAppFixture()
            .ReplaceService<ILayerCatalog>(new SpatialReferenceTestLayerCatalog());

        await fixture.InitializeAsync();

        try
        {
            var client = fixture.CreateAdminClient();
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"/api/v1/streaming/features?layers={SpatialReferenceTestLayerCatalog.PointLayerId}&bbox=-122.5,37.5,-122.0,38.0");
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Stream_WithUnsupportedPolygonFilter_ReturnsBadRequest()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/streaming/features?layers=0&intersects=1");
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().Contain("polygonIntersects");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Stream_WithTemporalFilterOnNonTimeAwareLayer_ReturnsBadRequest()
    {
        var fixture = new WebAppFixture()
            .ReplaceService<ILayerCatalog>(new TestLayerCatalog());

        await fixture.InitializeAsync();

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                "/api/v1/streaming/features?layers=0&datetime=2023-01-01T00:00:00Z/2023-02-01T00:00:00Z");
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

            using var response = await fixture.CreateAdminClient().SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            body.Should().Contain("not time-aware");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Stream_WithTemporalFilterOnTimeAwareLayer_AllowsSseHandshake()
    {
        var fixture = new WebAppFixture()
            .ReplaceService<ILayerCatalog>(new TimeAwareStreamLayerCatalog());

        await fixture.InitializeAsync();

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                "/api/v1/streaming/features?layers=0&datetime=2023-01-01T00:00:00Z/2023-02-01T00:00:00Z");
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var response = await fixture.CreateAdminClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    // ─── AC: Client can open an SSE connection ──────────────────────────

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_Connect_ReturnsEventStreamContentType()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/streaming/features");
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        // ResponseHeadersRead completes as soon as headers arrive; the 5-second
        // timeout should only fire if the SSE handshake never completes.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_Connect_ReceivesStatusEvent()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/streaming/features?layers=0");
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var status = await ReadNextSseEventAsync(reader, cts.Token);

        status.EventName.Should().Be("status");
        status.Data.GetProperty("type").GetString().Should().Be("status");
        status.Data.GetProperty("status").GetString().Should().Be("connected");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_ReplayEnvelope_IncludesFullSdkContractFields()
    {
        var publisher = _fixture.GetService<IFeatureChangeEventPublisher>();
        var eventStore = _fixture.GetService<IFeatureChangeEventStore>();
        var serviceId = $"shape-{Guid.NewGuid():N}";

        await publisher.PublishAsync(new FeatureChangeEventRequest
        {
            SourceId = "postgres-cdc",
            ServiceId = serviceId,
            LayerId = 0,
            ObjectId = 4321,
            Operation = "create",
            Protocol = "rest",
            RequestId = "req-shape",
            GeometryJson = """{"type":"Point","coordinates":[-157.8583,21.3069]}""",
            GeometrySrid = 4326,
            GeometryEnvelope = [-157.8583d, 21.3069d, -157.8583d, 21.3069d],
            PropertiesJson = """{"name":"Honolulu","status":"active"}"""
        });

        var stored = (await eventStore.QueryAsync(null, null, null, 500))
            .Single(e => e.ServiceId == serviceId);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/streaming/features?layers=0&cursor={stored.Cursor - 1}");
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        JsonElement envelope = default;
        while (!cts.Token.IsCancellationRequested)
        {
            var evt = await ReadNextSseEventAsync(reader, cts.Token);
            if (evt.EventName != "feature-change" ||
                evt.Data.GetProperty("serviceId").GetString() != serviceId)
            {
                continue;
            }

            envelope = evt.Data.Clone();
            break;
        }

        envelope.ValueKind.Should().Be(JsonValueKind.Object);
        envelope.GetProperty("type").GetString().Should().Be("feature-change");
        envelope.GetProperty("sourceId").GetString().Should().Be("postgres-cdc");
        envelope.GetProperty("serviceId").GetString().Should().Be(serviceId);
        envelope.GetProperty("layerId").GetInt32().Should().Be(0);
        envelope.GetProperty("featureId").GetString().Should().Be("4321");
        envelope.GetProperty("objectId").GetInt64().Should().Be(4321);
        envelope.GetProperty("operation").GetString().Should().Be("insert");
        envelope.GetProperty("cursor").GetInt64().Should().Be(stored.Cursor);
        envelope.GetProperty("timestamp").GetDateTimeOffset().Should().BeCloseTo(stored.Timestamp, TimeSpan.FromSeconds(1));
        envelope.GetProperty("geometry").GetProperty("type").GetString().Should().Be("Point");
        envelope.GetProperty("geometryCrs").GetString().Should().Be("EPSG:4326");
        envelope.GetProperty("attributes").GetProperty("name").GetString().Should().Be("Honolulu");
        envelope.GetProperty("subscriptionId").GetString().Should().Be("default");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features/capabilities")]
    public async Task Capabilities_ProEdition_AdvertisesTransportsFiltersAndLayers()
    {
        using var response = await _client.GetAsync("/api/v1/streaming/features/capabilities");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("enabled").GetBoolean().Should().BeTrue();
        data.GetProperty("minimumEdition").GetString().Should().Be("Pro");
        data.GetProperty("transports").EnumerateArray().Select(item => item.GetString())
            .Should().Contain(["websocket", "sse"]);
        data.GetProperty("filterFamilies").EnumerateArray().Select(item => item.GetString())
            .Should().Contain(["layer", "bbox", "attribute", "temporal"]);
        data.GetProperty("replaySupported").GetBoolean().Should().BeTrue();
        data.GetProperty("layers").GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features/capabilities")]
    public async Task Capabilities_CommunityEdition_DisablesStreamingMetadata()
    {
        var fixture = new WebAppFixture()
            .ReplaceService<ILicenseStatusProvider>(new TestLicenseStatusProvider(HonuaEdition.Community));

        await fixture.InitializeAsync();

        try
        {
            using var response = await fixture.CreateAdminClient().GetAsync("/api/v1/streaming/features/capabilities");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var data = doc.RootElement.GetProperty("data");
            data.GetProperty("enabled").GetBoolean().Should().BeFalse();
            data.GetProperty("edition").GetString().Should().Be("Community");
            data.GetProperty("transports").GetArrayLength().Should().Be(0);
            data.GetProperty("filterFamilies").GetArrayLength().Should().Be(0);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    // Regression for review finding "Streaming access checks bypass
    // service-level policies". A layer in a service with a role-gated policy
    // must not appear in capabilities even when the layer itself has no
    // layer-level policy — the service policy is the only gate. Pre-fix the
    // capabilities filter only consulted the layer policy.
    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features/capabilities")]
    public async Task Capabilities_OmitsLayersWhoseServicePolicyDenies()
    {
        var fixture = new WebAppFixture()
            .ReplaceService<ILayerCatalog>(new ServiceLevelAccessStreamLayerCatalog());

        await fixture.InitializeAsync();

        try
        {
            using var response = await fixture.CreateAdminClient().GetAsync("/api/v1/streaming/features/capabilities");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var data = doc.RootElement.GetProperty("data");
            var layers = data.GetProperty("layers").EnumerateArray().ToList();

            layers.Should().HaveCount(1);
            layers[0].GetProperty("layerId").GetInt32().Should().Be(ServiceLevelAccessStreamLayerCatalog.OpenLayerId);

            var body = await response.Content.ReadAsStringAsync();
            body.Should().NotContain("Layer In Restricted Service");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    // Regression for the same finding's other half: when a serviceId is
    // supplied, layer ids must be members of that service. Pre-fix the code
    // fell back to a global GetLayerAsync, letting callers attach layers
    // that don't belong to the named service.
    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Stream_WithServiceIdAndForeignLayer_ReturnsBadRequest()
    {
        var fixture = new WebAppFixture()
            .ReplaceService<ILayerCatalog>(new ServiceLevelAccessStreamLayerCatalog());

        await fixture.InitializeAsync();

        try
        {
            // Layer 9 is in restricted-svc; specifying serviceId=open-svc must reject.
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"/api/v1/streaming/features?serviceId=open-svc&layers={ServiceLevelAccessStreamLayerCatalog.RestrictedServiceLayerId}");
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

            using var response = await fixture.CreateAdminClient().SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            // Apostrophe is escaped as ' in JSON so match the unambiguous prefix.
            body.Should().Contain($"Layer {ServiceLevelAccessStreamLayerCatalog.RestrictedServiceLayerId} is not part of service");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    // Regression for review finding "Streaming capabilities expose
    // inaccessible layer metadata". The discovery endpoint must omit layers
    // the caller cannot read; otherwise restricted layer names, CRS, or
    // time-aware status leak through the anonymous-readable capabilities.
    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features/capabilities")]
    public async Task Capabilities_OmitsLayersTheCallerCannotAccess()
    {
        var fixture = new WebAppFixture()
            .ReplaceService<ILayerCatalog>(new MixedAccessStreamLayerCatalog());

        await fixture.InitializeAsync();

        try
        {
            // Even with admin auth (dev bypass), the restricted layer requires
            // role "restricted-stream-reader" which admin does not hold, so
            // the capabilities response must filter it out entirely.
            using var response = await fixture.CreateAdminClient().GetAsync("/api/v1/streaming/features/capabilities");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var data = doc.RootElement.GetProperty("data");
            var layers = data.GetProperty("layers").EnumerateArray().ToList();

            layers.Should().HaveCount(1);
            layers[0].GetProperty("layerId").GetInt32().Should().Be(MixedAccessStreamLayerCatalog.VisibleLayerId);
            layers[0].GetProperty("name").GetString().Should().Be("Public Stream Layer");

            var body = await response.Content.ReadAsStringAsync();
            body.Should().NotContain("Restricted Stream Layer");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_WhenSessionLimitReached_ReturnsServiceUnavailable()
    {
        var fixture = CreateLimitedStreamingFixture(1);
        await fixture.InitializeAsync();

        try
        {
            var sessionManager = fixture.GetService<FeatureStreamSessionManager>();
            using var heldSession = sessionManager.CreateSession("WebSocket", "held-session");

            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/streaming/features");
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

            using var response = await fixture.CreateAdminClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("session limit");
            sessionManager.SessionCount.Should().Be(1);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task WebSocket_WhenSessionLimitReached_ReturnsServiceUnavailable()
    {
        var fixture = CreateLimitedStreamingFixture(1);
        await fixture.InitializeAsync();

        try
        {
            var sessionManager = fixture.GetService<FeatureStreamSessionManager>();
            using var heldSession = sessionManager.CreateSession("WebSocket", "held-session");

            var wsClient = fixture.CreateWebSocketClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                wsClient.ConnectAsync(new Uri("ws://localhost/api/v1/streaming/features?clientLabel=ws-limit"), cts.Token));

            ex.Message.Should().Contain("503");
            sessionManager.SessionCount.Should().Be(1);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies that a fresh SSE connection (no cursor) delivers the first live
    /// event without dropping or replaying the entire store.
    /// </summary>
    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_FreshConnect_DeliversFirstLiveEvent()
    {
        var publisher = _fixture.GetService<IFeatureChangeEventPublisher>();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var serviceId = $"fresh-{uniqueId}";

        // Open SSE connection without a cursor (pure live stream).
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/streaming/features");
        request.Headers.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Publish a live event while connected.
        await publisher.PublishAsync(new FeatureChangeEventRequest
        {
            ServiceId = serviceId,
            LayerId = 0,
            ObjectId = 1,
            Operation = "create",
            Protocol = "rest",
            RequestId = $"req-{uniqueId}"
        });

        // Read SSE stream and verify the event arrives.
        using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        long? receivedCursor = null;
        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cts.Token);
                if (line is null)
                {
                    break;
                }

                if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    var json = line["data: ".Length..];
                    using var doc = JsonDocument.Parse(json);
                    if (!IsFeatureChangeFrame(doc.RootElement))
                    {
                        continue;
                    }

                    var sid = doc.RootElement.GetProperty("serviceId").GetString();
                    if (sid == serviceId)
                    {
                        receivedCursor = doc.RootElement.GetProperty("cursor").GetInt64();
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout — assert below.
        }

        receivedCursor.Should().NotBeNull("the first live event should be delivered on a fresh connection");
    }

    // ─── AC: Server sends heartbeat frames ──────────────────────────────

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Heartbeat_BroadcastsToConnectedSessions()
    {
        var sessionManager = _fixture.GetService<FeatureStreamSessionManager>();
        using var session = sessionManager.CreateSession("WebSocket", "hb-test");

        sessionManager.BroadcastHeartbeat();

        session.Reader.TryRead(out var msg).Should().BeTrue();
        msg.IsHeartbeat.Should().BeTrue();
        sessionManager.HeartbeatsSent.Should().BeGreaterOrEqualTo(1);
    }

    // ─── AC: Slow consumers are disconnected after buffer limit ─────────

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task SlowConsumer_BufferOverflow_DisconnectsSession()
    {
        var sessionManager = _fixture.GetService<FeatureStreamSessionManager>();
        using var session = sessionManager.CreateSession("WebSocket", "slow-test");
        sessionManager.MarkDrainStarted(session.SessionId);

        // MaxBufferPerConnection defaults to 256. Fill the buffer then overflow it.
        // With drain active, TryWrite failure triggers slow-consumer disconnect.
        for (var i = 0; i < 300; i++)
        {
            sessionManager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope(cursor: i)));
        }

        // Session should be disconnected as a slow consumer.
        sessionManager.GetSessions().Should().NotContain(s => s.ClientLabel == "slow-test");
        session.DisconnectToken.IsCancellationRequested.Should().BeTrue();
        sessionManager.SlowConsumerDrops.Should().BeGreaterOrEqualTo(1);
    }

    // ─── AC: Reconnect with cursor replays missed events ────────────────

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Reconnect_WithCursor_ReplaysMissedEvents()
    {
        var publisher = _fixture.GetService<IFeatureChangeEventPublisher>();
        var eventStore = _fixture.GetService<IFeatureChangeEventStore>();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var serviceId = $"replay-{uniqueId}";

        // Publish 5 events with a unique service ID to isolate from other tests.
        for (var i = 0; i < 5; i++)
        {
            await publisher.PublishAsync(new FeatureChangeEventRequest
            {
                ServiceId = serviceId,
                LayerId = 0,
                ObjectId = i,
                Operation = "create",
                Protocol = "rest",
                RequestId = $"req-{uniqueId}-{i}"
            });
        }

        // Query all events and filter to this test's by service ID to get exact cursors.
        var allEvents = await eventStore.QueryAsync(null, null, null, 500);
        var ownEvents = allEvents.Where(e => e.ServiceId == serviceId).ToList();
        ownEvents.Should().HaveCount(5);

        // Replay from the 2nd event's cursor — should return events 3, 4, 5.
        var replayCursor = ownEvents[1].Cursor;
        var expectedCursors = ownEvents.Skip(2).Select(e => e.Cursor).ToList();

        // Open an actual SSE connection with the cursor to exercise the real replay path.
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/streaming/features?cursor={replayCursor}");
        request.Headers.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");

        // Read SSE frames from the response stream until we have collected
        // enough replayed events or the timeout fires.
        var replayedCursors = new List<long>();
        using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cts.Token);
                if (line is null)
                {
                    break;
                }

                // SSE data lines carry the JSON envelope.
                if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    var json = line["data: ".Length..];
                    using var doc = JsonDocument.Parse(json);
                    if (!IsFeatureChangeFrame(doc.RootElement))
                    {
                        continue;
                    }

                    var cur = doc.RootElement.GetProperty("cursor").GetInt64();

                    // Only count events from this test's service.
                    var sid = doc.RootElement.GetProperty("serviceId").GetString();
                    if (sid == serviceId)
                    {
                        replayedCursors.Add(cur);
                    }

                    if (replayedCursors.Count >= expectedCursors.Count)
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout — assert what we collected so far.
        }

        replayedCursors.Should().Equal(expectedCursors,
            "SSE replay should deliver exactly the events after the provided cursor");
    }

    /// <summary>
    /// Validates the full replay-to-live path: connects with a cursor, publishes
    /// additional events while connected, and asserts all events arrive exactly once.
    /// Note: the overlap timing between replay and live publish is best-effort;
    /// the test primarily guards against regression in the handoff path.
    /// </summary>
    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Reconnect_WithLiveEventsPublishedDuringReplay_DeliversAll()
    {
        var publisher = _fixture.GetService<IFeatureChangeEventPublisher>();
        var eventStore = _fixture.GetService<IFeatureChangeEventStore>();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var serviceId = $"overlap-{uniqueId}";

        // Publish 5 initial events.
        for (var i = 0; i < 5; i++)
        {
            await publisher.PublishAsync(new FeatureChangeEventRequest
            {
                ServiceId = serviceId,
                LayerId = 0,
                ObjectId = i,
                Operation = "create",
                Protocol = "rest",
                RequestId = $"req-{uniqueId}-{i}"
            });
        }

        // Get cursor from event 2 — events 3, 4, 5 will be replayed.
        var allEvents = await eventStore.QueryAsync(null, null, null, 500);
        var ownEvents = allEvents.Where(e => e.ServiceId == serviceId).ToList();
        ownEvents.Should().HaveCount(5);
        var replayCursor = ownEvents[1].Cursor;

        // Open SSE connection with cursor.
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/streaming/features?cursor={replayCursor}");
        request.Headers.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Publish 3 more live events while the stream is connected.
        for (var i = 5; i < 8; i++)
        {
            await publisher.PublishAsync(new FeatureChangeEventRequest
            {
                ServiceId = serviceId,
                LayerId = 0,
                ObjectId = i,
                Operation = "update",
                Protocol = "rest",
                RequestId = $"req-{uniqueId}-{i}"
            });
        }

        // Re-query to get all 8 events' cursors. Expected: events 3-8 (skip 1, 2).
        var updatedEvents = await eventStore.QueryAsync(null, null, null, 500);
        var allOwnEvents = updatedEvents.Where(e => e.ServiceId == serviceId).ToList();
        allOwnEvents.Should().HaveCount(8);
        var expectedCursors = allOwnEvents.Skip(2).Select(e => e.Cursor).ToList();

        // Read SSE stream and collect events from this test.
        var receivedCursors = new List<long>();
        using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cts.Token);
                if (line is null)
                {
                    break;
                }

                if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    var json = line["data: ".Length..];
                    using var doc = JsonDocument.Parse(json);
                    if (!IsFeatureChangeFrame(doc.RootElement))
                    {
                        continue;
                    }

                    var cur = doc.RootElement.GetProperty("cursor").GetInt64();
                    var sid = doc.RootElement.GetProperty("serviceId").GetString();
                    if (sid == serviceId)
                    {
                        receivedCursors.Add(cur);
                    }

                    if (receivedCursors.Count >= expectedCursors.Count)
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout — assert what we collected so far.
        }

        receivedCursors.Should().OnlyHaveUniqueItems("each event should be delivered exactly once");
        receivedCursors.Should().Equal(expectedCursors,
            "replay events 3-5 and live events 6-8 should all be delivered in cursor order");
    }

    // ─── AC: Connection count visible in admin/health endpoints ─────────

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/streaming/features/sessions")]
    public async Task ListSessions_WhenNone_ReturnsEmptyList()
    {
        var response = await _client.GetAsync("/api/v1/admin/streaming/features/sessions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();

        var data = root.GetProperty("data");
        data.GetProperty("activeSessions").GetInt32().Should().Be(0);
        data.GetProperty("sessions").GetArrayLength().Should().Be(0);
        data.TryGetProperty("generatedAt", out _).Should().BeTrue();
        data.TryGetProperty("slowConsumerDrops", out _).Should().BeTrue();
        data.TryGetProperty("heartbeatsSent", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/streaming/features/sessions")]
    public async Task ListSessions_AfterConnect_ReturnsSessionInfo()
    {
        var sessionManager = _fixture.GetService<FeatureStreamSessionManager>();
        using var session = sessionManager.CreateSession("WebSocket", "admin-vis-test");

        var response = await _client.GetAsync("/api/v1/admin/streaming/features/sessions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("activeSessions").GetInt32().Should().BeGreaterOrEqualTo(1);
        data.GetProperty("webSocketSessions").GetInt32().Should().BeGreaterOrEqualTo(1);

        var sessions = data.GetProperty("sessions");
        sessions.GetArrayLength().Should().BeGreaterOrEqualTo(1);

        var sessionData = sessions.EnumerateArray()
            .First(s => s.GetProperty("clientLabel").GetString() == "admin-vis-test");
        sessionData.GetProperty("transport").GetString().Should().Be("WebSocket");
        sessionData.GetProperty("durationSeconds").GetDouble().Should().BeGreaterOrEqualTo(0);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/streaming/features/sessions")]
    public async Task ListSessions_WithFilter_ReturnsSessionFilterInfo()
    {
        var sessionManager = _fixture.GetService<FeatureStreamSessionManager>();
        var filter = new StreamSubscriptionFilter(serviceId: "svc-admin-filter", layerIds: [1, 3]);
        using var session = sessionManager.CreateSession("SSE", "filtered-admin-vis-test", filter);

        var response = await _client.GetAsync("/api/v1/admin/streaming/features/sessions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var sessionData = doc.RootElement
            .GetProperty("data")
            .GetProperty("sessions")
            .EnumerateArray()
            .First(s => s.GetProperty("clientLabel").GetString() == "filtered-admin-vis-test");

        sessionData.GetProperty("hasFilter").GetBoolean().Should().BeTrue();
        sessionData.GetProperty("filterSummary").GetString().Should().Contain("serviceId=svc-admin-filter");
        sessionData.GetProperty("serviceIdFilter").GetString().Should().Be("svc-admin-filter");
        sessionData.GetProperty("layerIdFilter")
            .EnumerateArray()
            .Select(layerId => layerId.GetInt32())
            .Should()
            .BeEquivalentTo([1, 3]);
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/streaming/features/sessions/{sessionId}")]
    public async Task DisconnectSession_ExistingId_ReturnsSuccess()
    {
        var sessionManager = _fixture.GetService<FeatureStreamSessionManager>();
        var session = sessionManager.CreateSession("SSE", "disconnect-test");

        var response = await _client.DeleteAsync(
            $"/api/v1/admin/streaming/features/sessions/{session.SessionId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

        // Verify disconnected.
        session.DisconnectToken.IsCancellationRequested.Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/streaming/features/sessions/{sessionId}")]
    public async Task DisconnectSession_UnknownId_Returns404()
    {
        var response = await _client.DeleteAsync(
            $"/api/v1/admin/streaming/features/sessions/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static FeatureStreamEnvelope CreateEnvelope(long cursor) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        Cursor = cursor,
        Timestamp = DateTimeOffset.UtcNow,
        ServiceId = "test-svc",
        LayerId = 0,
        ObjectId = 1,
        Operation = "create",
        Protocol = "rest",
        RequestId = "req-1"
    };

    private static bool IsFeatureChangeFrame(JsonElement element)
        => element.TryGetProperty("type", out var type) &&
           string.Equals(type.GetString(), "feature-change", StringComparison.Ordinal);

    private static async Task SendWebSocketJsonAsync(
        WebSocket webSocket,
        string json,
        CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        await webSocket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<JsonElement> ReceiveWebSocketJsonAsync(
        WebSocket webSocket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await webSocket.ReceiveAsync(buffer, cancellationToken);
            result.MessageType.Should().Be(WebSocketMessageType.Text);
            stream.Write(buffer, 0, result.Count);

            if (!result.EndOfMessage)
            {
                continue;
            }

            using var doc = JsonDocument.Parse(stream.ToArray());
            return doc.RootElement.Clone();
        }
    }

    private static async Task<SseEvent> ReadNextSseEventAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        string? eventName = null;
        string? data = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                throw new EndOfStreamException("SSE stream ended before an event was received.");
            }

            if (line.Length == 0)
            {
                if (eventName is not null && data is not null)
                {
                    using var doc = JsonDocument.Parse(data);
                    return new SseEvent(eventName, doc.RootElement.Clone());
                }

                eventName = null;
                data = null;
                continue;
            }

            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                eventName = line["event: ".Length..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                data = line["data: ".Length..];
            }
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private sealed record SseEvent(string EventName, JsonElement Data);

    private static WebAppFixture CreateLimitedStreamingFixture(int maxConcurrentSessions)
        => new WebAppFixture().ConfigureWebHost(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FeatureStreaming:MaxConcurrentSessions"] = maxConcurrentSessions.ToString(CultureInfo.InvariantCulture)
                });
            });
        });

    private static async Task WaitForSessionAsync(
        FeatureStreamSessionManager manager, string clientLabel, CancellationToken ct)
    {
        for (var i = 0; i < 50 && !ct.IsCancellationRequested; i++)
        {
            if (manager.GetSessions().Any(s => s.ClientLabel == clientLabel))
            {
                return;
            }

            await Task.Delay(20, ct);
        }
    }

    private sealed class TestLicenseStatusProvider(HonuaEdition edition) : ILicenseStatusProvider
    {
        public LicenseStatus GetCurrentStatus()
            => new(edition, IsValid: true, ExpiresAt: null, LicensedTo: null);

        public Task<LicenseUploadResult> UploadLicenseAsync(
            Stream licenseStream,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new LicenseUploadResult(false, "Not supported in tests."));
    }

    // Two services where service "restricted-svc" has a role-gated access
    // policy (admin lacks the role under dev auth) and its layer has no
    // layer-level policy. Used to verify that streaming surfaces evaluate
    // service-level policies, not just layer-level ones.
    private sealed class ServiceLevelAccessStreamLayerCatalog : ILayerCatalog
    {
        public const int OpenLayerId = 0;
        public const int RestrictedServiceLayerId = 9;

        private readonly LayerDefinition _openLayer;
        private readonly LayerDefinition _restrictedServiceLayer;
        private readonly ServiceDefinition _openService;
        private readonly ServiceDefinition _restrictedService;

        public ServiceLevelAccessStreamLayerCatalog()
        {
            var spatialReference = SpatialReference.Create(4326);
            var extent = FeatureExtent.Create(-180, -90, 180, 90, 4326);
            var fields = new[]
            {
                new FieldDefinition("objectid", FieldType.Integer, null, false, null, "Object ID"),
                new FieldDefinition("name", FieldType.String, 255, true, null, "Name"),
                new FieldDefinition("shape", FieldType.Geometry, null, false, null, "Geometry")
            };

            _openLayer = new LayerDefinition(
                Id: OpenLayerId,
                Name: "Public Stream Layer",
                Description: "Layer in the open service",
                GeometryType: GeometryType.Point,
                SpatialReference: spatialReference,
                Fields: fields,
                Extent: extent);

            _restrictedServiceLayer = new LayerDefinition(
                Id: RestrictedServiceLayerId,
                Name: "Layer In Restricted Service",
                Description: "Layer has no policy of its own; service policy gates access",
                GeometryType: GeometryType.Point,
                SpatialReference: spatialReference,
                Fields: fields,
                Extent: extent);

            _openService = new ServiceDefinition(
                "open-svc",
                "Open service",
                [_openLayer],
                spatialReference,
                ServiceExtent: extent);

            _restrictedService = new ServiceDefinition(
                "restricted-svc",
                "Restricted service",
                [_restrictedServiceLayer],
                spatialReference,
                ServiceExtent: extent,
                Metadata: new CatalogMetadata { AccessPolicy = new AccessPolicy { AllowedRoles = ["restricted-stream-reader"] } });
        }

        public Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult<LayerDefinition?>(layerId switch
            {
                OpenLayerId => _openLayer,
                RestrictedServiceLayerId => _restrictedServiceLayer,
                _ => null
            });

        public Task<LayerDefinition[]> ListLayersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new[] { _openLayer, _restrictedServiceLayer });

        public Task<ServiceDefinition?> GetServiceAsync(string serviceName, CancellationToken cancellationToken = default)
            => Task.FromResult<ServiceDefinition?>(serviceName switch
            {
                "open-svc" => _openService,
                "restricted-svc" => _restrictedService,
                _ => null
            });

        public Task<ServiceDefinition[]> ListServicesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new[] { _openService, _restrictedService });

        public Task<bool> LayerExistsAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult(layerId == OpenLayerId || layerId == RestrictedServiceLayerId);

        public Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default)
            => Task.FromResult(serviceName == "open-svc" || serviceName == "restricted-svc");

        public Task<Relationship?> GetRelationshipAsync(int layerId, int relationshipId, CancellationToken cancellationToken = default)
            => Task.FromResult<Relationship?>(null);

        public Task<Relationship[]> ListRelationshipsAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<Relationship>());
    }

    // Mixes one anonymous-readable layer with one role-restricted layer. The
    // capabilities endpoint must filter the restricted layer out for callers
    // that lack the role, so its name/CRS/time-aware status is not leaked.
    private sealed class MixedAccessStreamLayerCatalog : ILayerCatalog
    {
        public const int VisibleLayerId = 0;
        public const int RestrictedLayerId = 7;

        private readonly LayerDefinition _visible;
        private readonly LayerDefinition _restricted;
        private readonly ServiceDefinition _service;

        public MixedAccessStreamLayerCatalog()
        {
            var spatialReference = SpatialReference.Create(4326);
            var extent = FeatureExtent.Create(-180, -90, 180, 90, 4326);
            var fields = new[]
            {
                new FieldDefinition("objectid", FieldType.Integer, null, false, null, "Object ID"),
                new FieldDefinition("name", FieldType.String, 255, true, null, "Name"),
                new FieldDefinition("shape", FieldType.Geometry, null, false, null, "Geometry")
            };

            _visible = new LayerDefinition(
                Id: VisibleLayerId,
                Name: "Public Stream Layer",
                Description: "Anonymous-readable",
                GeometryType: GeometryType.Point,
                SpatialReference: spatialReference,
                Fields: fields,
                Extent: extent,
                Metadata: new CatalogMetadata { AccessPolicy = new AccessPolicy { AllowAnonymous = true } });

            _restricted = new LayerDefinition(
                Id: RestrictedLayerId,
                Name: "Restricted Stream Layer",
                Description: "Role-gated; admins are not in this role",
                GeometryType: GeometryType.Point,
                SpatialReference: spatialReference,
                Fields: fields,
                Extent: extent,
                Metadata: new CatalogMetadata { AccessPolicy = new AccessPolicy { AllowedRoles = ["restricted-stream-reader"] } });

            _service = new ServiceDefinition(
                "stream-test",
                "Streaming capability access test service",
                [_visible, _restricted],
                spatialReference,
                ServiceExtent: extent);
        }

        public Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult<LayerDefinition?>(layerId switch
            {
                VisibleLayerId => _visible,
                RestrictedLayerId => _restricted,
                _ => null
            });

        public Task<LayerDefinition[]> ListLayersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new[] { _visible, _restricted });

        public Task<ServiceDefinition?> GetServiceAsync(string serviceName, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Equals(serviceName, _service.Name, StringComparison.OrdinalIgnoreCase) ? _service : null);

        public Task<ServiceDefinition[]> ListServicesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new[] { _service });

        public Task<bool> LayerExistsAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult(layerId == VisibleLayerId || layerId == RestrictedLayerId);

        public Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Equals(serviceName, _service.Name, StringComparison.OrdinalIgnoreCase));

        public Task<Relationship?> GetRelationshipAsync(int layerId, int relationshipId, CancellationToken cancellationToken = default)
            => Task.FromResult<Relationship?>(null);

        public Task<Relationship[]> ListRelationshipsAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<Relationship>());
    }

    private sealed class TimeAwareStreamLayerCatalog : ILayerCatalog
    {
        private readonly ServiceDefinition _service;
        private readonly LayerDefinition _layer;

        public TimeAwareStreamLayerCatalog()
        {
            var fields = new[]
            {
                new FieldDefinition("objectid", FieldType.Integer, null, false, null, "Object ID"),
                new FieldDefinition("name", FieldType.String, 255, true, null, "Name"),
                new FieldDefinition("timestamp", FieldType.DateTime, null, true, null, "Timestamp"),
                new FieldDefinition("shape", FieldType.Geometry, null, false, null, "Geometry")
            };
            var spatialReference = SpatialReference.Create(4326);
            var extent = FeatureExtent.Create(-180, -90, 180, 90, 4326);
            _layer = new LayerDefinition(
                Id: 0,
                Name: "Time Aware Stream Layer",
                Description: "Time-aware layer for streaming tests",
                GeometryType: GeometryType.Point,
                SpatialReference: spatialReference,
                Fields: fields,
                Extent: extent,
                Metadata: new CatalogMetadata
                {
                    TimeInfo = new LayerTimeInfo { StartTimeField = "timestamp" }
                });
            _service = new ServiceDefinition(
                "test",
                "Time-aware streaming service",
                [_layer],
                spatialReference,
                ServiceExtent: extent);
        }

        public Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult(layerId == _layer.Id ? _layer : null);

        public Task<LayerDefinition[]> ListLayersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new[] { _layer });

        public Task<ServiceDefinition?> GetServiceAsync(string serviceName, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Equals(serviceName, _service.Name, StringComparison.OrdinalIgnoreCase) ? _service : null);

        public Task<ServiceDefinition[]> ListServicesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new[] { _service });

        public Task<bool> LayerExistsAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult(layerId == _layer.Id);

        public Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Equals(serviceName, _service.Name, StringComparison.OrdinalIgnoreCase));

        public Task<Relationship?> GetRelationshipAsync(
            int layerId,
            int relationshipId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<Relationship?>(null);

        public Task<Relationship[]> ListRelationshipsAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<Relationship>());
    }
}
