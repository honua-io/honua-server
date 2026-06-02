// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;

using FluentAssertions;

using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Maps;

/// <summary>
/// Tests for OGC Maps conformance endpoint, verifying all declared conformance classes.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.OgcApiMaps)]
public class OgcMapsConformanceTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/maps/conformance")]
    public async Task GetConformance_ReturnsOk()
    {
        var response = await _fixture.Client.GetAsync("/ogc/maps/conformance");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/maps/conformance")]
    public async Task GetConformance_IncludesConformsToArray()
    {
        var response = await _fixture.Client.GetAsync("/ogc/maps/conformance");
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        json.RootElement.TryGetProperty("conformsTo", out var conformsTo).Should().BeTrue();
        conformsTo.ValueKind.Should().Be(JsonValueKind.Array);
        conformsTo.EnumerateArray().Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/maps/conformance")]
    public async Task GetConformance_IncludesCoreConformance()
    {
        var classes = await GetConformanceClassesAsync();

        classes.Should().Contain("https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/core",
            "must declare OGC API - Maps Core conformance");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/maps/conformance")]
    public async Task GetConformance_DoesNotOverclaimCommonLandingAndOpenApiConformance()
    {
        var classes = await GetConformanceClassesAsync();

        classes.Should().NotContain("https://www.opengis.net/spec/ogcapi-common-1/1.0/conf/landing-page");
        classes.Should().NotContain("https://www.opengis.net/spec/ogcapi-common-1/1.0/conf/html");
        classes.Should().NotContain("https://www.opengis.net/spec/ogcapi-common-1/1.0/conf/oas30");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/maps/conformance")]
    public async Task GetConformance_IncludesCollectionMapConformance()
    {
        var classes = await GetConformanceClassesAsync();

        classes.Should().Contain("https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/collection-map",
            "must declare Collection Map conformance");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/maps/conformance")]
    public async Task GetConformance_IncludesDatasetMapConformance()
    {
        var classes = await GetConformanceClassesAsync();

        classes.Should().Contain("https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/dataset-map",
            "must declare Dataset Map conformance");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/maps/conformance")]
    public async Task GetConformance_DoesNotOverclaimBackgroundConformance()
    {
        var classes = await GetConformanceClassesAsync();

        classes.Should().NotContain("https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/background",
            "background parameters are parsed but not applied by the raster renderer");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/maps/conformance")]
    public async Task GetConformance_IncludesCollectionsSelectionConformance()
    {
        var classes = await GetConformanceClassesAsync();

        classes.Should().Contain("https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/collections-selection",
            "must declare Collections Selection conformance for the collections param");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/maps/conformance")]
    public async Task GetConformance_IncludesDatetimeConformance()
    {
        var classes = await GetConformanceClassesAsync();

        classes.Should().Contain("https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/datetime",
            "datetime is now enforced and rendered by the raster map pipeline");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/maps/conformance")]
    public async Task GetConformance_ClaimsStyledMapConformance()
    {
        var classes = await GetConformanceClassesAsync();

        classes.Should().Contain("https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/styled-map",
            "styled-map rendering of vector collections is now supported via the Skia pipeline (ADR-0048)");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/maps/conformance")]
    public async Task GetConformance_IncludesFormatConformance()
    {
        var classes = await GetConformanceClassesAsync();

        classes.Should().Contain("https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/png",
            "must declare PNG conformance");
        classes.Should().Contain("https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/jpeg",
            "must declare JPEG conformance");
        classes.Should().Contain("https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/tiff",
            "must declare TIFF conformance");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/maps/conformance")]
    public async Task GetConformance_IncludesSpatialConformance()
    {
        var classes = await GetConformanceClassesAsync();

        classes.Should().Contain("https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/crs",
            "must declare CRS conformance");
        classes.Should().NotContain("https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/bbox",
            "OGC API Maps does not define a bbox conformance class");
        classes.Should().NotContain("https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/spatial-subsetting",
            "the generic subset dimension parameter is not implemented");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/maps/conformance")]
    public async Task GetConformance_IncludesRenderingConformance()
    {
        var classes = await GetConformanceClassesAsync();

        classes.Should().Contain("https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/scaling",
            "must declare Scaling conformance");
    }

    private async Task<string[]> GetConformanceClassesAsync()
    {
        var response = await _fixture.Client.GetAsync("/ogc/maps/conformance");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("conformsTo")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToArray();
    }
}
