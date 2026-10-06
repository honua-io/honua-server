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
/// honua-server#5510: an OGC API Features client reading the Records catalogue starts at the
/// landing page's API definition link and then pages <c>items</c> with a large <c>limit</c>.
/// OGC API - Records Part 1 <c>/req/core/root-success</c> (inherited from OGC API - Common)
/// requires the landing page to link the API definition, and OGC API - Features Part 1 §7.15.4
/// requires a <c>limit</c> above the maximum to be served at the maximum, not rejected.
/// </summary>
[Collection("Database.OgcApiData")]
[Protocol(TestProtocols.OgcApiRecords)]
public sealed class RecordsLandingAndLimitTests : IClassFixture<OgcRecordsEndpointTestsFixture>
{
    private const string CatalogId = "honua-catalog";
    private const int MaximumLimit = 1000;
    private readonly WebAppFixture _fixture;

    public RecordsLandingAndLimitTests(OgcRecordsEndpointTestsFixture fixture)
    {
        _fixture = fixture.App;
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/records")]
    public async Task GetLandingPage_LinksApiDefinition_AndLinkResolves()
    {
        var response = await _fixture.Client.GetAsync("/ogc/records");
        await AssertOkAsync(response);

        using var json = await ReadJsonAsync(response);
        var serviceDesc = json.RootElement.GetProperty("links").EnumerateArray()
            .Where(link => HasRel(link, "service-desc"))
            .ToArray();
        serviceDesc.Should().ContainSingle("the Records landing page must link its API definition");
        serviceDesc[0].GetProperty("type").GetString().Should().Be("application/vnd.oai.openapi+json;version=3.0");

        var href = new Uri(serviceDesc[0].GetProperty("href").GetString()!);
        href.AbsolutePath.Should().Be("/ogc/records/openapi.json");
        var definition = await _fixture.Client.GetAsync(href.PathAndQuery);
        await AssertOkAsync(definition);
        using var definitionJson = await ReadJsonAsync(definition);
        definitionJson.RootElement.GetProperty("openapi").GetString().Should().StartWith("3.");
        definitionJson.RootElement.GetProperty("paths").TryGetProperty("/collections/{collectionId}/items", out _)
            .Should().BeTrue("the linked API definition must describe this API's record search");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/records/openapi.json")]
    public async Task GetApiDefinition_DescribesRecordsPaths_AndAdvertisesTheEnforcedLimitBounds()
    {
        var response = await _fixture.Client.GetAsync("/ogc/records/openapi.json");
        await AssertOkAsync(response);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.oai.openapi+json");

        using var json = await ReadJsonAsync(response);
        var root = json.RootElement;
        root.GetProperty("info").GetProperty("title").GetString().Should().Be("Honua OGC API Records");
        root.GetProperty("servers")[0].GetProperty("url").GetString().Should().EndWith("/ogc/records");

        var paths = root.GetProperty("paths").EnumerateObject().Select(path => path.Name).ToArray();
        paths.Should().BeEquivalentTo(
            "/",
            "/openapi.json",
            "/conformance",
            "/collections",
            "/collections/{collectionId}",
            "/collections/{collectionId}/items",
            "/collections/{collectionId}/items/{recordId}");

        var limit = root.GetProperty("paths").GetProperty("/collections/{collectionId}/items")
            .GetProperty("get").GetProperty("parameters").EnumerateArray()
            .Single(parameter => parameter.GetProperty("name").GetString() == "limit")
            .GetProperty("schema");
        limit.GetProperty("minimum").GetInt32().Should().Be(1);
        limit.GetProperty("maximum").GetInt32().Should().Be(MaximumLimit);
        limit.GetProperty("default").GetInt32().Should().Be(10);

        var unsupported = await _fixture.Client.GetAsync("/ogc/records/openapi.json?f=html");
        unsupported.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    public async Task GetItems_WithLimitAboveMaximum_ServesTheMaximumInsteadOfRejecting()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/records/collections/{CatalogId}/items?limit=3000");
        await AssertOkAsync(response);

        using var json = await ReadJsonAsync(response);
        var numberMatched = json.RootElement.GetProperty("numberMatched").GetInt32();
        numberMatched.Should().BeGreaterThan(1);
        json.RootElement.GetProperty("numberReturned").GetInt32()
            .Should().Be(Math.Min(numberMatched, MaximumLimit));
        json.RootElement.GetProperty("features").GetArrayLength()
            .Should().Be(Math.Min(numberMatched, MaximumLimit));
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    public async Task GetItems_WithLimitAboveMaximum_PagingLinksCarryTheClampedLimit()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/records/collections/{CatalogId}/items?limit=3000&offset=1");
        await AssertOkAsync(response);

        using var json = await ReadJsonAsync(response);
        var prev = json.RootElement.GetProperty("links").EnumerateArray()
            .Single(link => HasRel(link, "prev"));
        var href = prev.GetProperty("href").GetString();
        href.Should().Contain($"limit={MaximumLimit}", "paging links must reflect the limit actually applied");
        href.Should().NotContain("limit=3000");
        href.Should().Contain("offset=0");
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items/{recordId}")]
    public async Task GetItem_ServiceRecord_ListsEachLayerIdOnce()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/records/collections/{CatalogId}/items/service:test");
        await AssertOkAsync(response);

        using var json = await ReadJsonAsync(response);
        var layerIds = json.RootElement.GetProperty("properties").GetProperty("layerIds")
            .EnumerateArray()
            .Select(value => value.GetInt32())
            .ToArray();
        layerIds.Should().Contain(WebAppFixture.TestLayerId);
        layerIds.Should().OnlyHaveUniqueItems();
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
}
