// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Ogc;

/// <summary>
/// Integration tests for shared OGC OpenAPI content negotiation.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.OgcApiFeatures)]
public sealed class OpenApiSpecNegotiationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /openapi.json")]
    public async Task GetOpenApi_WithUnacceptableAcceptHeader_ReturnsNotAcceptable()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/openapi.json");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json", 0));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));

        using var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotAcceptable);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /openapi.json")]
    public async Task GetOpenApi_WithJsonCompatibleAcceptHeader_ReturnsOpenApiDocument()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/openapi.json");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/*+json"));

        using var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().StartWith("application/vnd.oai.openapi+json");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /openapi.json")]
    public async Task GetOpenApi_WithOnlyZeroQualityAcceptHeader_ReturnsNotAcceptable()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/openapi.json");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json", 0));

        using var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotAcceptable);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/features")]
    public async Task GetLandingPage_WithOnlyZeroQualityAcceptHeader_ReturnsNotAcceptable()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/ogc/features");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json", 0));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html", 0));

        using var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotAcceptable);
    }
}
