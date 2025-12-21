// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Json;
using FluentAssertions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Server.Features.OgcFeatures.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Infrastructure;
using Xunit;

namespace Honua.Server.Tests;

/// <summary>
/// Integration tests for OGC API Features Core endpoints (landing page and conformance)
/// </summary>
[Protocol(Protocols.OgcApiFeatures)]
public sealed class OgcFeaturesEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        // Replace with test implementation for OGC collections tests
        _fixture.ReplaceService<ILayerCatalog>(new TestLayerCatalog());
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/features")]
    public async Task GetLandingPage_ReturnsValidLandingPageWithRequiredLinks()
    {
        // Arrange
        var client = _fixture.CreateClient();

        // Act
        var response = await client.GetAsync("/ogc/features");

        // Assert
        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var landingPage = await response.Content.ReadFromJsonAsync<LandingPage>();
        landingPage.Should().NotBeNull();
        landingPage!.Title.Should().NotBeNullOrEmpty();
        landingPage.Description.Should().NotBeNullOrEmpty();
        landingPage.Links.Should().NotBeEmpty();

        // Verify required links exist
        var links = landingPage.Links.ToArray();
        links.Should().Contain(l => l.Rel == RelationTypes.Self);
        links.Should().Contain(l => l.Rel == RelationTypes.ServiceDesc);
        links.Should().Contain(l => l.Rel == RelationTypes.Conformance);
        links.Should().Contain(l => l.Rel == RelationTypes.Data);

        // Verify link structure
        links.Where(l => !string.IsNullOrEmpty(l.Href)).Should().HaveCount(links.Length);
        links.Where(l => !string.IsNullOrEmpty(l.Type)).Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/features/conformance")]
    public async Task GetConformance_ReturnsValidConformanceDeclarationWithRequiredClasses()
    {
        // Arrange
        var client = _fixture.CreateClient();

        // Act
        var response = await client.GetAsync("/ogc/features/conformance");

        // Assert
        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var conformance = await response.Content.ReadFromJsonAsync<ConformanceDeclaration>();
        conformance.Should().NotBeNull();
        conformance!.ConformsTo.Should().NotBeEmpty();

        var conformanceClasses = conformance.ConformsTo.ToArray();

        // Verify required OGC API Features Core conformance classes
        conformanceClasses.Should().Contain("http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/core");
        conformanceClasses.Should().Contain("http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/oas30");
        conformanceClasses.Should().Contain("http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/html");
        conformanceClasses.Should().Contain("http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/geojson");

        // Verify OGC API Common conformance classes
        conformanceClasses.Should().Contain("http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/core");
        conformanceClasses.Should().Contain("http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/landing-page");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/features")]
    public async Task GetLandingPage_IncludesCorrectBaseUrl()
    {
        // Arrange
        var client = _fixture.CreateClient();

        // Act
        var response = await client.GetAsync("/ogc/features");

        // Assert
        response.Be200Ok();

        var landingPage = await response.Content.ReadFromJsonAsync<LandingPage>();
        landingPage.Should().NotBeNull();

        // Verify that links use the correct base URL
        var links = landingPage!.Links.ToArray();
        var selfLink = links.First(l => l.Rel == RelationTypes.Self);
        selfLink.Href.Should().EndWith("/ogc/features");
        selfLink.Href.Should().StartWith("http");

        var conformanceLink = links.First(l => l.Rel == RelationTypes.Conformance);
        conformanceLink.Href.Should().EndWith("/ogc/features/conformance");
    }

    // TODO: Add cache header tests when output caching is properly configured for test environment

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/features/collections")]
    public async Task GetCollections_ReturnsValidCollectionsWithLayers()
    {
        // Arrange
        var client = _fixture.CreateClient();

        // Act
        var response = await client.GetAsync("/ogc/features/collections");

        // Assert
        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var collections = await response.Content.ReadFromJsonAsync<Collections>();
        collections.Should().NotBeNull();
        collections!.CollectionList.Should().NotBeEmpty();
        collections.Links.Should().NotBeEmpty();

        // Verify required links exist
        var links = collections.Links.ToArray();
        links.Should().Contain(l => l.Rel == RelationTypes.Self);
        links.Should().Contain(l => l.Rel == "parent");

        // Verify collections have required properties
        var firstCollection = collections.CollectionList.First();
        firstCollection.Id.Should().NotBeNullOrEmpty();
        firstCollection.Links.Should().NotBeEmpty();
        firstCollection.ItemType.Should().Be("feature");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/features/collections/{collectionId}")]
    public async Task GetCollection_WithValidId_ReturnsCollectionMetadata()
    {
        // Arrange
        var client = _fixture.CreateClient();
        var collectionId = "0"; // Layer ID from TestLayerCatalog

        // Act
        var response = await client.GetAsync($"/ogc/features/collections/{collectionId}");

        // Assert
        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var collection = await response.Content.ReadFromJsonAsync<CollectionInfo>();
        collection.Should().NotBeNull();
        collection!.Id.Should().Be(collectionId);
        collection.Links.Should().NotBeEmpty();
        collection.ItemType.Should().Be("feature");

        // Verify required links exist
        var links = collection.Links.ToArray();
        links.Should().Contain(l => l.Rel == RelationTypes.Self);
        links.Should().Contain(l => l.Rel == RelationTypes.Data); // Items link
        links.Should().Contain(l => l.Rel == "parent");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/features/collections/{collectionId}")]
    public async Task GetCollection_WithInvalidId_Returns404()
    {
        // Arrange
        var client = _fixture.CreateClient();
        var invalidCollectionId = "nonexistent_layer";

        // Act
        var response = await client.GetAsync($"/ogc/features/collections/{invalidCollectionId}");

        // Assert
        response.Should().HaveStatusCode(System.Net.HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/features/collections")]
    public async Task GetCollections_IncludesCorrectBaseUrl()
    {
        // Arrange
        var client = _fixture.CreateClient();

        // Act
        var response = await client.GetAsync("/ogc/features/collections");

        // Assert
        response.Be200Ok();

        var collections = await response.Content.ReadFromJsonAsync<Collections>();
        collections.Should().NotBeNull();

        // Verify that links use the correct base URL
        var links = collections!.Links.ToArray();
        var selfLink = links.First(l => l.Rel == RelationTypes.Self);
        selfLink.Href.Should().EndWith("/ogc/features/collections");
        selfLink.Href.Should().StartWith("http");

        // Verify collection links are correct
        if (collections.CollectionList.Length > 0)
        {
            var firstCollection = collections.CollectionList.First();
            var collectionSelfLink = firstCollection.Links.First(l => l.Rel == RelationTypes.Self);
            collectionSelfLink.Href.Should().Contain($"/ogc/features/collections/{firstCollection.Id}");
        }
    }
}