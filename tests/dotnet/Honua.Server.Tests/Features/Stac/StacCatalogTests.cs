// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Stac;

/// <summary>
/// Integration tests for the STAC Catalog (root) endpoint.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Stac)]
public sealed class StacCatalogTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.StacCatalog)]
    [Endpoint("GET /stac")]
    public async Task GetCatalog_ReturnsValidStacCatalog()
    {
        var response = await _fixture.Client.GetAsync("/stac");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType
            .Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("type").GetString().Should().Be("Catalog");
        json.RootElement.GetProperty("id").GetString().Should().NotBeNullOrEmpty();
        json.RootElement.GetProperty("stac_version").GetString().Should().Be("1.0.0");
        json.RootElement.GetProperty("description").GetString().Should().NotBeNullOrEmpty();
        json.RootElement.GetProperty("links").EnumerateArray().Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.StacCatalog)]
    [Endpoint("GET /stac")]
    public async Task GetCatalog_ReturnsStrongETagAndSupportsConditionalRequest()
    {
        var firstResponse = await _fixture.Client.GetAsync("/stac");

        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        firstResponse.Headers.ETag.Should().NotBeNull();
        firstResponse.Headers.ETag!.IsWeak.Should().BeFalse();
        firstResponse.Headers.ETag!.Tag.Should().NotBeNullOrWhiteSpace();

        using var conditionalRequest = new HttpRequestMessage(HttpMethod.Get, "/stac");
        conditionalRequest.Headers.TryAddWithoutValidation("If-None-Match", firstResponse.Headers.ETag.ToString());

        var secondResponse = await _fixture.Client.SendAsync(conditionalRequest);

        secondResponse.StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    [IntegrationTest]
    [Operation(Operations.StacCatalog)]
    [Endpoint("GET /stac")]
    public async Task GetCatalog_ContainsConformanceClasses()
    {
        var response = await _fixture.Client.GetAsync("/stac");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        var conformsTo = json.RootElement.GetProperty("conformsTo")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToList();

        conformsTo.Should().Contain("https://api.stacspec.org/v1.0.0/core");
        conformsTo.Should().Contain("https://api.stacspec.org/v1.0.0/item-search");
        conformsTo.Should().Contain("https://api.stacspec.org/v1.0.0/collections");
    }

    [IntegrationTest]
    [Operation(Operations.StacCatalog)]
    [Endpoint("GET /stac")]
    public async Task GetCatalog_HasSearchAndCollectionsLinks()
    {
        var response = await _fixture.Client.GetAsync("/stac");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        var links = json.RootElement.GetProperty("links")
            .EnumerateArray()
            .Select(l => l.GetProperty("rel").GetString()!)
            .ToList();

        links.Should().Contain("self");
        links.Should().Contain("root");
        links.Should().Contain("search");
        links.Should().Contain("data");
    }

    [IntegrationTest]
    [Operation(Operations.StacCatalog)]
    [Endpoint("GET /stac")]
    public async Task GetCatalog_DoesNotAdvertiseOgcFeaturesOpenApiAsServiceDescription()
    {
        var response = await _fixture.Client.GetAsync("/stac");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        var serviceDescLinks = json.RootElement.GetProperty("links")
            .EnumerateArray()
            .Where(link => string.Equals(
                link.GetProperty("rel").GetString(),
                "service-desc",
                StringComparison.Ordinal))
            .ToArray();

        serviceDescLinks.Should().BeEmpty();
    }
}
