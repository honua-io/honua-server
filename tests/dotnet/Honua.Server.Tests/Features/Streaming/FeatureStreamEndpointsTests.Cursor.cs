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
}
