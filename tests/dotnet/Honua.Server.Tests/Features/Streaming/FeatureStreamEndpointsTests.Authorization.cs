// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Infrastructure.Events;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Streaming;

public sealed partial class FeatureStreamEndpointsTests
{
    [IntegrationTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Stream_RealPortalCredentialExpiresOrIsRevoked_TerminatesAndReplacementResumes(bool webSocket, bool expire)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var issuer = _fixture.GetService<IPortalTokenIssuer>();
        const string referer = "https://stream-proof.example/";
        async Task<PortalTokenIssuance> IssueAsync(TimeSpan ttl) => await issuer.IssueAsync(
            new PortalTokenIssueRequest("stream-proof", "Stream proof", null, ["admin"],
                PortalTokenClientType.Referer, referer, DateTimeOffset.UtcNow + ttl), ct);
        var credential = await IssueAsync(expire ? TimeSpan.FromSeconds(5) : TimeSpan.FromMinutes(1));
        var publisher = _fixture.GetService<IFeatureChangeEventPublisher>();
        async Task PublishAsync(long id) => await publisher.PublishAsync(new FeatureChangeEventRequest
        {
            ServiceId = "test", LayerId = 0, ObjectId = id, Operation = "update", Protocol = "rest",
            RequestId = $"auth-proof-{id}", PropertiesJson = "{\"name\":\"credential-proof\"}"
        }, ct);

        string Path(string token, long? cursor = null) =>
            $"/api/v1/streaming/features?serviceId=test&layers=0&token={token}" + (cursor.HasValue ? $"&cursor={cursor}" : "");
        async Task<WebSocket> ConnectAsync(string token, long? cursor = null)
        {
            var client = _fixture.CreateWebSocketClient();
            var configure = client.ConfigureRequest;
            client.ConfigureRequest = request => { configure?.Invoke(request); request.Headers.Referer = referer; };
            return await client.ConnectAsync(new Uri("ws://localhost" + Path(token, cursor)), ct);
        }

        long lastCursor;
        if (webSocket)
        {
            using var socket = await ConnectAsync(credential.Token);
            _ = await ReceiveWebSocketJsonAsync(socket, ct);
            await PublishAsync(73001);
            JsonElement frame;
            do { frame = await ReceiveWebSocketJsonAsync(socket, ct); }
            while (frame.GetProperty("type").GetString() != "feature-change");
            frame.GetProperty("objectId").GetInt64().Should().Be(73001);
            lastCursor = frame.GetProperty("cursor").GetInt64();
            if (!expire) { await issuer.RevokeAsync(credential.Token, ct); }
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bound.CancelAfter(expire ? TimeSpan.FromSeconds(8) : TimeSpan.FromSeconds(4));
            var buffer = new byte[8192];
            WebSocketReceiveResult terminal;
            do
            {
                terminal = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), bound.Token);
                if (terminal.MessageType == WebSocketMessageType.Text)
                {
                    Encoding.UTF8.GetString(buffer, 0, terminal.Count).Should().NotContain("feature-change");
                }
            } while (terminal.MessageType != WebSocketMessageType.Close);
            terminal.CloseStatus.Should().Be(WebSocketCloseStatus.PolicyViolation);
            terminal.CloseStatusDescription.Should().Be("authorization-ended");
        }
        else
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Path(credential.Token));
            request.Headers.Referrer = new Uri(referer);
            request.Headers.Accept.ParseAdd("text/event-stream");
            using var response = await _fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            _ = await ReadNextSseEventAsync(reader, ct);
            await PublishAsync(73001);
            SseEvent frame;
            do { frame = await ReadNextSseEventAsync(reader, ct); } while (frame.EventName != "feature-change");
            frame.Data.GetProperty("objectId").GetInt64().Should().Be(73001);
            lastCursor = frame.Data.GetProperty("cursor").GetInt64();
            if (!expire) { await issuer.RevokeAsync(credential.Token, ct); }
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bound.CancelAfter(expire ? TimeSpan.FromSeconds(8) : TimeSpan.FromSeconds(4));
            do
            {
                frame = await ReadNextSseEventAsync(reader, bound.Token);
                frame.EventName.Should().NotBe("feature-change");
            } while (!frame.Data.TryGetProperty("code", out _));
            frame.Data.GetProperty("code").GetString().Should().Be("authorization-ended");
            (await reader.ReadLineAsync(bound.Token)).Should().BeNull("the authorization outcome is terminal");
        }

        await PublishAsync(73002);
        var replacement = await IssueAsync(TimeSpan.FromMinutes(1));
        using var resumed = await ConnectAsync(replacement.Token, lastCursor);
        JsonElement replay;
        do { replay = await ReceiveWebSocketJsonAsync(resumed, ct); }
        while (replay.GetProperty("type").GetString() != "feature-change");
        replay.GetProperty("objectId").GetInt64().Should().Be(73002);
        replay.GetProperty("cursor").GetInt64().Should().BeGreaterThan(lastCursor);

        using var rejected = new HttpRequestMessage(HttpMethod.Get, Path(credential.Token, lastCursor));
        rejected.Headers.Referrer = new Uri(referer);
        rejected.Headers.Accept.ParseAdd("text/event-stream");
        using var denied = await _fixture.Client.SendAsync(rejected, HttpCompletionOption.ResponseHeadersRead, ct);
        denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
