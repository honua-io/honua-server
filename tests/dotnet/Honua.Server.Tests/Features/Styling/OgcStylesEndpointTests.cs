// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Styling;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Styling;

/// <summary>
/// Integration tests for the OGC API - Styles Phase 1 adapter (ADR-0048, issue #1388).
/// Covers landing/styles list, conformance, content-negotiated stylesheets (MapLibre +
/// derived SLD), style metadata, manage-styles PUT (with strict validation), and the
/// 501 responses for standalone POST-create / DELETE.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.OgcApiStyles)]
public sealed class OgcStylesEndpointTests : IAsyncLifetime
{
    private const string MapboxStyleMediaType = "application/vnd.mapbox.style+json";
    private const string Sld10MediaType = "application/vnd.ogc.sld+xml;version=1.0";
    private const string Sld11MediaType = "application/vnd.ogc.sld+xml;version=1.1";

    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/styles/conformance")]
    public async Task GetConformance_ListsThePhase1ConformanceClasses()
    {
        var client = _fixture.CreateClient();

        var response = await client.GetAsync("/ogc/styles/conformance");

        response.Be200Ok();
        var payload = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(payload);
        var classes = document.RootElement.GetProperty("conformsTo")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();

        classes.Should().Contain("http://www.opengis.net/spec/ogcapi-styles-1/1.0/conf/core");
        classes.Should().Contain("http://www.opengis.net/spec/ogcapi-styles-1/1.0/conf/mapbox-styles");
        classes.Should().Contain("http://www.opengis.net/spec/ogcapi-styles-1/1.0/conf/sld-10");
        classes.Should().Contain("http://www.opengis.net/spec/ogcapi-styles-1/1.0/conf/sld-11");
        classes.Should().Contain("http://www.opengis.net/spec/ogcapi-styles-1/1.0/conf/style-validation");
        classes.Should().Contain("http://www.opengis.net/spec/ogcapi-styles-1/1.0/conf/manage-styles");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/styles/openapi.json")]
    public async Task GetOpenApi_ReturnsOpenApiDocument()
    {
        var client = _fixture.CreateClient();

        var response = await client.GetAsync("/ogc/styles/openapi.json");

        response.Be200Ok();
        var payload = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(payload);
        document.RootElement.TryGetProperty("openapi", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/styles")]
    public async Task GetStylesList_AfterStyling_IncludesTheStyledCollection()
    {
        var client = _fixture.CreateAdminClient();
        await SeedTestLayerStyleAsync(client);

        var response = await client.GetAsync("/ogc/styles");

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var styles = document.RootElement.GetProperty("styles");
        styles.GetArrayLength().Should().BeGreaterThan(0);

        var first = styles[0];
        first.GetProperty("id").GetString().Should().NotBeNullOrEmpty();
        var links = first.GetProperty("links").EnumerateArray().ToArray();
        links.Should().Contain(l =>
            l.GetProperty("rel").GetString() == "stylesheet"
            && l.GetProperty("type").GetString() == MapboxStyleMediaType);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/styles/{styleId}")]
    public async Task GetStylesheet_DefaultAccept_ReturnsMapLibreStyle()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await SeedAndResolveStyleIdAsync(client);

        var response = await client.GetAsync($"/ogc/styles/{Uri.EscapeDataString(styleId)}");

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be(MapboxStyleMediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("version").GetInt32().Should().Be(8);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/styles/{styleId}")]
    public async Task GetStylesheet_AcceptSld10_ReturnsDerivedSld()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await SeedAndResolveStyleIdAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/ogc/styles/{Uri.EscapeDataString(styleId)}");
        request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse(Sld10MediaType));

        var response = await client.SendAsync(request);

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.ogc.sld+xml");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("StyledLayerDescriptor");
        body.Should().Contain("version=\"1.0.0\"");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/styles/{styleId}")]
    public async Task GetStylesheet_AcceptSld11_ReturnsDerivedSld11()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await SeedAndResolveStyleIdAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/ogc/styles/{Uri.EscapeDataString(styleId)}");
        request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse(Sld11MediaType));

        var response = await client.SendAsync(request);

        response.Be200Ok();
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("StyledLayerDescriptor");
        body.Should().Contain("version=\"1.1.0\"");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/styles/{styleId}")]
    public async Task GetStylesheet_UnknownStyle_Returns404()
    {
        var client = _fixture.CreateClient();

        var response = await client.GetAsync($"/ogc/styles/does-not-exist-{Guid.NewGuid():N}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/styles/{styleId}/metadata")]
    public async Task GetStyleMetadata_ReturnsMetadataWithStylesheetSchemaAndPreviewLinks()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await SeedAndResolveStyleIdAsync(client);

        var response = await client.GetAsync($"/ogc/styles/{Uri.EscapeDataString(styleId)}/metadata");

        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("id").GetString().Should().Be(styleId);

        var links = document.RootElement.GetProperty("links").EnumerateArray().ToArray();
        links.Should().Contain(l => l.GetProperty("rel").GetString() == "stylesheet");
        links.Should().Contain(l => l.GetProperty("rel").GetString() == "describedby");
        links.Should().Contain(l => l.GetProperty("rel").GetString() == "preview");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task PutStyle_WithValidMapLibre_Returns204()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await SeedAndResolveStyleIdAsync(client);

        var style = BuildDefaultStyleJson();
        using var content = new StringContent(style, Encoding.UTF8, MapboxStyleMediaType);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/ogc/styles/{Uri.EscapeDataString(styleId)}")
        {
            Content = content
        };

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task PutStyle_WithInvalidMapLibre_StrictHandling_Returns400()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await SeedAndResolveStyleIdAsync(client);

        // Missing version/layers -> normalizer rejects.
        const string invalid = "{\"name\":\"broken\"}";
        using var content = new StringContent(invalid, Encoding.UTF8, MapboxStyleMediaType);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/ogc/styles/{Uri.EscapeDataString(styleId)}")
        {
            Content = content
        };
        request.Headers.TryAddWithoutValidation("Prefer", "handling=strict");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/styles")]
    public async Task PostStyle_WithValidMapLibre_Returns201AndIsRetrievable()
    {
        var client = _fixture.CreateAdminClient();

        var styleId = $"created-{Guid.NewGuid():N}";
        using var content = new StringContent(BuildDefaultStyleJson(), Encoding.UTF8, MapboxStyleMediaType);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ogc/styles") { Content = content };
        request.Headers.TryAddWithoutValidation("X-Style-Id", styleId);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.ToString().Should().Contain(Uri.EscapeDataString(styleId));

        // The newly created standalone style is retrievable through the read path.
        var fetched = await client.GetAsync($"/ogc/styles/{Uri.EscapeDataString(styleId)}");
        fetched.Be200Ok();
        fetched.Content.Headers.ContentType?.MediaType.Should().Be(MapboxStyleMediaType);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/styles")]
    public async Task PostStyle_DuplicateId_Returns409Conflict()
    {
        var client = _fixture.CreateAdminClient();

        var styleId = $"dup-{Guid.NewGuid():N}";

        using var first = new StringContent(BuildDefaultStyleJson(), Encoding.UTF8, MapboxStyleMediaType);
        using var firstRequest = new HttpRequestMessage(HttpMethod.Post, "/ogc/styles") { Content = first };
        firstRequest.Headers.TryAddWithoutValidation("X-Style-Id", styleId);
        (await client.SendAsync(firstRequest)).StatusCode.Should().Be(HttpStatusCode.Created);

        using var second = new StringContent(BuildDefaultStyleJson(), Encoding.UTF8, MapboxStyleMediaType);
        using var secondRequest = new HttpRequestMessage(HttpMethod.Post, "/ogc/styles") { Content = second };
        secondRequest.Headers.TryAddWithoutValidation("X-Style-Id", styleId);

        var response = await client.SendAsync(secondRequest);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("DELETE /ogc/styles/{styleId}")]
    public async Task DeleteStyle_AfterCreate_Returns204ThenNotFound()
    {
        var client = _fixture.CreateAdminClient();

        var styleId = $"del-{Guid.NewGuid():N}";
        using var content = new StringContent(BuildDefaultStyleJson(), Encoding.UTF8, MapboxStyleMediaType);
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/ogc/styles") { Content = content };
        createRequest.Headers.TryAddWithoutValidation("X-Style-Id", styleId);
        (await client.SendAsync(createRequest)).StatusCode.Should().Be(HttpStatusCode.Created);

        var deleteResponse = await client.DeleteAsync($"/ogc/styles/{Uri.EscapeDataString(styleId)}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var getResponse = await client.GetAsync($"/ogc/styles/{Uri.EscapeDataString(styleId)}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("DELETE /ogc/styles/{styleId}")]
    public async Task DeleteStyle_Unknown_Returns404()
    {
        var client = _fixture.CreateAdminClient();

        var response = await client.DeleteAsync($"/ogc/styles/missing-{Guid.NewGuid():N}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/features/collections/{collectionId}")]
    public async Task GetCollection_IncludesStylesAndStylesheetLinks()
    {
        var client = _fixture.CreateClient();

        var collectionsResponse = await client.GetAsync("/ogc/features/collections");
        collectionsResponse.Be200Ok();
        using var collectionsDocument = JsonDocument.Parse(await collectionsResponse.Content.ReadAsStringAsync());
        var collections = collectionsDocument.RootElement.GetProperty("collections");
        collections.GetArrayLength().Should().BeGreaterThan(0);
        var collectionId = collections[0].GetProperty("id").GetString();
        collectionId.Should().NotBeNullOrEmpty();

        var response = await client.GetAsync($"/ogc/features/collections/{Uri.EscapeDataString(collectionId!)}");
        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var links = document.RootElement.GetProperty("links").EnumerateArray().ToArray();

        links.Should().Contain(l => l.GetProperty("rel").GetString() == "style");
        links.Should().Contain(l => l.GetProperty("rel").GetString() == "http://www.opengis.net/def/rel/ogc/1.0/styles");
        links.Should().Contain(l =>
            l.GetProperty("rel").GetString() == "stylesheet"
            && l.GetProperty("href").GetString()!.Contains("/ogc/styles/", StringComparison.Ordinal));
    }

    private async Task<string> SeedAndResolveStyleIdAsync(HttpClient adminClient)
    {
        await SeedTestLayerStyleAsync(adminClient);

        var response = await adminClient.GetAsync("/ogc/styles");
        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var styles = document.RootElement.GetProperty("styles");
        styles.GetArrayLength().Should().BeGreaterThan(0);
        return styles[0].GetProperty("id").GetString()!;
    }

    private static async Task SeedTestLayerStyleAsync(HttpClient adminClient)
    {
        var request = new LayerStyleUpdateRequest
        {
            MapLibreStyle = JsonSerializer.Deserialize<JsonElement>(BuildDefaultStyleJson())
        };

        var response = await adminClient.PutAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style",
            JsonContent.Create(request, LayerStyleJsonContext.Default.LayerStyleUpdateRequest));
        response.Be200Ok();
    }

    private static string BuildDefaultStyleJson()
    {
        var layer = new StyleLayerDescriptor(
            WebAppFixture.TestLayerId,
            "Test Layer",
            MetadataV2GeometryType.Point);
        var style = StyleDefaults.BuildDefaultMapLibreStyle(layer);
        return JsonSerializer.Serialize(style);
    }
}
