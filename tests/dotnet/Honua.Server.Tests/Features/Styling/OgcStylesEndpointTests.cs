// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Styling;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Styling;

/// <summary>
/// Integration tests for the OGC API - Styles adapter (ADR-0048, issues #1388/#1389).
/// Covers landing/styles list, conformance, content-negotiated stylesheets (MapLibre +
/// derived SLD and Esri drawingInfo), style metadata, and the manage-styles PUT/POST/DELETE
/// surface for both collection-keyed and standalone catalog styles (#3188).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.OgcApiStyles)]
public sealed class OgcStylesEndpointTests : IAsyncLifetime
{
    private const string MapboxStyleMediaType = "application/vnd.mapbox.style+json";
    private const string Sld10MediaType = "application/vnd.ogc.sld+xml;version=1.0";
    private const string Sld11MediaType = "application/vnd.ogc.sld+xml;version=1.1";
    private const string EsriDrawingInfoMediaType = "application/vnd.esri.drawinginfo+json";

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
        document.RootElement.GetProperty("paths")
            .GetProperty("/ogc/styles/{styleId}")
            .GetProperty("delete")
            .GetProperty("responses")
            .TryGetProperty("403", out _)
            .Should().BeTrue();
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
        links.Should().Contain(l =>
            l.GetProperty("rel").GetString() == "stylesheet"
            && l.GetProperty("type").GetString() == EsriDrawingInfoMediaType);
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
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/styles")]
    public async Task PostStyle_IdOwnedByCollection_Returns409Conflict()
    {
        var client = _fixture.CreateAdminClient();
        // The style projection is keyed by the collection resource's metadata name. Seed
        // its canonical layer style so that exact route-owned identifier is visible.
        var collectionStyleId = await SeedAndResolveStyleIdAsync(client);

        using var content = new StringContent(BuildDefaultStyleJson(), Encoding.UTF8, MapboxStyleMediaType);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ogc/styles") { Content = content };
        request.Headers.TryAddWithoutValidation("X-Style-Id", collectionStyleId);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/styles")]
    public async Task PostStyle_StrictFractionalVersion_Returns400()
    {
        var client = _fixture.CreateAdminClient();
        var style = JsonNode.Parse(BuildDefaultStyleJson())!;
        style["version"] = 8.5;

        using var content = new StringContent(style.ToJsonString(), Encoding.UTF8, MapboxStyleMediaType);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ogc/styles?validate=strict") { Content = content };

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("DELETE /ogc/styles/{styleId}")]
    public async Task DeleteStyle_AfterCreate_Returns204ThenNotFound()
    {
        var client = _fixture.CreateAdminClient();
        await SeedTestLayerStyleAsync(client);

        var styleId = $"del-{Guid.NewGuid():N}";
        using var content = new StringContent(BuildDefaultStyleJson(), Encoding.UTF8, MapboxStyleMediaType);
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/ogc/styles") { Content = content };
        createRequest.Headers.TryAddWithoutValidation("X-Style-Id", styleId);
        (await client.SendAsync(createRequest)).StatusCode.Should().Be(HttpStatusCode.Created);

        using (var scope = _fixture.Services.CreateScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<IStyleCatalog>();
            (await catalog.AssociateLayerAsync(WebAppFixture.TestLayerId, styleId, ordinal: 1)).Should().BeTrue();
            await scope.ServiceProvider.GetRequiredService<IMetadataV2StyleGraphSync>()
                .SyncLayerStylesAsync(WebAppFixture.TestLayerId);
        }

        var styleResourceId = MetadataV2StyleResourceFactory.BuildStyleResourceId(styleId);
        _fixture.GetCurrentV2GraphSnapshot().Index.ResourcesByStorageLayerId[WebAppFixture.TestLayerId]
            .StyleResourceIds.Should().Contain(styleResourceId);

        var deleteResponse = await client.DeleteAsync($"/ogc/styles/{Uri.EscapeDataString(styleId)}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var getResponse = await client.GetAsync($"/ogc/styles/{Uri.EscapeDataString(styleId)}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        snapshot.Index.ResourcesByStorageLayerId[WebAppFixture.TestLayerId]
            .StyleResourceIds.Should().NotContain(styleResourceId);
        snapshot.Index.ResourcesById.Should().NotContainKey(styleResourceId);

        // Layer default styles are canonical per-layer state. DELETE must not remove their
        // catalog mirror and leave the styled layer inaccessible through OGC Styles.
        var mirroredStyleId = $"style-layer-{WebAppFixture.TestLayerId}";
        var mirroredDelete = await client.DeleteAsync($"/ogc/styles/{Uri.EscapeDataString(mirroredStyleId)}");
        mirroredDelete.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var mirroredGet = await client.GetAsync($"/ogc/styles/{Uri.EscapeDataString(mirroredStyleId)}");
        mirroredGet.Be200Ok();
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
    [Endpoint("GET /ogc/styles/{styleId}")]
    public async Task GetStylesheet_StandaloneStyle_AcceptEsriDrawingInfo_ReturnsDrawingInfo()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await CreateStandaloneStyleAsync(client, MetadataV2GeometryType.Point);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/ogc/styles/{Uri.EscapeDataString(styleId)}");
        request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse(EsriDrawingInfoMediaType));

        var response = await client.SendAsync(request);

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be(EsriDrawingInfoMediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("renderer").GetProperty("symbol").GetProperty("type")
            .GetString().Should().Be("esriSMS");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/styles/{styleId}")]
    public async Task GetStylesheet_StandaloneStyle_MalformedLayerType_SkipsMalformedLayer()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = $"malformed-layer-{Guid.NewGuid():N}";
        var style = JsonNode.Parse(BuildStyleJson(MetadataV2GeometryType.Polygon))!;
        style["layers"]!.AsArray().Insert(0, new JsonObject
        {
            ["id"] = "malformed",
            ["type"] = new JsonObject()
        });
        using (var content = new StringContent(style.ToJsonString(), Encoding.UTF8, MapboxStyleMediaType))
        using (var createRequest = new HttpRequestMessage(HttpMethod.Post, "/ogc/styles") { Content = content })
        {
            createRequest.Headers.TryAddWithoutValidation("X-Style-Id", styleId);
            (await client.SendAsync(createRequest)).StatusCode.Should().Be(HttpStatusCode.Created);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/ogc/styles/{styleId}");
        request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse(EsriDrawingInfoMediaType));

        var response = await client.SendAsync(request);

        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("renderer").GetProperty("symbol").GetProperty("type")
            .GetString().Should().Be("esriSFS");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/styles/{styleId}")]
    public async Task GetStylesheet_UnsupportedAccept_Returns406ListingEveryEncoding()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await CreateStandaloneStyleAsync(client, MetadataV2GeometryType.Point);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/ogc/styles/{Uri.EscapeDataString(styleId)}");
        request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse("text/csv"));

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotAcceptable);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var detail = document.RootElement.GetProperty("detail").GetString();
        detail.Should().Contain(MapboxStyleMediaType);
        detail.Should().Contain(Sld10MediaType);
        detail.Should().Contain(Sld11MediaType);
        detail.Should().Contain(EsriDrawingInfoMediaType);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task PutStyle_StandaloneStyle_UpdatesCatalogStyle()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await CreateStandaloneStyleAsync(client, MetadataV2GeometryType.Point);

        // Replace the canonical document with a line style so the update is observable.
        using var content = new StringContent(
            BuildStyleJson(MetadataV2GeometryType.LineString),
            Encoding.UTF8,
            MapboxStyleMediaType);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/ogc/styles/{Uri.EscapeDataString(styleId)}")
        {
            Content = content
        };

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var fetched = await client.GetAsync($"/ogc/styles/{Uri.EscapeDataString(styleId)}");
        fetched.Be200Ok();
        using var document = JsonDocument.Parse(await fetched.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("layers")[0].GetProperty("type").GetString().Should().Be("line");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task PutStyle_StandaloneStyle_MapLibreRoundTripsToDrawingInfo()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await CreateStandaloneStyleAsync(client, MetadataV2GeometryType.Point);

        using var content = new StringContent(
            BuildStyleJson(MetadataV2GeometryType.LineString),
            Encoding.UTF8,
            MapboxStyleMediaType);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/ogc/styles/{Uri.EscapeDataString(styleId)}")
        {
            Content = content
        };
        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // The Esri encoding is derived from the canonical MapLibre style that was just PUT,
        // so it must reflect the new geometry rather than the drawingInfo cached at create.
        using var esriRequest = new HttpRequestMessage(HttpMethod.Get, $"/ogc/styles/{Uri.EscapeDataString(styleId)}");
        esriRequest.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse(EsriDrawingInfoMediaType));

        var esriResponse = await client.SendAsync(esriRequest);

        esriResponse.Be200Ok();
        using var document = JsonDocument.Parse(await esriResponse.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("renderer").GetProperty("symbol").GetProperty("type")
            .GetString().Should().Be("esriSLS");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task PutStyle_StandaloneStyle_WithDrawingInfo_StoresCanonicalMapLibre()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await CreateStandaloneStyleAsync(client, MetadataV2GeometryType.Point);

        // A drawingInfo renderer must not be allowed to change the geometry family of the
        // layer-backed source referenced by the canonical style.
        var mismatchedDrawingInfo = JsonSerializer.Serialize(
            StyleDefaults.BuildDefaultDrawingInfo(MetadataV2GeometryType.Polygon));
        using var mismatchedContent = new StringContent(
            mismatchedDrawingInfo,
            Encoding.UTF8,
            EsriDrawingInfoMediaType);
        using var mismatchedRequest = new HttpRequestMessage(
            HttpMethod.Put,
            $"/ogc/styles/{Uri.EscapeDataString(styleId)}")
        {
            Content = mismatchedContent
        };

        var mismatchedResponse = await client.SendAsync(mismatchedRequest);

        mismatchedResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var drawingInfo = JsonSerializer.Serialize(
            StyleDefaults.BuildDefaultDrawingInfo(MetadataV2GeometryType.Point));
        using var content = new StringContent(drawingInfo, Encoding.UTF8, EsriDrawingInfoMediaType);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/ogc/styles/{Uri.EscapeDataString(styleId)}")
        {
            Content = content
        };

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // MapLibre stays the single source of truth: the renderer was converted on write.
        var mapLibre = await client.GetAsync($"/ogc/styles/{Uri.EscapeDataString(styleId)}");
        mapLibre.Be200Ok();
        mapLibre.Content.Headers.ContentType?.MediaType.Should().Be(MapboxStyleMediaType);
        using var mapLibreDocument = JsonDocument.Parse(await mapLibre.Content.ReadAsStringAsync());
        mapLibreDocument.RootElement.GetProperty("layers").EnumerateArray()
            .Should().Contain(layer => layer.GetProperty("type").GetString() == "circle");

        // ...and reading the Esri encoding back derives the same symbolizer family.
        using var esriRequest = new HttpRequestMessage(HttpMethod.Get, $"/ogc/styles/{Uri.EscapeDataString(styleId)}");
        esriRequest.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse(EsriDrawingInfoMediaType));
        var esriResponse = await client.SendAsync(esriRequest);
        esriResponse.Be200Ok();
        using var esriDocument = JsonDocument.Parse(await esriResponse.Content.ReadAsStringAsync());
        esriDocument.RootElement.GetProperty("renderer").GetProperty("symbol").GetProperty("type")
            .GetString().Should().Be("esriSMS");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task PutStyle_StandaloneStyle_NonObjectDrawingInfo_Returns400()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await CreateStandaloneStyleAsync(client, MetadataV2GeometryType.Point);

        foreach (var payload in new[]
                 {
                     "null",
                     "[]",
                     "\"renderer\"",
                     "42",
                     "{\"renderer\":{\"type\":{}}}",
                     "{\"renderer\":{\"type\":\"uniqueValue\",\"uniqueValueInfos\":[null,{\"value\":\"a\",\"symbol\":{\"type\":\"esriSLS\"}}]}}"
                 })
        {
            using var content = new StringContent(payload, Encoding.UTF8, EsriDrawingInfoMediaType);
            using var request = new HttpRequestMessage(HttpMethod.Put, $"/ogc/styles/{Uri.EscapeDataString(styleId)}")
            {
                Content = content
            };

            var response = await client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task PutStyle_LayerBoundCatalogStyle_WritesThroughToLayerStyle()
    {
        var client = _fixture.CreateAdminClient();

        // Seeding a layer style mirrors it into the catalog as "style-layer-{id}", which the
        // styles list surfaces. Editing that style must reach the canonical per-layer store.
        await SeedTestLayerStyleAsync(client);
        var styleId = $"style-layer-{WebAppFixture.TestLayerId}";

        var style = JsonNode.Parse(BuildDefaultStyleJson())!;
        style["layers"]![0]!["paint"]!["circle-color"] = "#ff0000";

        using var content = new StringContent(style.ToJsonString(), Encoding.UTF8, MapboxStyleMediaType);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/ogc/styles/{Uri.EscapeDataString(styleId)}")
        {
            Content = content
        };

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var layerStyle = await client.GetAsync($"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style");
        layerStyle.Be200Ok();
        using var document = JsonDocument.Parse(await layerStyle.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("data").GetProperty("mapLibreStyle")
            .GetProperty("layers")[0].GetProperty("paint").GetProperty("circle-color")
            .GetString().Should().Be("#ff0000");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task PutStyle_LayerBoundCatalogStyle_NonObjectDrawingInfo_Returns400()
    {
        var client = _fixture.CreateAdminClient();
        await SeedTestLayerStyleAsync(client);
        var styleId = $"style-layer-{WebAppFixture.TestLayerId}";

        foreach (var payload in new[] { "[]", "{\"renderer\":{\"type\":{}}}" })
        {
            using var content = new StringContent(payload, Encoding.UTF8, EsriDrawingInfoMediaType);
            using var request = new HttpRequestMessage(HttpMethod.Put, $"/ogc/styles/{styleId}") { Content = content };

            var response = await client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task PutStyle_AssociatedCatalogStyle_UpdatesOnlyRequestedStyle()
    {
        var client = _fixture.CreateAdminClient();
        await SeedTestLayerStyleAsync(client);

        var styleId = await CreateStandaloneStyleAsync(
            client,
            MetadataV2GeometryType.Point,
            $"style-layer-0{WebAppFixture.TestLayerId}");
        using (var scope = _fixture.Services.CreateScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<IStyleCatalog>();
            (await catalog.AssociateLayerAsync(WebAppFixture.TestLayerId, styleId, ordinal: 0)).Should().BeTrue();
        }

        using var content = new StringContent(
            BuildStyleJson(MetadataV2GeometryType.LineString),
            Encoding.UTF8,
            MapboxStyleMediaType);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/ogc/styles/{Uri.EscapeDataString(styleId)}")
        {
            Content = content
        };

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var requestedStyle = await client.GetAsync($"/ogc/styles/{Uri.EscapeDataString(styleId)}");
        requestedStyle.Be200Ok();
        using var requestedDocument = JsonDocument.Parse(await requestedStyle.Content.ReadAsStringAsync());
        requestedDocument.RootElement.GetProperty("layers")[0].GetProperty("type").GetString().Should().Be("line");

        var layerStyle = await client.GetAsync($"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style");
        layerStyle.Be200Ok();
        using var layerDocument = JsonDocument.Parse(await layerStyle.Content.ReadAsStringAsync());
        layerDocument.RootElement.GetProperty("data").GetProperty("mapLibreStyle")
            .GetProperty("layers")[0].GetProperty("type").GetString().Should().Be("circle");

        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var styleResourceId = MetadataV2StyleResourceFactory.BuildStyleResourceId(styleId);
        snapshot.Index.ResourcesByStorageLayerId[WebAppFixture.TestLayerId]
            .StyleResourceIds.Should().Contain(styleResourceId);
        var graphStyle = snapshot.Index.ResourcesById[styleResourceId].Style!;
        var mapboxEncoding = graphStyle.Encodings.Single(encoding => encoding.Encoding == "mapbox-style");
        mapboxEncoding.Body.Should().NotBeNull();
        using var graphMapboxDocument = JsonDocument.Parse(mapboxEncoding.Body!);
        graphMapboxDocument.RootElement.GetProperty("layers")[0].GetProperty("type")
            .GetString().Should().Be("line");
        var esriEncoding = graphStyle.Encodings.Single(encoding => encoding.Encoding == "esri-drawing-info");
        esriEncoding.Body.Should().NotBeNull();
        using var graphEsriDocument = JsonDocument.Parse(esriEncoding.Body!);
        graphEsriDocument.RootElement.GetProperty("renderer").GetProperty("symbol").GetProperty("type")
            .GetString().Should().Be("esriSLS");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task PutStyle_UnknownStyle_Returns404()
    {
        var client = _fixture.CreateAdminClient();

        using var content = new StringContent(BuildDefaultStyleJson(), Encoding.UTF8, MapboxStyleMediaType);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/ogc/styles/missing-{Guid.NewGuid():N}")
        {
            Content = content
        };

        var response = await client.SendAsync(request);

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

        using var content = JsonContent.Create(request, LayerStyleJsonContext.Default.LayerStyleUpdateRequest);
        using var response = await adminClient.PutAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style",
            content);
        response.Be200Ok();
    }

    /// <summary>
    /// Creates a standalone (layer-less) catalog style through the manage-styles POST
    /// surface and returns its identifier.
    /// </summary>
    private static async Task<string> CreateStandaloneStyleAsync(
        HttpClient adminClient,
        MetadataV2GeometryType geometryType,
        string? requestedStyleId = null)
    {
        var styleId = requestedStyleId ?? $"standalone-{Guid.NewGuid():N}";
        using var content = new StringContent(BuildStyleJson(geometryType), Encoding.UTF8, MapboxStyleMediaType);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ogc/styles") { Content = content };
        request.Headers.TryAddWithoutValidation("X-Style-Id", styleId);

        var response = await adminClient.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return styleId;
    }

    private static string BuildDefaultStyleJson() => BuildStyleJson(MetadataV2GeometryType.Point);

    private static string BuildStyleJson(MetadataV2GeometryType geometryType)
    {
        var layer = new StyleLayerDescriptor(
            WebAppFixture.TestLayerId,
            "Test Layer",
            geometryType);
        var style = StyleDefaults.BuildDefaultMapLibreStyle(layer);
        return JsonSerializer.Serialize(style);
    }
}
