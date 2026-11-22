// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Maps;

[Collection("Database.OgcApiTiles")]
[Protocol(TestProtocols.OgcApiMaps)]
public sealed class OgcMapsConformanceContractTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData("core")]
    [InlineData("landing-page")]
    [InlineData("json")]
    [InlineData("oas30")]
    [Endpoint("GET /ogc/maps/conformance")]
    [Operation(Operations.Metadata)]
    public async Task GetConformance_DeclaresCommonClasses(string conformanceClass)
    {
        using var response = await _fixture.Client.GetAsync("/ogc/maps/conformance");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("conformsTo").EnumerateArray().Select(value => value.GetString())
            .Should().Contain("http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/" + conformanceClass);
    }

    [IntegrationTheory]
    [InlineData("/ogc/maps/conformance")]
    [InlineData("/ogc/maps/collections/0/map?bbox=-180,-90,180,90")]
    [Endpoint("GET /ogc/maps/conformance")]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    [Operation(Operations.ErrorHandling)]
    public async Task UnsupportedAccept_ReturnsNotAcceptable(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.ParseAdd("application/xml");
        using var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.NotAcceptable);
    }

    [IntegrationTheory]
    [InlineData("EPSG:4326", "http://www.opengis.net/def/crs/EPSG/0/4326", "-5,-10,15,20")]
    [InlineData("CRS84", "http://www.opengis.net/def/crs/OGC/1.3/CRS84", "-10,-5,20,15")]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    [Operation(Operations.Render)]
    public async Task GetCollectionMap_ContentCrsPreservesIdentifier(string crs, string identifier, string bbox)
    {
        using var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{WebAppFixture.TestLayerId}/map?bbox=-10,-5,20,15&crs={Uri.EscapeDataString(crs)}&width=16&height=16");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("Content-Crs").Single().Should().Be($"<{identifier}>");
        response.Headers.GetValues("Content-Bbox").Single().Should().Be(bbox);
    }

    [IntegrationTheory]
    [InlineData("unknown=value", HttpStatusCode.BadRequest)]
    [InlineData("f=xml", HttpStatusCode.BadRequest)]
    [Endpoint("GET /ogc/maps/conformance")]
    [Operation(Operations.ErrorHandling)]
    public async Task GetConformance_ValidatesQuery(string query, HttpStatusCode expected)
    {
        using var response = await _fixture.Client.GetAsync("/ogc/maps/conformance?" + query);
        response.StatusCode.Should().Be(expected);
    }

}
