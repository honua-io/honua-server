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
        var client = await SignInGrpcBrowserAsync();
        client.DefaultRequestHeaders.Add("X-API-Key", "test-admin-key");

        var session = await client.GetFromJsonAsync<AdminAuthSessionResponse>("/api/v1/admin/auth/session");

        session!.IsAuthenticated.Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/auth/session")]
    public async Task GetSession_InvalidApiKeyWithSessionCookie_PreservesBrowserSession()
    {
        var client = await SignInGrpcBrowserAsync();
        client.DefaultRequestHeaders.Add("X-API-Key", "invalid-key");

        var session = await client.GetFromJsonAsync<AdminAuthSessionResponse>("/api/v1/admin/auth/session");

        session!.IsAuthenticated.Should().BeFalse();
        client.DefaultRequestHeaders.Remove("X-API-Key");
        var restored = await client.GetFromJsonAsync<AdminAuthSessionResponse>("/api/v1/admin/auth/session");
        restored!.IsAuthenticated.Should().BeTrue();
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Grpc)]
    [Endpoint("POST /geospatial.v1.SpecService/PlanSpec")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.SpecService/PlanSpec")]
    public async Task GrpcWeb_ExplicitApiKeyWithSessionCookie_ReachesServiceWithoutCsrf()
    {
        var client = await SignInGrpcBrowserAsync();
        client.DefaultRequestHeaders.Add("X-API-Key", "test-admin-key");

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
        var client = await SignInGrpcBrowserAsync();
        await BrowserCsrf.SetHeaderAsync(client);

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
        var client = await SignInGrpcBrowserAsync();
        client.DefaultRequestHeaders.Add("X-Honua-CSRF", "invalid");

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
        var client = await SignInGrpcBrowserAsync();
        await BrowserCsrf.SetHeaderAsync(client);
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");

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
        await BrowserCsrf.SetHeaderAsync(browser);
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
    [Endpoint("POST /geospatial.v1.FeatureService/QueryFeatures")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/QueryFeatures")]
    public async Task GrpcWeb_ExplicitPortalBearerWithSessionCookie_ReachesServiceWithoutCsrf()
    {
        var client = await SignInGrpcBrowserAsync();
        await BrowserCsrf.SetHeaderAsync(client);
        client.DefaultRequestHeaders.Referrer = new Uri("https://localhost/");
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "admin",
            ["password"] = "test-admin-key",
            ["client"] = "referer",
            ["referer"] = "https://localhost/",
            ["f"] = "json",
        });
        using var issuance = await client.PostAsync("/sharing/rest/generateToken", content);
        issuance.StatusCode.Should().Be(HttpStatusCode.OK);
        client.DefaultRequestHeaders.Remove("X-Honua-CSRF");
        using var token = System.Text.Json.JsonDocument.Parse(await issuance.Content.ReadAsStringAsync());
        token.RootElement.TryGetProperty("token", out var bearer).Should().BeTrue();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", bearer.GetString());

        using var response = await PostEmptyGrpcWebAsync();

        // The administrative Spec service requires an operator credential even for a valid portal token.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var query = await PostEmptyGrpcWebAsync(client,
            "/geospatial.v1.FeatureService/QueryFeatures");
        query.StatusCode.Should().Be(HttpStatusCode.OK);
        query.Headers.GetValues("grpc-status").Should().ContainSingle().Which.Should().Be("3");
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
        var client = await SignInGrpcBrowserAsync();
        client.DefaultRequestHeaders.Add("X-API-Key", "test-admin-key");

        using var response = await client.PostAsync(path, null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<HttpClient> SignInGrpcBrowserAsync()
    {
        var client = await GetClientAsync();
        await SignInGrpcBrowserAsync(client);
        return client;
    }

    private static async Task SignInGrpcBrowserAsync(HttpClient browser)
    {
        browser.BaseAddress = new Uri("https://localhost");
        await BrowserCsrf.SetHeaderAsync(browser);
        using var response = await browser.PostAsJsonAsync("/api/v1/admin/auth/session", new { password = "test-admin-key" });
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        browser.DefaultRequestHeaders.Remove("X-Honua-CSRF");
    }

    private async Task<HttpResponseMessage> PostEmptyGrpcWebAsync() => await PostEmptyGrpcWebAsync(await GetClientAsync());

    private static async Task<HttpResponseMessage> PostEmptyGrpcWebAsync(
        HttpClient browser, string path = "/geospatial.v1.SpecService/PlanSpec")
    {
        using var content = new ByteArrayContent([0, 0, 0, 0, 0]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc-web+proto");
        return await browser.PostAsync(path, content);
    }
}
