// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Stac;

/// <summary>
/// Integration tests for the STAC Catalog (root) endpoint.
/// </summary>
[Protocol(TestProtocols.Stac)]
[Collection("Database")]
public sealed class StacCatalogTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;

    public StacCatalogTests(WebAppFixture fixture) => _fixture = fixture;

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
    public async Task GetCatalog_AdvertisesValidatorRequiredHypermediaLinks()
    {
        var response = await _fixture.Client.GetAsync("/stac");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        var links = json.RootElement.GetProperty("links")
            .EnumerateArray()
            .ToArray();

        links.Should().Contain(link =>
            link.GetProperty("rel").GetString() == "service-desc" &&
            link.GetProperty("href").GetString()!.EndsWith("/stac/openapi.json", StringComparison.Ordinal));
        links.Should().Contain(link =>
            link.GetProperty("rel").GetString() == "service-doc");
        links.Should().Contain(link =>
            link.GetProperty("rel").GetString() == "conformance" &&
            link.GetProperty("href").GetString()!.EndsWith("/stac/conformance", StringComparison.Ordinal));
    }

    /// <summary>
    /// STAC Item Search Filter Extension (honua-server#1932): the landing page MUST advertise the
    /// catalog queryables document via the OGC queryables rel link
    /// (http://www.opengis.net/def/rel/ogc/1.0/queryables) so clients and stac-api-validator can
    /// discover the filterable property set. The href must point at /stac/queryables.
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.StacCatalog)]
    [Endpoint("GET /stac")]
    public async Task GetCatalog_AdvertisesQueryablesRelLink()
    {
        var response = await _fixture.Client.GetAsync("/stac");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("links")
            .EnumerateArray()
            .Should()
            .Contain(link =>
                link.GetProperty("rel").GetString() == "http://www.opengis.net/def/rel/ogc/1.0/queryables" &&
                link.GetProperty("href").GetString()!.EndsWith("/stac/queryables", StringComparison.Ordinal));
    }

    [IntegrationTest]
    [Operation(Operations.StacCatalog)]
    [Endpoint("GET /stac/conformance")]
    public async Task GetConformance_ReturnsStacConformanceClasses()
    {
        var response = await _fixture.Client.GetAsync("/stac/conformance");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        var conformsTo = json.RootElement.GetProperty("conformsTo")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToList();

        conformsTo.Should().Contain("https://api.stacspec.org/v1.0.0/core");
        conformsTo.Should().Contain("https://api.stacspec.org/v1.0.0/ogcapi-features");
        json.RootElement.GetProperty("links")
            .EnumerateArray()
            .Should()
            .Contain(link => link.GetProperty("rel").GetString() == "self");
    }

    [IntegrationTest]
    [Operation(Operations.StacCatalog)]
    [Endpoint("GET /stac/openapi.json")]
    public async Task GetOpenApiSpec_ReturnsStacApiDescription()
    {
        var response = await _fixture.Client.GetAsync("/stac/openapi.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("openapi").GetString().Should().StartWith("3.");
        json.RootElement.GetProperty("paths").TryGetProperty("/stac", out _).Should().BeTrue();
        json.RootElement.GetProperty("paths").TryGetProperty("/stac/search", out _).Should().BeTrue();
    }

    /// <summary>
    /// STAC API spec §7.2: the landing page links array MUST contain at least one rel=search link
    /// for each supported HTTP method on /stac/search, with the corresponding "method" field set so
    /// clients know which verbs are available.
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.StacCatalog)]
    [Endpoint("GET /stac")]
    public async Task GetCatalog_EmitsBothGetAndPostSearchLinksWithMethodField()
    {
        var response = await _fixture.Client.GetAsync("/stac");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        var searchLinks = json.RootElement.GetProperty("links")
            .EnumerateArray()
            .Where(l => l.TryGetProperty("rel", out var rel) &&
                        rel.GetString() == "search")
            .ToArray();

        searchLinks.Should().HaveCountGreaterOrEqualTo(2, "both GET and POST search links are required");

        searchLinks.Any(link => link.TryGetProperty("method", out var getMethod) && getMethod.GetString() == "GET")
            .Should().BeTrue("a rel=search link with method=GET must be present");

        searchLinks.Any(link => link.TryGetProperty("method", out var postMethod) && postMethod.GetString() == "POST")
            .Should().BeTrue("a rel=search link with method=POST must be present");
    }
}
