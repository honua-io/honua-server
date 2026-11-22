// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.WebSockets;
using FluentAssertions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Realtime;
using Honua.Server.Tests.Features.Admin;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Streaming;

[Collection("Database")]
[Protocol(TestProtocols.Streaming)]
[Operation(Operations.Streaming)]
public sealed class FeatureStreamDrainTests
{
    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task ShutdownDrain_EndsOpenSseWithRetryAndRefusesNewStreams()
    {
        var fixture = CreateFixture();
        await fixture.InitializeAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var client = fixture.CreateClient(c =>
            {
                c.DefaultRequestHeaders.Add(RlsClaimsTestAuthHandler.UserHeader, "alice");
                c.DefaultRequestHeaders.Add(RlsClaimsTestAuthHandler.RolesHeader, "admin");
            });
            using var held = await OpenSseAsync(client, cts.Token);
            held.StatusCode.Should().Be(HttpStatusCode.OK);

            await fixture.GetService<IRealtimeStreamLifecycle>().DrainAsync(cts.Token);
            var body = await held.Content.ReadAsStringAsync(cts.Token);
            using var refused = await OpenSseAsync(client, cts.Token);

            body.Should().MatchRegex(@"\nretry: [1-5][0-9]{3}\n\n$");
            refused.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            refused.Headers.GetValues("Retry-After").Should().Equal("2");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task ShutdownDrain_ClosesWebSocketWithServiceRestartAndWaitsForPeerReply()
    {
        var fixture = CreateFixture();
        await fixture.InitializeAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var client = fixture.CreateWebSocketClient();
            client.ConfigureRequest = request =>
            {
                request.Headers[RlsClaimsTestAuthHandler.UserHeader] = "alice";
                request.Headers[RlsClaimsTestAuthHandler.RolesHeader] = "admin";
            };
            using var socket = await client.ConnectAsync(new Uri("ws://localhost/api/v1/streaming/features"), cts.Token);

            var drain = fixture.GetService<IRealtimeStreamLifecycle>().DrainAsync(cts.Token);
            var buffer = new byte[16 * 1024];
            WebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(buffer, cts.Token);
            }
            while (received.MessageType != WebSocketMessageType.Close);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
            await drain;

            received.CloseStatus.Should().Be((WebSocketCloseStatus)1012);
            received.CloseStatusDescription.Should().Be("Service restart.");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static WebAppFixture CreateFixture() => new WebAppFixture()
        .ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Pro))
        .ConfigureServices(services =>
        {
            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, RlsClaimsTestAuthHandler>(
                RlsClaimsTestAuthHandler.SchemeName, _ => { });
            services.PostConfigureAll<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = RlsClaimsTestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = RlsClaimsTestAuthHandler.SchemeName;
                options.DefaultScheme = RlsClaimsTestAuthHandler.SchemeName;
            });
        });

    private static async Task<HttpResponseMessage> OpenSseAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/streaming/features");
        request.Headers.Accept.ParseAdd("text/event-stream");
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }
}
