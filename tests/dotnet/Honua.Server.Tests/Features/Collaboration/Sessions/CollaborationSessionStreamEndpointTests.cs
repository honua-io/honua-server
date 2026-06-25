// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Collaboration.Sessions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Collaboration.Sessions;

/// <summary>
/// Integration coverage for the authenticated WebSocket collaboration session transport
/// (honua-server#971/#1290): join handshake + presence snapshot, cross-participant presence
/// fan-out, cursor control frames, and fail-closed authorization for unauthorized joins.
/// </summary>
[Protocol(Honua.TestKit.Constants.ProtocolNames.Streaming)]
[Operation(Honua.TestKit.Constants.Operations.Streaming)]
public sealed class CollaborationSessionStreamEndpointTests
{
    private const string AdminPassword = "collaboration-stream-test-key";
    private const string MapId = "saved-map:ops";

    [IntegrationTest]
    [Endpoint("GET /api/v1/saved-maps/{mapId}/collaboration/sessions/stream")]
    public async Task Stream_WithAuthorizedJoin_ReceivesConnectedStatusAndSnapshot()
    {
        using var factory = CreateFactory(allowJoin: true);
        var wsClient = factory.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = request => request.Headers["X-API-Key"] = AdminPassword;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using var ws = await wsClient.ConnectAsync(
            new Uri($"ws://localhost/api/v1/saved-maps/{MapId}/collaboration/sessions/stream?displayName=Ada"),
            cts.Token);

        ws.State.Should().Be(WebSocketState.Open);

        var connected = await ReceiveJsonAsync(ws, cts.Token);
        connected.GetProperty("type").GetString().Should().Be("status");
        connected.GetProperty("status").GetString().Should().Be("connected");

        var snapshot = await ReceiveJsonAsync(ws, cts.Token);
        snapshot.GetProperty("type").GetString().Should().Be("snapshot");
        snapshot.GetProperty("snapshot").GetProperty("mapId").GetString().Should().Be(MapId);
        snapshot.GetProperty("snapshot").GetProperty("participants").GetArrayLength().Should().Be(1);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/saved-maps/{mapId}/collaboration/sessions/stream")]
    public async Task Stream_WhenPeerJoins_ReceivesParticipantJoinedPresenceEvent()
    {
        using var factory = CreateFactory(allowJoin: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var firstClient = factory.Server.CreateWebSocketClient();
        firstClient.ConfigureRequest = request => request.Headers["X-API-Key"] = AdminPassword;
        using var first = await firstClient.ConnectAsync(
            new Uri($"ws://localhost/api/v1/saved-maps/{MapId}/collaboration/sessions/stream?displayName=Ada"),
            cts.Token);

        // Drain the first client's connect + snapshot frames.
        _ = await ReceiveJsonAsync(first, cts.Token);
        _ = await ReceiveJsonAsync(first, cts.Token);

        // A second participant joins the same map; the first must observe the presence event.
        var secondClient = factory.Server.CreateWebSocketClient();
        secondClient.ConfigureRequest = request => request.Headers["X-API-Key"] = AdminPassword;
        using var second = await secondClient.ConnectAsync(
            new Uri($"ws://localhost/api/v1/saved-maps/{MapId}/collaboration/sessions/stream?displayName=Bob"),
            cts.Token);

        var joined = await ReceiveJsonAsync(first, cts.Token);
        joined.GetProperty("type").GetString().Should().Be("participant.joined");
        joined.GetProperty("participant").GetProperty("displayName").GetString().Should().Be("Bob");

        await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await second.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/saved-maps/{mapId}/collaboration/sessions/stream")]
    public async Task Stream_WhenPeerSendsCursorFrame_FanOutsCursorEvent()
    {
        using var factory = CreateFactory(allowJoin: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var firstClient = factory.Server.CreateWebSocketClient();
        firstClient.ConfigureRequest = request => request.Headers["X-API-Key"] = AdminPassword;
        using var first = await firstClient.ConnectAsync(
            new Uri($"ws://localhost/api/v1/saved-maps/{MapId}/collaboration/sessions/stream?displayName=Ada"),
            cts.Token);
        _ = await ReceiveJsonAsync(first, cts.Token);
        _ = await ReceiveJsonAsync(first, cts.Token);

        var secondClient = factory.Server.CreateWebSocketClient();
        secondClient.ConfigureRequest = request => request.Headers["X-API-Key"] = AdminPassword;
        using var second = await secondClient.ConnectAsync(
            new Uri($"ws://localhost/api/v1/saved-maps/{MapId}/collaboration/sessions/stream?displayName=Bob"),
            cts.Token);
        _ = await ReceiveJsonAsync(second, cts.Token);
        _ = await ReceiveJsonAsync(second, cts.Token);

        // Drain the participant.joined event the second peer's join produced on the first socket.
        var joined = await ReceiveJsonAsync(first, cts.Token);
        joined.GetProperty("type").GetString().Should().Be("participant.joined");

        await SendJsonAsync(
            second,
            """{"type":"cursor","cursor":{"longitude":-122.4,"latitude":37.7,"zoom":12}}""",
            cts.Token);

        var cursor = await ReceiveJsonAsync(first, cts.Token);
        cursor.GetProperty("type").GetString().Should().Be("cursor.updated");
        cursor.GetProperty("cursor").GetProperty("longitude").GetDouble().Should().BeApproximately(-122.4, 0.0001);

        await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await second.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/saved-maps/{mapId}/collaboration/sessions/stream")]
    public async Task Stream_WithoutAuthorization_RejectsUpgrade()
    {
        using var factory = CreateFactory(allowJoin: false);
        var wsClient = factory.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = request => request.Headers["X-API-Key"] = AdminPassword;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // The fail-closed authorizer denies the join, so the upgrade is rejected before the
        // socket is accepted. The TestServer surfaces the rejection as a handshake exception.
        var connect = async () => await wsClient.ConnectAsync(
            new Uri($"ws://localhost/api/v1/saved-maps/{MapId}/collaboration/sessions/stream"),
            cts.Token);

        await connect.Should().ThrowAsync<Exception>();
    }

    private static WebApplicationFactory<Program> CreateFactory(bool allowJoin)
    {
        return new TestWebApplicationFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["HONUA_DEV_AUTH"] = "false",
                        ["HONUA_ADMIN_PASSWORD"] = AdminPassword
                    });
                });

                if (allowJoin)
                {
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<ISavedMapCollaborationAuthorizer>();
                        services.AddSingleton<ISavedMapCollaborationAuthorizer, AllowSavedMapCollaborationAuthorizer>();
                    });
                }
            });
    }

    private static async Task<JsonElement> ReceiveJsonAsync(WebSocket ws, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new System.IO.MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, cancellationToken);
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static Task SendJsonAsync(WebSocket ws, string json, CancellationToken cancellationToken)
        => ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, cancellationToken);

    private sealed class AllowSavedMapCollaborationAuthorizer : ISavedMapCollaborationAuthorizer
    {
        public ValueTask<SavedMapCollaborationAuthorizationResult> AuthorizeJoinAsync(
            string mapId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(SavedMapCollaborationAuthorizationResult.Allow());
    }
}
