// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class AdminAuthEndpointsTests
{
    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/auth/session")]
    public async Task GetSession_ValidApiKeyWithSessionCookie_IsAuthenticated()
    {
        await SignInGrpcBrowserAsync();
        _client.DefaultRequestHeaders.Add("X-API-Key", "test-admin-key");

        var session = await _client.GetFromJsonAsync<AdminAuthSessionResponse>("/api/v1/admin/auth/session");

        session!.IsAuthenticated.Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/auth/session")]
    public async Task GetSession_InvalidApiKeyWithSessionCookie_PreservesBrowserSession()
    {
        await SignInGrpcBrowserAsync();
        _client.DefaultRequestHeaders.Add("X-API-Key", "invalid-key");

        var session = await _client.GetFromJsonAsync<AdminAuthSessionResponse>("/api/v1/admin/auth/session");

        session!.IsAuthenticated.Should().BeFalse();
        _client.DefaultRequestHeaders.Remove("X-API-Key");
        var restored = await _client.GetFromJsonAsync<AdminAuthSessionResponse>("/api/v1/admin/auth/session");
        restored!.IsAuthenticated.Should().BeTrue();
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Grpc)]
    [Endpoint("POST /geospatial.v1.SpecService/PlanSpec")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.SpecService/PlanSpec")]
    public async Task GrpcWeb_ExplicitApiKeyWithSessionCookie_ReachesServiceWithoutCsrf()
    {
        await SignInGrpcBrowserAsync();
        _client.DefaultRequestHeaders.Add("X-API-Key", "test-admin-key");

        using var response = await PostEmptyGrpcWebAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("grpc-status").Should().ContainSingle().Which.Should().Be("3");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Grpc)]
    [Endpoint("POST /geospatial.v1.SpecService/PlanSpec")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.SpecService/PlanSpec")]
    public async Task GrpcWeb_SessionCookieWithoutCsrf_ReturnsPermissionDeniedMetadata()
    {
        await SignInGrpcBrowserAsync();

        using var response = await PostEmptyGrpcWebAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/grpc-web");
        response.Headers.GetValues("grpc-status").Should().ContainSingle().Which.Should().Be("7");
        Uri.UnescapeDataString(response.Headers.GetValues("grpc-message").Single())
            .Should().Contain("X-Honua-CSRF");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Grpc)]
    [Endpoint("POST /geospatial.v1.SpecService/PlanSpec")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.SpecService/PlanSpec")]
    public async Task GrpcWeb_SessionCookieWithCsrf_ReachesService()
    {
        await SignInGrpcBrowserAsync();
        await SetCsrfHeaderAsync(_client);

        using var response = await PostEmptyGrpcWebAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("grpc-status").Should().ContainSingle().Which.Should().Be("3");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Grpc)]
    [Endpoint("POST /geospatial.v1.SpecService/PlanSpec")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.SpecService/PlanSpec")]
    public async Task GrpcWeb_SessionCookieWithInvalidCsrf_ReturnsPermissionDeniedMetadata()
    {
        await SignInGrpcBrowserAsync();
        _client.DefaultRequestHeaders.Add("X-Honua-CSRF", "invalid");

        using var response = await PostEmptyGrpcWebAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("grpc-status").Should().ContainSingle().Which.Should().Be("7");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Grpc)]
    [Endpoint("POST /geospatial.v1.SpecService/PlanSpec")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.SpecService/PlanSpec")]
    public async Task GrpcWeb_SessionCookieWithCrossSiteCsrf_ReturnsPermissionDeniedMetadata()
    {
        await SignInGrpcBrowserAsync();
        await SetCsrfHeaderAsync(_client);
        _client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");

        using var response = await PostEmptyGrpcWebAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("grpc-status").Should().ContainSingle().Which.Should().Be("7");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Grpc)]
    [Endpoint("POST /geospatial.v1.SpecService/PlanSpec")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.SpecService/PlanSpec")]
    public async Task GrpcWeb_ExplicitBearerWithSessionCookie_ReachesServiceWithoutCsrf()
    {
        await using var fixture = CreatePasswordTokenFixture();
        await fixture.InitializeAsync();
        using var browser = fixture.CreateClient();
        await SignInGrpcBrowserAsync(browser);
        await SetCsrfHeaderAsync(browser);
        using var issuance = await browser.PostAsync("/api/v1/admin/auth/bearer", null);
        issuance.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = await issuance.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminOperatorBearerResponse);
        browser.DefaultRequestHeaders.Remove("X-Honua-CSRF");
        browser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);

        using var response = await PostEmptyGrpcWebAsync(browser);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("grpc-status").Should().ContainSingle().Which.Should().Be("3");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Grpc)]
    [Endpoint("POST /geospatial.v1.SpecService/PlanSpec")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.SpecService/PlanSpec")]
    public async Task GrpcWeb_ExplicitPortalBearerWithSessionCookie_ReachesServiceWithoutCsrf()
    {
        await SignInGrpcBrowserAsync();
        await SetCsrfHeaderAsync(_client);
        _client.DefaultRequestHeaders.Referrer = new Uri("https://localhost/");
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "admin",
            ["password"] = "test-admin-key",
            ["client"] = "referer",
            ["referer"] = "https://localhost/",
            ["f"] = "json",
        });
        using var issuance = await _client.PostAsync("/sharing/rest/generateToken", content);
        issuance.StatusCode.Should().Be(HttpStatusCode.OK);
        _client.DefaultRequestHeaders.Remove("X-Honua-CSRF");
        using var token = System.Text.Json.JsonDocument.Parse(await issuance.Content.ReadAsStringAsync());
        token.RootElement.TryGetProperty("token", out var bearer).Should().BeTrue();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", bearer.GetString());

        using var response = await PostEmptyGrpcWebAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("grpc-status").Should().ContainSingle().Which.Should().Be("3");
    }

    [IntegrationTheory]
    [InlineData("/api/v1/admin/auth/logout")]
    [InlineData("/api/v1/admin/auth/bearer")]
    [InlineData("/api/v1/admin/auth/providers/generic/authorize-url")]
    [Endpoint("POST /api/v1/admin/auth/logout")]
    [Endpoint("POST /api/v1/admin/auth/bearer")]
    [Endpoint("POST /api/v1/admin/auth/providers/{providerKey}/authorize-url")]
    public async Task SessionOperation_ExplicitApiKeyWithSessionCookie_StillRequiresCsrf(string path)
    {
        await SignInGrpcBrowserAsync();
        _client.DefaultRequestHeaders.Add("X-API-Key", "test-admin-key");

        using var response = await _client.PostAsync(path, null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private Task SignInGrpcBrowserAsync() => SignInGrpcBrowserAsync(_client);

    private static async Task SignInGrpcBrowserAsync(HttpClient browser)
    {
        browser.BaseAddress = new Uri("https://localhost");
        await SetCsrfHeaderAsync(browser);
        using var response = await browser.PostAsJsonAsync("/api/v1/admin/auth/session", new { password = "test-admin-key" });
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        browser.DefaultRequestHeaders.Remove("X-Honua-CSRF");
    }

    private Task<HttpResponseMessage> PostEmptyGrpcWebAsync() => PostEmptyGrpcWebAsync(_client);

    private static async Task<HttpResponseMessage> PostEmptyGrpcWebAsync(HttpClient browser)
    {
        using var content = new ByteArrayContent([0, 0, 0, 0, 0]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc-web+proto");
        return await browser.PostAsync("/geospatial.v1.SpecService/PlanSpec", content);
    }
}
