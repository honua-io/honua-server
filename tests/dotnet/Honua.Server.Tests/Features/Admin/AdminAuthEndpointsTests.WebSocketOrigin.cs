// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class AdminAuthEndpointsTests
{
    private const string WebSocketProbePath = "wss://localhost/__websocket-origin-probe";

    // A WebSocket upgrade carries the browser session cookie without an antiforgery check, so a
    // session-authenticated upgrade must come from the server's own origin.
    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/auth/session")]
    public async Task BrowserSession_WebSocketUpgradeFromAnotherOrigin_IsRefused()
    {
        var cookie = await SignInSessionCookieAsync();

        var crossOrigin = await UpgradeOutcomeAsync(cookie, "https://app.localhost");
        crossOrigin.Should().EndWith("status code: 403");

        // Positive control: the same upgrade from the server's own origin passes the check and
        // reaches routing, which has no WebSocket endpoint at the probe path.
        var sameOrigin = await UpgradeOutcomeAsync(cookie, "https://localhost");
        sameOrigin.Should().NotEndWith("status code: 403");
    }

    private async Task<string> SignInSessionCookieAsync()
    {
        var client = await GetClientAsync();
        client.BaseAddress = new Uri("https://localhost");
        await BrowserCsrf.SetHeaderAsync(client);
        using var signIn = await client.PostAsJsonAsync("/api/v1/admin/auth/session", new { password = "test-admin-key" });
        signIn.StatusCode.Should().Be(HttpStatusCode.NoContent);
        return signIn.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("honua_admin_session=", StringComparison.Ordinal))
            .Split(';')[0];
    }

    // The handshake outcome: "open", or the test host's incomplete-handshake message with its status.
    private async Task<string> UpgradeOutcomeAsync(string cookie, string origin)
    {
        var client = _fixture.CreateWebSocketClient();
        var inherited = client.ConfigureRequest;
        client.ConfigureRequest = request =>
        {
            inherited?.Invoke(request);
            request.Headers["Cookie"] = cookie;
            request.Headers["Origin"] = origin;
        };

        try
        {
            using var socket = await client.ConnectAsync(new Uri(WebSocketProbePath), CancellationToken.None);
            return "open";
        }
        catch (InvalidOperationException handshake)
        {
            return handshake.Message;
        }
    }
}
