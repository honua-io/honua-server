// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Records;

/// <summary>
/// Integration tests for the read-only OGC API Records catalog surface.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.OgcApiRecords)]
public sealed class OgcRecordsEndpointTests : IAsyncLifetime
{
    private const string CatalogId = "honua-catalog";
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/records")]
    public async Task GetLandingPage_ReturnsRecordsLinks()
    {
        var response = await _fixture.Client.GetAsync("/ogc/records");

        await AssertOkAsync(response);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        using var json = await ReadJsonAsync(response);
        json.RootElement.GetProperty("title").GetString().Should().Be("Honua OGC API Records");
        json.RootElement.GetProperty("description").GetString().Should().Contain("metadata record discovery");

        var links = json.RootElement.GetProperty("links").EnumerateArray().ToArray();
        links.Should().Contain(link => HasRel(link, "self") && HrefEndsWith(link, "/ogc/records"));
        links.Should().Contain(link => HasRel(link, "conformance") && HrefEndsWith(link, "/ogc/records/conformance"));
        links.Should().Contain(link => HasRel(link, "data") && HrefEndsWith(link, "/ogc/records/collections"));
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/records/conformance")]
    public async Task GetConformance_ReturnsImplementedRecordsClasses()
    {
        var response = await _fixture.Client.GetAsync("/ogc/records/conformance");

        await AssertOkAsync(response);

        using var json = await ReadJsonAsync(response);
        var conformance = json.RootElement.GetProperty("conformsTo")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToArray();

        conformance.Should().Contain("http://www.opengis.net/spec/ogcapi-records-1/1.0/conf/core");
        conformance.Should().Contain("http://www.opengis.net/spec/ogcapi-records-1/1.0/conf/record-collection");
        conformance.Should().Contain("http://www.opengis.net/spec/ogcapi-records-1/1.0/conf/record-core");
        conformance.Should().Contain("http://www.opengis.net/spec/ogcapi-records-1/1.0/conf/record-geojson");
        conformance.Should().NotContain(value => value != null && value.Contains("/conf/harvest", StringComparison.OrdinalIgnoreCase));
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/records/collections")]
    public async Task GetCollections_ReturnsHonuaCatalogCollection()
    {
        var response = await _fixture.Client.GetAsync("/ogc/records/collections");

        await AssertOkAsync(response);

        using var json = await ReadJsonAsync(response);
        var collections = json.RootElement.GetProperty("collections").EnumerateArray().ToArray();
        collections.Should().ContainSingle();

        var collection = collections[0];
        collection.GetProperty("id").GetString().Should().Be(CatalogId);
        collection.GetProperty("itemType").GetString().Should().Be("record");
        collection.GetProperty("links").EnumerateArray()
            .Should()
            .Contain(link => HasRel(link, "items") && HrefEndsWith(link, $"/ogc/records/collections/{CatalogId}/items"));
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/records/collections/{collectionId}")]
    public async Task GetCollection_WithCatalogId_ReturnsCollectionMetadata()
    {
        var response = await _fixture.Client.GetAsync($"/ogc/records/collections/{CatalogId}");

        await AssertOkAsync(response);

        using var json = await ReadJsonAsync(response);
        json.RootElement.GetProperty("id").GetString().Should().Be(CatalogId);
        json.RootElement.GetProperty("title").GetString().Should().Be("Honua Catalog Records");
        json.RootElement.GetProperty("itemType").GetString().Should().Be("record");
        json.RootElement.GetProperty("links").EnumerateArray()
            .Should()
            .Contain(link => HasRel(link, "items") && HrefEndsWith(link, $"/ogc/records/collections/{CatalogId}/items"));
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    public async Task GetItems_WithDefaultCatalog_ReturnsServiceAndLayerRecords()
    {
        var response = await _fixture.Client.GetAsync($"/ogc/records/collections/{CatalogId}/items?limit=10");

        await AssertOkAsync(response);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/geo+json");

        using var json = await ReadJsonAsync(response);
        json.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
        json.RootElement.GetProperty("numberMatched").GetInt32().Should().BeGreaterThanOrEqualTo(4);
        json.RootElement.GetProperty("numberReturned").GetInt32().Should().BeGreaterThanOrEqualTo(4);

        var features = json.RootElement.GetProperty("features").EnumerateArray().ToArray();
        features.Select(feature => feature.GetProperty("id").GetString())
            .Should()
            .Contain(["service:test", $"layer:{WebAppFixture.TestLayerId}"]);

        var serviceRecord = features.Single(feature => feature.GetProperty("id").GetString() == "service:test");
        serviceRecord.GetProperty("properties").GetProperty("type").GetString().Should().Be("service");
        serviceRecord.GetProperty("links").EnumerateArray()
            .Should()
            .Contain(link => HrefEndsWith(link, "/rest/services/test/FeatureServer"));
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    public async Task GetItems_WithFilters_ReturnsDeterministicSubsets()
    {
        var idFiltered = await GetRecordIdsAsync($"ids={Uri.EscapeDataString($"layer:{WebAppFixture.TestLayerId}")}");
        idFiltered.Should().Equal($"layer:{WebAppFixture.TestLayerId}");

        var typeFiltered = await GetRecordIdsAsync("type=service");
        typeFiltered.Should().Equal("service:test");

        var externalIdFiltered = await GetRecordIdsAsync("externalIds=Test%20Layer");
        externalIdFiltered.Should().Contain($"layer:{WebAppFixture.TestLayerId}");
        externalIdFiltered.Should().NotContain("service:test");

        var qFiltered = await GetRecordIdsAsync("q=Related");
        qFiltered.Should().Contain(["layer:1", "layer:2"]);
        qFiltered.Should().NotContain($"layer:{WebAppFixture.TestLayerId}");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    public async Task GetItems_WithBboxFilter_ReturnsIntersectingRecords()
    {
        var intersecting = await GetRecordIdsAsync("bbox=-123,37,-122,38");
        intersecting.Should().Contain(["service:test", $"layer:{WebAppFixture.TestLayerId}"]);

        var outside = await GetRecordIdsAsync("bbox=200,80,210,85");
        outside.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    public async Task GetItems_WithLimitAndOffset_ReturnsPagedLinks()
    {
        var firstResponse = await _fixture.Client.GetAsync($"/ogc/records/collections/{CatalogId}/items?limit=1");

        await AssertOkAsync(firstResponse);

        using var firstPage = await ReadJsonAsync(firstResponse);
        firstPage.RootElement.GetProperty("numberReturned").GetInt32().Should().Be(1);
        firstPage.RootElement.GetProperty("features")[0].GetProperty("id").GetString().Should().Be("service:test");
        firstPage.RootElement.GetProperty("links").EnumerateArray()
            .Should()
            .Contain(link => HasRel(link, "next") && HrefContains(link, "offset=1"));

        var secondResponse = await _fixture.Client.GetAsync($"/ogc/records/collections/{CatalogId}/items?limit=1&offset=1");

        await AssertOkAsync(secondResponse);

        using var secondPage = await ReadJsonAsync(secondResponse);
        secondPage.RootElement.GetProperty("numberReturned").GetInt32().Should().Be(1);
        secondPage.RootElement.GetProperty("features")[0].GetProperty("id").GetString().Should().Be($"layer:{WebAppFixture.TestLayerId}");
        secondPage.RootElement.GetProperty("links").EnumerateArray()
            .Should()
            .Contain(link => HasRel(link, "prev") && HrefContains(link, "offset=0"));
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items/{recordId}")]
    public async Task GetItem_WithLayerRecordId_ReturnsRecordLinks()
    {
        var recordId = Uri.EscapeDataString($"layer:{WebAppFixture.TestLayerId}");
        var response = await _fixture.Client.GetAsync($"/ogc/records/collections/{CatalogId}/items/{recordId}");

        await AssertOkAsync(response);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/geo+json");

        using var json = await ReadJsonAsync(response);
        json.RootElement.GetProperty("type").GetString().Should().Be("Feature");
        json.RootElement.GetProperty("id").GetString().Should().Be($"layer:{WebAppFixture.TestLayerId}");
        json.RootElement.GetProperty("properties").GetProperty("type").GetString().Should().Be("dataset");
        json.RootElement.GetProperty("properties").GetProperty("geometryType").GetString().Should().Be("Point");

        var links = json.RootElement.GetProperty("links").EnumerateArray().ToArray();
        links.Should().Contain(link => HasRel(link, "self") && HrefContains(link, "layer%3A0"));
        links.Should().Contain(link => HrefEndsWith(link, $"/ogc/features/collections/{WebAppFixture.TestLayerId}"));
        links.Should().Contain(link => HrefEndsWith(link, $"/ogc/features/collections/{WebAppFixture.TestLayerId}/items"));
        links.Should().Contain(link => HrefEndsWith(link, $"/stac/collections/{WebAppFixture.TestLayerId}"));
        links.Should().Contain(link => HrefEndsWith(link, $"/rest/services/test/FeatureServer/{WebAppFixture.TestLayerId}"));
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items/{recordId}")]
    public async Task GetItem_WithUnknownRecordId_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync($"/ogc/records/collections/{CatalogId}/items/missing");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    public async Task GetItems_WithInvalidQuery_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync($"/ogc/records/collections/{CatalogId}/items?limit=0");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<string[]> GetRecordIdsAsync(string query)
    {
        var response = await _fixture.Client.GetAsync($"/ogc/records/collections/{CatalogId}/items?{query}");

        await AssertOkAsync(response);

        using var json = await ReadJsonAsync(response);
        return json.RootElement.GetProperty("features")
            .EnumerateArray()
            .Select(feature => feature.GetProperty("id").GetString())
            .OfType<string>()
            .ToArray();
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(content);
    }

    private static async Task AssertOkAsync(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.OK)
        {
            return;
        }

        var allowHeader = response.Headers.TryGetValues("Allow", out var allowValues)
            ? string.Join(", ", allowValues)
            : "<none>";
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "Allow: {0}; Body: {1}", allowHeader, body);
    }

    private static bool HasRel(JsonElement link, string rel)
        => link.TryGetProperty("rel", out var relElement) &&
           string.Equals(relElement.GetString(), rel, StringComparison.OrdinalIgnoreCase);

    private static bool HrefEndsWith(JsonElement link, string suffix)
        => link.TryGetProperty("href", out var hrefElement) &&
           hrefElement.GetString()?.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) == true;

    private static bool HrefContains(JsonElement link, string value)
        => link.TryGetProperty("href", out var hrefElement) &&
           hrefElement.GetString()?.Contains(value, StringComparison.OrdinalIgnoreCase) == true;
}
