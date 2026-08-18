// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Records;

/// <summary>
/// Depth tests for the OGC API Records surface (#2983): bbox edge cases (3D and
/// antimeridian-crossing boxes), datetime interval semantics for records without a
/// temporal value (#1988 regression), paging limits and link preservation, unknown
/// query-parameter rejection, format negotiation, and unknown-collection handling.
/// Complements the happy-path coverage in <see cref="OgcRecordsEndpointTests"/>.
/// </summary>
[Collection("Database.OgcApiData")]
[Protocol(TestProtocols.OgcApiRecords)]
public sealed class OgcRecordsDepthTests : IClassFixture<OgcRecordsEndpointTestsFixture>
{
    private const string CatalogId = "honua-catalog";
    private readonly WebAppFixture _fixture;

    public OgcRecordsDepthTests(OgcRecordsEndpointTestsFixture fixture)
    {
        _fixture = fixture.App;
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    public async Task GetItems_WithSixElementBbox_UsesHorizontalCoordinates()
    {
        // Spec-legal 3D bbox (minx,miny,minz,maxx,maxy,maxz) must not 400 and must
        // filter on the horizontal ordinates only (#1987 regression).
        var intersecting = await GetRecordIdsAsync("bbox=-123,37,0,-122,38,100");
        intersecting.Should().Contain(["service:test", $"layer:{WebAppFixture.TestLayerId}"]);

        var outside = await GetRecordIdsAsync("bbox=100,80,0,110,85,100");
        outside.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    public async Task GetItems_WithAntimeridianCrossingBbox_AppliesUnionSemantics()
    {
        // west > east crosses the antimeridian (RFC 7946 §5.2) and represents
        // [west,180] ∪ [-180,east]; the seeded records sit around -122.5..-122.3.
        var matched = await GetRecordIdsAsync("bbox=170,37,-122,38");
        matched.Should().Contain(["service:test", $"layer:{WebAppFixture.TestLayerId}"]);

        var unmatched = await GetRecordIdsAsync("bbox=170,37,-124,38");
        unmatched.Should().NotContain("service:test");
        unmatched.Should().NotContain($"layer:{WebAppFixture.TestLayerId}");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    public async Task GetItems_WithMalformedBbox_ReturnsBadRequest()
    {
        var invalidBboxes = new[]
        {
            "1,2,3",                 // three ordinates
            "1,2,3,4,5",             // five ordinates (neither 2D nor 3D)
            "a,b,c,d",               // non-numeric
            "0,10,10,5",             // minY > maxY
            "NaN,0,10,10",           // non-finite
            "Infinity,0,10,10"       // non-finite
        };

        foreach (var bbox in invalidBboxes)
        {
            var response = await _fixture.Client.GetAsync(
                $"/ogc/records/collections/{CatalogId}/items?bbox={Uri.EscapeDataString(bbox)}");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"bbox '{bbox}' must be rejected");
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    public async Task GetItems_WithDatetime_KeepsRecordsWithoutTemporalValue()
    {
        // Catalog records carry no temporal value, so a datetime filter must not drop
        // them (#1988 regression): instants and half-open intervals all keep them.
        // The both-open "../.." form is not covered here: it is outside the OGC API
        // datetime ABNF (an interval needs at least one bound) and is rejected before
        // dispatch by the shared input-validation middleware.
        var datetimeQueries = new[]
        {
            "datetime=2026-01-01T00:00:00Z",
            "datetime=" + Uri.EscapeDataString("2020-01-01T00:00:00Z/.."),
            "datetime=" + Uri.EscapeDataString("../2030-01-01T00:00:00Z")
        };

        foreach (var query in datetimeQueries)
        {
            var ids = await GetRecordIdsAsync(query + "&limit=100");
            ids.Should().Contain(
                ["service:test", $"layer:{WebAppFixture.TestLayerId}"],
                $"datetime query '{query}' must not exclude records without a temporal value");
        }
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    public async Task GetItems_WithInvalidDatetime_ReturnsBadRequest()
    {
        var invalidDatetimes = new[]
        {
            "notadate",
            "2026-01-02T00:00:00Z/2026-01-01T00:00:00Z",                     // start > end
            "2026-01-01T00:00:00Z/2026-01-02T00:00:00Z/2026-01-03T00:00:00Z", // three parts
            "../garbage",
            "../.."                                                           // both-open: outside the OGC ABNF
        };

        foreach (var datetime in invalidDatetimes)
        {
            var response = await _fixture.Client.GetAsync(
                $"/ogc/records/collections/{CatalogId}/items?datetime={Uri.EscapeDataString(datetime)}");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"datetime '{datetime}' must be rejected");
        }
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    public async Task GetItems_WithLimitOrOffsetOutOfRange_ReturnsBadRequest()
    {
        var overLimit = await _fixture.Client.GetAsync(
            $"/ogc/records/collections/{CatalogId}/items?limit=1001");
        overLimit.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var negativeOffset = await _fixture.Client.GetAsync(
            $"/ogc/records/collections/{CatalogId}/items?offset=-1");
        negativeOffset.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    public async Task GetItems_WithOffsetBeyondMatches_ReturnsEmptyPageWithPrevLinkOnly()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/records/collections/{CatalogId}/items?limit=10&offset=1000");

        await AssertOkAsync(response);

        using var json = await ReadJsonAsync(response);
        json.RootElement.GetProperty("numberReturned").GetInt32().Should().Be(0);
        json.RootElement.GetProperty("numberMatched").GetInt32().Should().BeGreaterThanOrEqualTo(4);
        json.RootElement.GetProperty("features").GetArrayLength().Should().Be(0);

        var links = json.RootElement.GetProperty("links").EnumerateArray().ToArray();
        links.Should().Contain(link => HasRel(link, "prev"));
        links.Should().NotContain(link => HasRel(link, "next"));
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    public async Task GetItems_PagedLinks_PreserveFilterParameters()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/records/collections/{CatalogId}/items?type=dataset&limit=1");

        await AssertOkAsync(response);

        using var json = await ReadJsonAsync(response);
        json.RootElement.GetProperty("numberReturned").GetInt32().Should().Be(1);
        json.RootElement.GetProperty("features")[0].GetProperty("properties").GetProperty("type").GetString()
            .Should().Be("dataset");

        var next = json.RootElement.GetProperty("links").EnumerateArray()
            .Single(link => HasRel(link, "next"));
        var href = next.GetProperty("href").GetString();
        href.Should().Contain("type=dataset", "paging links must carry the active filters forward");
        href.Should().Contain("offset=1");
        href.Should().Contain("limit=1");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    [Endpoint("GET /ogc/records/collections")]
    public async Task GetItems_WithUnknownQueryParameter_ReturnsBadRequest()
    {
        var items = await _fixture.Client.GetAsync(
            $"/ogc/records/collections/{CatalogId}/items?unsupported=1");
        items.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var collections = await _fixture.Client.GetAsync("/ogc/records/collections?unsupported=1");
        collections.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.ContentNegotiation)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    public async Task GetItems_FormatParameter_AllowsGeoJsonAndRejectsHtml()
    {
        var geojson = await _fixture.Client.GetAsync(
            $"/ogc/records/collections/{CatalogId}/items?f=geojson");
        await AssertOkAsync(geojson);
        geojson.Content.Headers.ContentType?.MediaType.Should().Be("application/geo+json");

        // Record items are GeoJSON-only; the metadata html format does not apply here.
        var html = await _fixture.Client.GetAsync(
            $"/ogc/records/collections/{CatalogId}/items?f=html");
        html.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /ogc/records/collections/{collectionId}")]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items/{recordId}")]
    public async Task RecordsEndpoints_WithUnknownCollection_ReturnNotFound()
    {
        var collection = await _fixture.Client.GetAsync("/ogc/records/collections/other-catalog");
        collection.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var items = await _fixture.Client.GetAsync("/ogc/records/collections/other-catalog/items");
        items.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var item = await _fixture.Client.GetAsync("/ogc/records/collections/other-catalog/items/service:test");
        item.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items/{recordId}")]
    public async Task GetItem_WithServiceRecordId_IsCaseInsensitive()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/records/collections/{CatalogId}/items/{Uri.EscapeDataString("SERVICE:TEST")}");

        await AssertOkAsync(response);

        using var json = await ReadJsonAsync(response);
        json.RootElement.GetProperty("id").GetString().Should().Be("service:test");
        json.RootElement.GetProperty("properties").GetProperty("type").GetString().Should().Be("service");
        json.RootElement.GetProperty("links").EnumerateArray()
            .Should().Contain(link => HrefEndsWith(link, "/rest/services/test/FeatureServer"));
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items/{recordId}")]
    public async Task GetItem_WithNonServiceRecordId_IsCaseSensitive()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/records/collections/{CatalogId}/items/{Uri.EscapeDataString($"LAYER:{WebAppFixture.TestLayerId}")}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    public async Task GetItems_WithIds_PreservesLegacyServiceCaseInsensitivityOnly()
    {
        var ids = Uri.EscapeDataString($"SERVICE:TEST,LAYER:{WebAppFixture.TestLayerId}");
        var matched = await GetRecordIdsAsync($"ids={ids}&limit=100");

        matched.Should().Contain("service:test");
        matched.Should().NotContain($"layer:{WebAppFixture.TestLayerId}");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items/{recordId}")]
    public async Task GetItem_WithUnknownQueryParameter_ReturnsBadRequest()
    {
        // Search parameters such as bbox are items-level only; the single-record
        // endpoint accepts only f.
        var response = await _fixture.Client.GetAsync(
            $"/ogc/records/collections/{CatalogId}/items/{Uri.EscapeDataString("service:test")}?bbox=-123,37,-122,38");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.ContentNegotiation)]
    [Endpoint("GET /ogc/records")]
    [Endpoint("GET /ogc/records/conformance")]
    public async Task Metadata_FormatParameter_SupportsHtmlAndRejectsUnknown()
    {
        var landingHtml = await _fixture.Client.GetAsync("/ogc/records?f=html");
        await AssertOkAsync(landingHtml);
        landingHtml.Content.Headers.ContentType?.MediaType.Should().Be("text/html");

        var conformanceHtml = await _fixture.Client.GetAsync("/ogc/records/conformance?f=html");
        await AssertOkAsync(conformanceHtml);
        conformanceHtml.Content.Headers.ContentType?.MediaType.Should().Be("text/html");

        var bogus = await _fixture.Client.GetAsync("/ogc/records?f=bogus");
        bogus.StatusCode.Should().Be(HttpStatusCode.BadRequest);
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

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "Body: {0}", body);
    }

    private static bool HasRel(JsonElement link, string rel)
        => link.TryGetProperty("rel", out var relElement) &&
           string.Equals(relElement.GetString(), rel, StringComparison.OrdinalIgnoreCase);

    private static bool HrefEndsWith(JsonElement link, string suffix)
        => link.TryGetProperty("href", out var hrefElement) &&
           hrefElement.GetString()?.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) == true;
}
