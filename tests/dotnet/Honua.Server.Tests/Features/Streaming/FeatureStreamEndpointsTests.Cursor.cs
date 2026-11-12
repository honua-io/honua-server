// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Infrastructure.Events;
using Honua.Server.Features.Streaming;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Streaming;

public sealed partial class FeatureStreamEndpointsTests
{
    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_BareNumberCursor_SendsCursorEpochResetThenGoesLive()
    {
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
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/streaming/features?layers=0&cursor=features:not-a-number");
        request.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
