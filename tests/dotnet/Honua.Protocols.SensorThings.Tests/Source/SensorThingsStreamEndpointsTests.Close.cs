// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.WebSockets;
using FluentAssertions;
using Honua.Protocols.SensorThings.Streaming;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

public sealed partial class SensorThingsStreamEndpointsTests
{
    [IntegrationTheory]
    [InlineData(WebSocketCloseStatus.NormalClosure, "finished")]
    [InlineData(WebSocketCloseStatus.EndpointUnavailable, "")]
    [Operation(Operations.Streaming)]
    [Endpoint("GET /sta/v1.1/ObservationsStream")]
    public async Task ObservationsStream_ClientClose_AcknowledgesAndReleasesSession(
        WebSocketCloseStatus status, string description)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var client = _fixture.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers["X-API-Key"] = WebAppFixture.SharedAdminPassword;
            request.Headers["X-Honua-Test-Schema"] = _fixture.CurrentSchema;
        };
        using var socket = await client.ConnectAsync(new Uri("ws://localhost/sta/v1.1/ObservationsStream"), timeout.Token);
        using var connected = await ReadSocketFrameAsync(socket, timeout.Token);
        connected.RootElement.GetProperty("status").GetString().Should().Be("connected");
        var manager = _fixture.GetService<ObservationStreamSessionManager>();
        manager.SessionCount.Should().Be(1);

        using var closeBudget = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await socket.CloseOutputAsync(status, description, closeBudget.Token);
        var result = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[256]), closeBudget.Token);
        result.MessageType.Should().Be(WebSocketMessageType.Close);
        result.CloseStatus.Should().Be(status);
        result.CloseStatusDescription.Should().Be(description);
        while (manager.SessionCount != 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), closeBudget.Token);
        }
    }
}
