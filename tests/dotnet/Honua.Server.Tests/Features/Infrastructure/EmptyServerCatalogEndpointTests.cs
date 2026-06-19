// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Infrastructure;

/// <summary>
/// Regression tests for honua-server#1619 — on a freshly deployed server with zero
/// published datasets, catalog discovery endpoints (<c>GET /stac</c>,
/// <c>GET /rest/services</c>, <c>GET /ogc/features</c>,
/// <c>GET /ogc/features/collections</c>, and <c>GET /odata</c>) previously returned
/// HTTP 500 with "No Metadata v2 snapshot has been activated for environment
/// 'Production'". The correct behaviour is HTTP 200 with a valid empty response so
/// evaluators who deploy before importing and uptime probes both see a healthy server.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Infrastructure)]
public sealed class EmptyServerCatalogEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture;

    public EmptyServerCatalogEndpointTests()
    {
        // Provide an in-memory graph with zero services/resources/publications to
        // simulate a freshly deployed server that has never had a dataset published.
        var emptyGraph = new TestMetadataV2GraphBuilder().Build();

        _fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IMetadataV2GraphProvider>();
                services.RemoveAll<IMetadataV2GraphStore>();
                var provider = new TestMetadataV2GraphProvider(emptyGraph);
                services.AddSingleton(provider);
                services.AddSingleton<IMetadataV2GraphProvider>(provider);
                services.AddSingleton<IMetadataV2GraphStore>(provider);
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    // --- STAC ---

    [IntegrationTest]
    [Operation(Operations.StacCatalog)]
    [Endpoint("GET /stac")]
    public async Task GetStacCatalog_FreshServer_Returns200WithEmptyChildLinks()
    {
        // Arrange
        var client = _fixture.CreateClient();

        // Act
        var response = await client.GetAsync("/stac");

        // Assert — must be 200, not 500 (honua-server#1619)
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a fresh server with no datasets must return a valid empty STAC catalog, not 500");

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        // Valid STAC catalog structure
        root.GetProperty("type").GetString().Should().Be("Catalog");
        root.GetProperty("id").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("description").GetString().Should().NotBeNullOrEmpty();

        // Links array must exist and contain the mandatory self/root/conformance links
        root.GetProperty("links").GetArrayLength().Should().BeGreaterThan(0,
            "even an empty STAC catalog must have self, root, conformance, and search links");

        // No child collection links — zero datasets
        var links = root.GetProperty("links").EnumerateArray().ToArray();
        var childLinks = links.Where(l =>
        {
            if (!l.TryGetProperty("rel", out var rel))
            {
                return false;
            }

            return rel.GetString() == "child";
        }).ToArray();
        childLinks.Should().BeEmpty("an empty server should not advertise any child collection links");
    }

    [IntegrationTest]
    [Operation(Operations.StacCatalog)]
    [Endpoint("GET /stac")]
    public async Task GetStacCatalog_FreshServer_ReturnsValidContentType()
    {
        // Arrange
        var client = _fixture.CreateClient();

        // Act
        var response = await client.GetAsync("/stac");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json",
            "STAC catalog must be returned as JSON");
    }

    // --- GeoServices /rest/services ---

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services")]
    public async Task GetRestServices_FreshServer_Returns200WithEmptyServicesList()
    {
        // Arrange
        var client = _fixture.CreateClient();

        // Act
        var response = await client.GetAsync("/rest/services");

        // Assert — must be 200, not 500 (honua-server#1619)
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a fresh server with no datasets must return a valid empty services directory, not 500");

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        // Valid GeoServices directory structure.
        // Honua does not advertise an ArcGIS Server version (see NoArcGisServerVersionTests).
        root.TryGetProperty("currentVersion", out _).Should().BeFalse();
        root.GetProperty("services").GetArrayLength().Should().Be(0,
            "an empty server should advertise zero services");
        root.GetProperty("folders").GetArrayLength().Should().Be(0,
            "an empty server should advertise zero folders");
    }

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services")]
    public async Task GetRestServices_FreshServer_ReturnsValidJson()
    {
        // Arrange
        var client = _fixture.CreateClient();

        // Act
        var response = await client.GetAsync("/rest/services");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json",
            "GeoServices directory must be returned as JSON");

        var content = await response.Content.ReadAsStringAsync();
        var act = () => JsonDocument.Parse(content);
        act.Should().NotThrow("response body must be valid JSON");
    }

    // --- OGC API Features landing page ---

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/features")]
    public async Task GetOgcFeaturesLandingPage_FreshServer_Returns200WithValidLinks()
    {
        // Arrange
        var client = _fixture.CreateClient();

        // Act
        var response = await client.GetAsync("/ogc/features");

        // Assert — must be 200, not 500 (honua-server#1619)
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a fresh server with no datasets must return a valid OGC API landing page, not 500");

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        // OGC API - Features Core requires title/description/links on the landing page.
        root.GetProperty("links").GetArrayLength().Should().BeGreaterThan(0,
            "the landing page must always carry self/conformance/collections links");
    }

    // --- OGC API Features collections ---

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/features/collections")]
    public async Task GetOgcFeaturesCollections_FreshServer_Returns200WithEmptyCollections()
    {
        // Arrange
        var client = _fixture.CreateClient();

        // Act
        var response = await client.GetAsync("/ogc/features/collections");

        // Assert — must be 200, not 500 (honua-server#1619)
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a fresh server with no datasets must return a valid empty collections document, not 500");

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        root.GetProperty("collections").GetArrayLength().Should().Be(0,
            "an empty server should advertise zero collections");
        root.GetProperty("links").GetArrayLength().Should().BeGreaterThan(0,
            "even an empty collections document must carry a self link");
    }

    // --- OData service document ---

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /odata")]
    public async Task GetODataServiceDocument_FreshServer_Returns404NotFiveHundred()
    {
        // The OData service document's documented empty-state contract is a 404 OData
        // error ("OData is not enabled for any available service") — see
        // ODataMetadataHandler.HandleGetServiceDocument and the endpoint's Produces(404).
        // Before honua-server#1619 a fresh server never reached that contract: resolving
        // publications threw and the endpoint returned 500. Pin the deliberate 404 here.

        // Arrange
        var client = _fixture.CreateClient();

        // Act
        var response = await client.GetAsync("/odata");

        // Assert — must be the documented 404 OData error, never 500 (honua-server#1619)
        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a fresh server with no datasets must surface OData's documented not-enabled error, not 500");

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        doc.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("ResourceNotFound", "the body must be a valid OData v4 error document");
    }
}
