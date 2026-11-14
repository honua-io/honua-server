// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Events;
using Honua.Server.Features.Streaming;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Honua.TestKit;

namespace Honua.Server.Tests.Features.Streaming;

public sealed partial class FeatureStreamEndpointsTests
{
    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_BareNumberCursor_SendsCursorEpochResetThenGoesLive()
    {
        await StartDefaultApplicationAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var head = await _fixture.GetService<IFeatureChangeEventStore>().GetCurrentCursorAsync(cts.Token);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/streaming/features?layers=0&cursor=1");
        request.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
        (await ReadNextSseEventAsync(reader, cts.Token)).EventName.Should().Be("status");
        var reset = await ReadNextSseEventAsync(reader, cts.Token);
        reset.EventName.Should().Be("reset");
        reset.Data.GetProperty("reason").GetString().Should().Be("cursor-epoch");
        var resetHead = reset.Data.GetProperty("head").GetString();
        resetHead.Should().StartWith("features:");
        FeatureStreamCursor.Parse(resetHead, null).Generation.Should().BeGreaterThanOrEqualTo(head);

        var change = await _fixture.AppendChangeAsync(new FeatureChangeEventRequest
        {
            ServiceId = "test",
            LayerId = 0,
            ObjectId = 5150,
            Operation = "update",
            Protocol = "rest",
            RequestId = "req-cursor-epoch-live"
        }, cts.Token);
        SseEvent live;
        do
        {
            live = await ReadNextSseEventAsync(reader, cts.Token);
        }
        while (live.EventName != "feature-change" || live.Data.GetProperty("objectId").GetInt64() != change.ObjectId);

        live.Data.GetProperty("cursor").GetInt64().Should().Be(change.Cursor);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_UnparseableCursor_ReturnsBadRequest()
    {
        await StartDefaultApplicationAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/streaming/features?layers=0&cursor=features:not-a-number");
        request.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task WebSocket_UnparseableCursor_IsRejectedBeforeUpgrade()
    {
        await StartDefaultApplicationAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var connect = () => _fixture.CreateWebSocketClient().ConnectAsync(
            new Uri("ws://localhost/api/v1/streaming/features?layers=0&cursor=features:invalid"), timeout.Token);
        var exception = await connect.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("status code: 400");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task WebSocket_BareNumberCursor_SendsCursorEpochReset()
    {
        await StartDefaultApplicationAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var socket = await _fixture.CreateWebSocketClient().ConnectAsync(
            new Uri("ws://localhost/api/v1/streaming/features?layers=0&cursor=1"), timeout.Token);
        _ = await ReceiveWebSocketJsonAsync(socket, timeout.Token);
        var reset = await ReceiveWebSocketJsonAsync(socket, timeout.Token);
        reset.GetProperty("type").GetString().Should().Be("reset");
        reset.GetProperty("reason").GetString().Should().Be("cursor-epoch");
        reset.GetProperty("head").GetString().Should().StartWith("features:");
    }

    [IntegrationTheory]
    [Endpoint("GET /api/v1/streaming/features")]
    [InlineData("1", "reset")]
    [InlineData("\"1\"", "reset")]
    [InlineData("\"features:1\"", "feature-change")]
    [InlineData("\"features:invalid\"", "error")]
    [InlineData("true", "error")]
    public async Task WebSocket_ControlCursor_EnforcesFeatureEpoch(string cursorJson, string expectedType)
    {
        var store = new FakeFeatureChangeEventStore();
        store.Append(new FeatureChangeEventRequest
        {
            ServiceId = "test", LayerId = 0, ObjectId = 734201, Operation = "update",
            Protocol = "rest", RequestId = "control-cursor-first"
        });
        store.Append(new FeatureChangeEventRequest
        {
            ServiceId = "test", LayerId = 0, ObjectId = 734202, Operation = "update",
            Protocol = "rest", RequestId = "control-cursor-second"
        });
        await using var fixture = new WebAppFixture()
            .ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Pro))
            .ReplaceService<IFeatureChangeEventStore>(store);
        await fixture.InitializeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var socket = await fixture.CreateWebSocketClient().ConnectAsync(
            new Uri("ws://localhost/api/v1/streaming/features"), timeout.Token);
        _ = await ReceiveWebSocketJsonAsync(socket, timeout.Token);
        await SendWebSocketJsonAsync(socket,
            $$"""{"type":"subscribe","subscriptionId":"epoch-test","layerId":0,"cursor":{{cursorJson}}}""",
            timeout.Token);
        var frame = await ReadWebSocketAfterStatusesAsync(socket, timeout.Token);
        frame.GetProperty("type").GetString().Should().Be(expectedType);
        if (expectedType == "reset")
        {
            frame.GetProperty("reason").GetString().Should().Be("cursor-epoch");
            frame.GetProperty("head").GetString().Should().Be("features:2");
        }
        else if (expectedType == "feature-change")
        {
            frame.GetProperty("objectId").GetInt64().Should().Be(734202);
            frame.GetProperty("subscriptionId").GetString().Should().Be("epoch-test");
        }
        else
        {
            frame.GetProperty("code").GetString().Should().Be("invalid-cursor");
        }
    }

    [IntegrationTheory]
    [Endpoint("GET /api/v1/streaming/features")]
    [InlineData("delta")]
    [InlineData("snapshot")]
    [InlineData("snapshot-then-delta")]
    public async Task WebSocket_ExpiredControlCursor_ResetsAndSnapshotsWithoutLosingSibling(string mode)
    {
        await using var fixture = new WebAppFixture()
            .ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Pro))
            .ReplaceService<IFeatureChangeEventStore>(FakeFeatureChangeEventStore.WithRetentionWindow(
                FeatureChangeRetentionWindow.Retained(currentCursor: 60, oldestRetainedCursor: 50)));
        await fixture.InitializeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var socket = await fixture.CreateWebSocketClient().ConnectAsync(
            new Uri("ws://localhost/api/v1/streaming/features"), timeout.Token);
        _ = await ReceiveWebSocketJsonAsync(socket, timeout.Token);
        await SendWebSocketJsonAsync(socket,
            """{"type":"subscribe","subscriptionId":"sibling","layerId":1}""", timeout.Token);
        (await ReceiveWebSocketJsonAsync(socket, timeout.Token)).GetProperty("status").GetString().Should().Be("subscribed");
        await SendWebSocketJsonAsync(socket,
            $$"""{"type":"subscribe","subscriptionId":"expired","layerId":0,"cursor":"features:1","mode":"{{mode}}"}""",
            timeout.Token);
        var reset = await ReceiveWebSocketJsonAsync(socket, timeout.Token);
        reset.GetProperty("type").GetString().Should().Be("reset");
        reset.GetProperty("reason").GetString().Should().Be("retention");
        reset.GetProperty("head").GetString().Should().Be("features:60");
        System.Text.Json.JsonElement frame;
        do
        {
            frame = await ReceiveWebSocketJsonAsync(socket, timeout.Token);
        }
        while (frame.GetProperty("type").GetString() is not ("snapshot-end" or "snapshot"));
        frame.GetProperty("complete").GetBoolean().Should().BeTrue();
        frame.GetProperty("resumeCursor").GetString().Should().Be("features:60");
        (await ReceiveWebSocketJsonAsync(socket, timeout.Token)).GetProperty("status").GetString().Should().Be("subscribed");
        await SendWebSocketJsonAsync(socket,
            """{"type":"unsubscribe","subscriptionId":"sibling"}""", timeout.Token);
        var sibling = await ReceiveWebSocketJsonAsync(socket, timeout.Token);
        sibling.GetProperty("status").GetString().Should().Be("unsubscribed");
        sibling.GetProperty("subscriptionId").GetString().Should().Be("sibling");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task WebSocket_EmittedResumeCursor_ReplaysInterveningEventWithoutEpochReset()
    {
        var store = new FakeFeatureChangeEventStore();
        for (var id = 1; id <= 2; id++)
        {
            store.Append(new FeatureChangeEventRequest
            {
                ServiceId = "test", LayerId = 0, ObjectId = id, Operation = "update",
                Protocol = "rest", RequestId = $"roundtrip-{id}"
            });
        }
        await using var fixture = new WebAppFixture()
            .ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Pro))
            .ReplaceService<IFeatureChangeEventStore>(store);
        await fixture.InitializeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string checkpoint;
        using (var socket = await fixture.CreateWebSocketClient().ConnectAsync(
            new Uri("ws://localhost/api/v1/streaming/features?layers=0&cursor=features:1"), timeout.Token))
        {
            var delivered = await ReadWebSocketAfterStatusesAsync(socket, timeout.Token);
            delivered.GetProperty("objectId").GetInt64().Should().Be(2);
            checkpoint = delivered.GetProperty("resumeCursor").GetString()!;
            checkpoint.Should().Be("features:2");
        }
        store.Append(new FeatureChangeEventRequest
        {
            ServiceId = "test", LayerId = 0, ObjectId = 3, Operation = "update",
            Protocol = "rest", RequestId = "roundtrip-3"
        });
        using var resumed = await fixture.CreateWebSocketClient().ConnectAsync(
            new Uri($"ws://localhost/api/v1/streaming/features?layers=0&cursor={checkpoint}"), timeout.Token);
        var replay = await ReadWebSocketAfterStatusesAsync(resumed, timeout.Token);
        replay.GetProperty("type").GetString().Should().Be("feature-change");
        replay.GetProperty("objectId").GetInt64().Should().Be(3);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_ExpiredDeltaCursor_SendsRetentionResetThenSnapshot()
    {
        await using var fixture = new WebAppFixture()
            .ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Pro))
            .ReplaceService<IFeatureChangeEventStore>(FakeFeatureChangeEventStore.WithRetentionWindow(
                FeatureChangeRetentionWindow.Retained(currentCursor: 60, oldestRetainedCursor: 50)));
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/streaming/features?layers=0&cursor=features:1");
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        SseEvent frame;
        do
        {
            frame = await ReadNextSseEventAsync(reader, timeout.Token);
        }
        while (frame.EventName == "status");
        frame.EventName.Should().Be("reset");
        frame.Data.GetProperty("reason").GetString().Should().Be("retention");
        frame.Data.GetProperty("head").GetString().Should().Be("features:60");
        (await ReadNextSseEventAsync(reader, timeout.Token)).EventName.Should().Be("snapshot-begin");
    }

    [IntegrationTheory]
    [Endpoint("GET /api/v1/streaming/features")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sse_ExpiredUnsafeSnapshotScope_ResetsWithoutPublishingSnapshotCheckpoint(bool filtered)
    {
        await using var fixture = new WebAppFixture()
            .ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Pro))
            .ReplaceService<IFeatureChangeEventStore>(FakeFeatureChangeEventStore.WithRetentionWindow(
                FeatureChangeRetentionWindow.Retained(currentCursor: 60, oldestRetainedCursor: 50)));
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/streaming/features?cursor=features:1" + (filtered ? "&layers=0&bbox=-180,-90,180,90" : ""));
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var body = await response.Content.ReadAsStringAsync(timeout.Token);
        body.Should().Contain("event: reset").And.Contain("retention");
        body.Should().NotContain("event: snapshot").And.NotContain("id: features:");
        body.Should().Contain(filtered ? "cannot be combined" : "explicit layer");
    }

    [IntegrationTheory]
    [Endpoint("GET /api/v1/streaming/features")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WebSocket_ExpiredUnsafeSnapshotScope_ResetsWithoutPublishingSnapshotCheckpoint(bool filtered)
    {
        await using var fixture = new WebAppFixture()
            .ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Pro))
            .ReplaceService<IFeatureChangeEventStore>(FakeFeatureChangeEventStore.WithRetentionWindow(
                FeatureChangeRetentionWindow.Retained(currentCursor: 60, oldestRetainedCursor: 50)));
        await fixture.InitializeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var socketClient = fixture.CreateWebSocketClient();
        var configureRequest = socketClient.ConfigureRequest;
        socketClient.ConfigureRequest = request =>
        {
            configureRequest?.Invoke(request);
            request.Headers["X-API-Key"] = WebAppFixture.SharedAdminPassword;
        };
        using var socket = await socketClient.ConnectAsync(
            new Uri("ws://localhost/api/v1/streaming/features?cursor=features:1" + (filtered ? "&layers=0&bbox=-180,-90,180,90" : "")), timeout.Token);
        var status = await ReceiveWebSocketJsonAsync(socket, timeout.Token);
        status.GetProperty("type").GetString().Should().Be("status");
        if (filtered)
        {
            var subscribed = await ReceiveWebSocketJsonAsync(socket, timeout.Token);
            subscribed.GetProperty("status").GetString().Should().Be("subscribed");
        }
        var reset = await ReceiveWebSocketJsonAsync(socket, timeout.Token);
        reset.GetProperty("type").GetString().Should().Be("reset");
        reset.GetProperty("reason").GetString().Should().Be("retention");
        var error = await ReceiveWebSocketJsonAsync(socket, timeout.Token);
        error.GetProperty("type").GetString().Should().Be("status");
        error.GetProperty("status").GetString().Should().Be("error");
        error.GetProperty("message").GetString().Should().Contain(filtered ? "cannot be combined" : "explicit layer");
    }

    [IntegrationTheory]
    [Endpoint("GET /api/v1/streaming/features")]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task WebSocket_RetentionAdvancesDuringReplayOrLiveSweep_SendsTypedReset(bool duringReplay, bool nonemptyPage)
    {
        var store = FakeFeatureChangeEventStore.WithRetentionWindow(
            FeatureChangeRetentionWindow.Retained(currentCursor: 60, oldestRetainedCursor: 50));
        if (nonemptyPage)
        {
            store.Append(new FeatureChangeEventRequest
            {
                ServiceId = "test", LayerId = 0, ObjectId = 9001, Operation = "update", Protocol = "rest",
                RequestId = "retention-websocket-nonempty-page"
            }, cursorGap: 40);
            store.SetRetentionWindow(FeatureChangeRetentionWindow.Retained(currentCursor: 60, oldestRetainedCursor: 50));
        }
        await using var fixture = new WebAppFixture()
            .ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Pro))
            .ReplaceService<IFeatureChangeEventStore>(store);
        await fixture.InitializeAsync();
        void AdvanceRetention()
        {
            store.QueryCompleted = null;
            store.SetRetentionWindow(FeatureChangeRetentionWindow.Retained(currentCursor: 100, oldestRetainedCursor: 80));
        }

        if (duringReplay)
        {
            store.QueryCompleted = AdvanceRetention;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var query = duringReplay ? "?layers=0&cursor=features:59" : "?layers=0";
        using var socket = await fixture.CreateWebSocketClient().ConnectAsync(
            new Uri("ws://localhost/api/v1/streaming/features" + query), timeout.Token);
        _ = await ReceiveWebSocketJsonAsync(socket, timeout.Token);
        if (!duringReplay)
        {
            AdvanceRetention();
        }

        var reset = await ReadWebSocketAfterStatusesAsync(socket, timeout.Token);
        reset.GetProperty("type").GetString().Should().Be("reset");
        reset.GetProperty("reason").GetString().Should().Be("retention");
        reset.GetProperty("head").GetString().Should().Be("features:100");
    }

    [IntegrationTheory]
    [Endpoint("GET /api/v1/streaming/features")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sse_RetentionAdvancesDuringNonemptyReplay_ResetsBeforeDeliveringPage(bool filtered)
    {
        var store = FakeFeatureChangeEventStore.WithRetentionWindow(
            FeatureChangeRetentionWindow.Retained(currentCursor: 60, oldestRetainedCursor: 50));
        SeedRetentionReplayPage(store);
        store.QueryCompleted = () =>
        {
            store.QueryCompleted = null;
            store.SetRetentionWindow(FeatureChangeRetentionWindow.Retained(currentCursor: 100, oldestRetainedCursor: 80));
        };
        await using var fixture = new WebAppFixture()
            .ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Pro))
            .ReplaceService<IFeatureChangeEventStore>(store);
        await fixture.InitializeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var client = fixture.CreateAdminClient();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/api/v1/streaming/features?layers=0&cursor=features:59" + (filtered ? "&bbox=-180,-90,180,90" : ""));
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        SseEvent frame;
        do
        {
            frame = await ReadNextSseEventAsync(reader, timeout.Token);
        }
        while (frame.EventName == "status");
        frame.EventName.Should().Be("reset");
        frame.Data.GetProperty("reason").GetString().Should().Be("retention");
        if (filtered)
        {
            var error = await ReadNextSseEventAsync(reader, timeout.Token);
            error.EventName.Should().Be("status");
            error.Data.GetProperty("status").GetString().Should().Be("error");
            error.Data.GetProperty("message").GetString().Should().Contain("cannot be combined");
        }
    }

    private static async Task<System.Text.Json.JsonElement> ReadWebSocketAfterStatusesAsync(
        System.Net.WebSockets.WebSocket socket, CancellationToken cancellationToken)
    {
        System.Text.Json.JsonElement reset;
        do
        {
            reset = await ReceiveWebSocketJsonAsync(socket, cancellationToken);
        }
        while (reset.GetProperty("type").GetString() == "status");
        return reset;
    }

    private static void SeedRetentionReplayPage(FakeFeatureChangeEventStore store)
    {
        store.Append(new FeatureChangeEventRequest
        {
            ServiceId = "test", LayerId = 0, ObjectId = 9001, Operation = "update", Protocol = "rest",
            RequestId = "retention-sse-nonempty-page"
        }, cursorGap: 40);
        store.SetRetentionWindow(FeatureChangeRetentionWindow.Retained(currentCursor: 60, oldestRetainedCursor: 50));
    }

}
