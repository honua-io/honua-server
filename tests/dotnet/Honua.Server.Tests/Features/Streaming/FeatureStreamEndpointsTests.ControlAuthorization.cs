// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.WebSockets;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Streaming;

public sealed partial class FeatureStreamEndpointsTests
{
    [IntegrationTheory]
    [Operation(Operations.Streaming)]
    [Endpoint("GET /api/v1/streaming/features")]
    [InlineData("\"layerId\":1")]
    [InlineData("\"serviceId\":\"test\"")]
    public async Task WebSocket_SubscribeForbiddenScope_ReturnsErrorAndAcceptsAllowedScope(string deniedScope)
    {
        await using var fixture = new WebAppFixture()
            .WithTestLicense(HonuaEdition.Pro)
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            });
        await fixture.InitializeAsync();
        fixture.UpdateV2ResourceMetadata(0,
            accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = [AlphaRole] });
        fixture.UpdateV2ResourceMetadata(1,
            accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = [BetaRole] });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = await IssueSplitTokenAsync(fixture.GetService<IPortalTokenIssuer>(),
            "control-reader", [AlphaRole], null, timeout.Token);
        var client = fixture.CreateWebSocketClient();
        var configure = client.ConfigureRequest;
        client.ConfigureRequest = request =>
        {
            configure?.Invoke(request);
            request.Headers["X-Honua-Test-Schema"] = fixture.CurrentSchema;
            request.Headers.Referer = SplitReferer;
        };
        using var socket = await client.ConnectAsync(new Uri(
            $"ws://localhost/api/v1/streaming/features?layers=0&token={token}"), timeout.Token);
        var connected = await ReceiveWebSocketJsonAsync(socket, timeout.Token);
        connected.GetProperty("status").GetString().Should().Be("connected");
        var initialSubscription = await ReceiveWebSocketJsonAsync(socket, timeout.Token);
        initialSubscription.GetProperty("status").GetString().Should().Be("subscribed");
        initialSubscription.GetProperty("subscriptionId").GetString().Should().Be("default");

        await AssertDeniedControlThenAllowedAsync(socket, deniedScope, timeout.Token);
    }

    private static async Task AssertDeniedControlThenAllowedAsync(
        WebSocket socket, string deniedScope, CancellationToken cancellationToken)
    {
        await SendWebSocketJsonAsync(socket,
            $$"""{"type":"subscribe","subscriptionId":"denied",{{deniedScope}}}""", cancellationToken);
        var denied = await ReceiveWebSocketJsonAsync(socket, cancellationToken);
        denied.GetProperty("type").GetString().Should().Be("error");
        denied.GetProperty("code").GetString().Should().Be("invalid-subscription");
        denied.GetProperty("message").GetString().Should().Contain("forbidden");

        await SendWebSocketJsonAsync(socket,
            """{"type":"subscribe","subscriptionId":"allowed","layerId":0}""", cancellationToken);
        var allowed = await ReceiveWebSocketJsonAsync(socket, cancellationToken);
        allowed.GetProperty("status").GetString().Should().Be("subscribed");
        allowed.GetProperty("subscriptionId").GetString().Should().Be("allowed");
    }
}
