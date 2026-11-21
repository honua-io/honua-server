// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.WebSockets;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Collaboration.Sessions;

public sealed partial class CollaborationSessionStreamEndpointTests
{
    private const string RevocationReferer = "https://collaboration-proof.example/";

    // The join is authorized once, before the upgrade; a credential revoked afterwards must still
    // end the stream instead of leaving it open to the map's later edits.
    [IntegrationTest]
    [Endpoint("GET /api/v1/maps/{mapId}/collaboration/sessions/stream")]
    public async Task Stream_CredentialRevokedAfterJoin_ClosesWithAuthorizationEnded()
    {
        using var factory = CreateFactory(allowJoin: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (socket, token) = await ConnectWithPortalTokenAsync(factory, cts.Token);
        using var ws = socket;
        var issuer = factory.Services.GetRequiredService<IPortalTokenIssuer>();

        await issuer.RevokeAsync(token, cts.Token);

        var closed = await ReceiveUntilCloseAsync(ws, cts.Token);
        closed.CloseStatus.Should().Be(WebSocketCloseStatus.PolicyViolation);
        closed.CloseStatusDescription.Should().Be("authorization-ended");
    }

    private static async Task<(WebSocket Socket, string Token)> ConnectWithPortalTokenAsync(
        WebApplicationFactory<Program> factory, CancellationToken cancellationToken)
    {
        var issuer = factory.Services.GetRequiredService<IPortalTokenIssuer>();
        var credential = await issuer.IssueAsync(
            new PortalTokenIssueRequest("collaboration-proof", "Collaboration proof", null, ["publisher"],
                PortalTokenClientType.Referer, RevocationReferer, DateTimeOffset.UtcNow.AddMinutes(5)),
            cancellationToken);

        var client = factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers.Referer = RevocationReferer;
        var ws = await client.ConnectAsync(
            new Uri($"ws://localhost/api/v1/maps/{MapId}/collaboration/sessions/stream?displayName=Ada&token={credential.Token}"),
            cancellationToken);
        ws.State.Should().Be(WebSocketState.Open);
        return (ws, credential.Token);
    }

    private static async Task<WebSocketReceiveResult> ReceiveUntilCloseAsync(WebSocket ws, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, cancellationToken);
        }
        while (result.MessageType != WebSocketMessageType.Close);

        return result;
    }
}
