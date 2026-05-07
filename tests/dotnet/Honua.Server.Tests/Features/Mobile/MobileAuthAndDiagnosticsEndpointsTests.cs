// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Mobile;

/// <summary>
/// Integration coverage for mobile runtime auth refresh and diagnostics upload endpoints (#924).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class MobileAuthAndDiagnosticsEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        });

    private HttpClient _anonymousClient = null!;
    private HttpClient _adminClient = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _anonymousClient = _fixture.CreateClient();
        _adminClient = _fixture.CreateAdminClient();
    }

    public async Task DisposeAsync()
    {
        _anonymousClient.Dispose();
        _adminClient.Dispose();
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Endpoint("POST /oauth/token")]
    public async Task TokenRefresh_WithDevelopmentRefreshToken_ReturnsBearerRefreshPayload()
    {
        using var response = await _anonymousClient.PostAsync(
            "/oauth/token",
            Json("""{"refreshToken":"refresh-token"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl?.NoStore.Should().BeTrue();

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("accessToken").GetString().Should().Be("live-image-test-bearer-token");
        body.RootElement.GetProperty("refreshToken").GetString().Should().Be("refresh-token");
        body.RootElement.GetProperty("tokenType").GetString().Should().Be("Bearer");
        body.RootElement.GetProperty("expiresIn").GetInt32().Should().Be(3600);
    }

    [IntegrationTest]
    [Endpoint("POST /oauth/token")]
    public async Task TokenRefresh_WithInvalidRefreshToken_ReturnsUnauthorized()
    {
        using var response = await _anonymousClient.PostAsync(
            "/oauth/token",
            Json("""{"refreshToken":"wrong-refresh-token"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("POST /api/mobile/exceptions")]
    public async Task ExceptionUpload_WithApiKey_ReturnsAccepted()
    {
        using var response = await _adminClient.PostAsync(
            "/api/mobile/exceptions",
            Json("""
                {
                  "id": "mobile-exception-test",
                  "source": "live-image-test",
                  "exceptionType": "System.InvalidOperationException",
                  "message": "token=secret",
                  "context": { "apiKey": "secret" }
                }
                """));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl?.NoStore.Should().BeTrue();

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("accepted").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("POST /api/mobile/exceptions")]
    public async Task ExceptionUpload_WithoutApiKey_ReturnsUnauthorized()
    {
        using var response = await _anonymousClient.PostAsync(
            "/api/mobile/exceptions",
            Json("""{"id":"mobile-exception-test","source":"live-image-test"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static StringContent Json(string payload)
        => new(payload, Encoding.UTF8, "application/json");
}
