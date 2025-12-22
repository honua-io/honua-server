// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Json;
using FluentAssertions;
using Honua.Server.Features.OgcFeatures.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
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
        // These endpoints don't require database or layer catalog services
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
}
