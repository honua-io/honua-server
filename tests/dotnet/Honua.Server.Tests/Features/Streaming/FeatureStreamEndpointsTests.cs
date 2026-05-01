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
