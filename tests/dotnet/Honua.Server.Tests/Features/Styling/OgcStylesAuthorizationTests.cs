// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Styling;

/// <summary>
/// Authorization depth for the styling mutation surface (#2983). The default test
/// fixture runs with the HONUA_DEV_AUTH bypass, so these tests boot a host with
/// real API-key authentication (mirroring <c>AdminAuthorizationTests</c>) and prove
/// that PUT/POST/DELETE /ogc/styles and the style-suggestion endpoint reject
/// missing and invalid credentials while the read surface stays public, and that
/// a correctly authenticated mutation is subject to the shared request-body size
/// guard (413).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.OgcApiStyles)]
public sealed class OgcStylesAuthorizationTests : IAsyncLifetime
{
    private const string AdminPassword = "ogc-styles-auth-test-key";
    private const string MapboxStyleMediaType = "application/vnd.mapbox.style+json";
    private const long MaxUploadBytes = 2048;

    private readonly WebAppFixture _fixture;
    private HttpClient _anonymousClient = null!;

    public OgcStylesAuthorizationTests()
    {
        _fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.UseSetting(
                    "Limits:MaxUploadSizeBytes",
                    MaxUploadBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _anonymousClient = _fixture.CreateClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task PutStyle_WithoutApiKey_Returns401()
    {
        using var content = new StringContent("{}", Encoding.UTF8, MapboxStyleMediaType);
        var response = await _anonymousClient.PutAsync("/ogc/styles/any-style", content);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task PutStyle_WithInvalidApiKey_Returns401()
    {
        var client = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", "not-the-admin-key"));

        using var content = new StringContent("{}", Encoding.UTF8, MapboxStyleMediaType);
        var response = await client.PutAsync("/ogc/styles/any-style", content);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/styles")]
    public async Task PostStyle_WithoutApiKey_Returns401()
    {
        using var content = new StringContent("{}", Encoding.UTF8, MapboxStyleMediaType);
        var response = await _anonymousClient.PostAsync("/ogc/styles", content);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("DELETE /ogc/styles/{styleId}")]
    public async Task DeleteStyle_WithoutApiKey_Returns401()
    {
        var response = await _anonymousClient.DeleteAsync("/ogc/styles/any-style");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/metadata/layers/{layerId}/suggest-style")]
    public async Task SuggestStyle_WithoutApiKey_Returns401()
    {
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await _anonymousClient.PostAsync("/api/v1/admin/metadata/layers/0/suggest-style", content);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/styles")]
    public async Task GetStylesList_WithoutApiKey_RemainsPubliclyReadable()
    {
        // Contrast case: only manage-styles mutations are admin-gated; the read
        // surface stays anonymous even with real API-key auth enforced.
        var response = await _anonymousClient.GetAsync("/ogc/styles");

        response.Be200Ok();
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/styles")]
    public async Task PostStyle_Authenticated_BodyOverUploadLimit_Returns413()
    {
        // Correct credentials (proving the key is accepted) but a body larger than
        // Limits:MaxUploadSizeBytes: the shared RequestBodySizeGuard must reject it.
        var client = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));

        var padding = new string('a', (int)MaxUploadBytes * 2);
        var oversized = $"{{\"version\":8,\"comment\":\"{padding}\",\"layers\":[]}}";
        using var content = new StringContent(oversized, Encoding.UTF8, MapboxStyleMediaType);
        var response = await client.PostAsync("/ogc/styles", content);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }
}
