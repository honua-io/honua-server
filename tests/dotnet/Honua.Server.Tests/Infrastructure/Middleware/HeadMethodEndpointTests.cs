// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Infrastructure.Middleware;

/// <summary>
/// RFC 9110 §9.3.2 regression tests (#3389): before the shared HEAD middleware landed, the
/// server answered <c>HEAD</c> with 404 or 405 on every route that mapped only <c>GET</c>
/// (<c>/healthz/*</c>, <c>/ogc/features*</c>, <c>/wfs</c>, <c>/stac</c>, <c>/rest/services</c>,
/// <c>/api/v1/admin/*</c>). That broke GDAL/vsicurl, OWSLib, link checkers and proxies, and it
/// stalled the client-interop nightly whose compose healthcheck probes <c>/healthz/ready</c>
/// with <c>wget --spider</c> (a real HEAD).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Infrastructure)]
public sealed class HeadMethodEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    /// <summary>
    /// Asserts the HEAD response is byte-for-byte the GET response minus the body: same status,
    /// same content type, and a <c>Content-Length</c> equal to the number of bytes GET returned.
    /// </summary>
    private async Task AssertHeadMatchesGetAsync(string path)
    {
        var client = _fixture.CreateClient();

        using var getResponse = await client.GetAsync(path);
        var getBody = await getResponse.Content.ReadAsByteArrayAsync();

        using var headRequest = new HttpRequestMessage(HttpMethod.Head, path);
        using var headResponse = await client.SendAsync(headRequest);

        headResponse.StatusCode.Should().Be(
            getResponse.StatusCode,
            "HEAD must return the same status as GET for {0} (RFC 9110 §9.3.2)",
            path);

        var headBody = await headResponse.Content.ReadAsByteArrayAsync();
        headBody.Should().BeEmpty("a HEAD response must not carry content");

        headResponse.Content.Headers.ContentType?.ToString()
            .Should().Be(
                getResponse.Content.Headers.ContentType?.ToString(),
                "clients probe HEAD to learn the media type of {0}",
                path);

        headResponse.Content.Headers.ContentLength.Should().Be(
            getBody.Length,
            "clients (GDAL /vsicurl in particular) size the payload from the HEAD Content-Length of {0}",
            path);
    }

    [IntegrationTest]
    [Operation(Operations.LivenessCheck)]
    [Endpoint("GET /healthz/live")]
    public async Task Head_LivenessProbe_MatchesGetStatusWithEmptyBodyAndContentLength()
        => await AssertHeadMatchesGetAsync("/healthz/live");

    [IntegrationTest]
    [Operation(Operations.ReadinessCheck)]
    [Endpoint("GET /healthz/ready")]
    public async Task Head_ReadinessProbe_MatchesGetStatusWithEmptyBodyAndContentLength()
        => await AssertHeadMatchesGetAsync("/healthz/ready");

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Protocol(TestProtocols.OgcApiFeatures)]
    [Endpoint("GET /ogc/features")]
    public async Task Head_OgcFeaturesLandingPage_MatchesGetStatusWithEmptyBodyAndContentLength()
        => await AssertHeadMatchesGetAsync("/ogc/features");

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Protocol(TestProtocols.OgcApiFeatures)]
    [Endpoint("GET /ogc/features/collections")]
    public async Task Head_OgcFeaturesCollections_MatchesGetStatusWithEmptyBodyAndContentLength()
        => await AssertHeadMatchesGetAsync("/ogc/features/collections");

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Protocol(TestProtocols.OgcApiFeatures)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task Head_OgcFeaturesItems_MatchesGetStatusWithEmptyBodyAndContentLength()
        => await AssertHeadMatchesGetAsync(
            $"/ogc/features/collections/{WebAppFixture.TestLayerId}/items?limit=1");

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Protocol(TestProtocols.Wfs20)]
    [Endpoint("GET /wfs")]
    public async Task Head_WfsGetCapabilities_MatchesGetStatusWithEmptyBodyAndContentLength()
        => await AssertHeadMatchesGetAsync("/wfs?SERVICE=WFS&VERSION=2.0.0&REQUEST=GetCapabilities");

    [IntegrationTest]
    [Operation(Operations.StacCatalog)]
    [Protocol(TestProtocols.Stac)]
    [Endpoint("GET /stac")]
    public async Task Head_StacLandingPage_MatchesGetStatusWithEmptyBodyAndContentLength()
        => await AssertHeadMatchesGetAsync("/stac");

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Protocol(TestProtocols.GeoservicesCatalog)]
    [Endpoint("GET /rest/services")]
    public async Task Head_GeoServicesCatalog_MatchesGetStatusWithEmptyBodyAndContentLength()
        => await AssertHeadMatchesGetAsync("/rest/services?f=json");

    /// <summary>
    /// A HEAD request must not be able to reach a POST-only route: routing still fails on the
    /// method, so the genuine 405 is preserved.
    /// </summary>
    [IntegrationTest]
    public async Task Head_PostOnlyRoute_Returns405MethodNotAllowed()
    {
        var client = _fixture.CreateClient();
        var path = $"/ogc/features/collections/{WebAppFixture.TestLayerId}/clusters";

        // The GET this HEAD is rewritten to does not match either, so the method mismatch
        // still terminates in routing.
        using var getResponse = await client.GetAsync(path);
        getResponse.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);

        using var request = new HttpRequestMessage(HttpMethod.Head, path);
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(
            HttpStatusCode.MethodNotAllowed,
            "HEAD is only answered where GET is; a POST-only route must still refuse it");
    }

    /// <summary>
    /// Calculate is exposed as GET for GeoServices compatibility but performs a bulk update.
    /// HEAD must therefore stop before the handler instead of inheriting its GET semantics.
    /// </summary>
    [IntegrationTest]
    public async Task Head_CalculateRoute_Returns405MethodNotAllowed()
    {
        var client = _fixture.CreateClient();
        var path = $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/calculate";

        using var request = new HttpRequestMessage(HttpMethod.Head, path);
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        response.Content.Headers.Allow.Should().BeEquivalentTo(["GET", "POST"]);
    }

    /// <summary>
    /// Only HEAD changes: <c>HealthEndpoints.NonGetMethods</c> -&gt; <c>HandleGetMethodNotAllowed</c>
    /// must keep answering 405 for the mutating methods.
    /// </summary>
    [IntegrationTest]
    public async Task NonGetMethods_OnGetOnlyRoute_StillReturn405MethodNotAllowed()
    {
        var client = _fixture.CreateClient();

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch })
        {
            using var request = new HttpRequestMessage(method, "/healthz/live");
            using var response = await client.SendAsync(request);

            response.StatusCode.Should().Be(
                HttpStatusCode.MethodNotAllowed,
                "{0} /healthz/live must stay method-not-allowed",
                method.Method);
        }
    }

    /// <summary>
    /// An unknown path is still a 404 for HEAD — the rewrite must not turn a miss into a match.
    /// </summary>
    [IntegrationTest]
    public async Task Head_UnknownPath_Returns404NotFound()
    {
        var client = _fixture.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Head, "/ogc/features/this-route-does-not-exist");
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
