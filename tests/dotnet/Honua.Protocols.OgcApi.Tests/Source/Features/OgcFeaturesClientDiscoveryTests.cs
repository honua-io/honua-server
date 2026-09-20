// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

[Collection("Database")]
[Protocol(TestProtocols.OgcApiFeatures)]
public sealed class OgcFeaturesClientDiscoveryTests : IAsyncLifetime
{
    private const string Crs84 = "http://www.opengis.net/def/crs/OGC/1.3/CRS84";
    private const string Epsg4326 = "http://www.opengis.net/def/crs/EPSG/0/4326";
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/features/collections")]
    [Endpoint("GET /ogc/features/collections/{collectionId}")]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task Wgs84Collection_AdvertisesLongitudeFirstStorage_AndHonorsExplicitAxisOrder()
    {
        using var collection = await GetJsonAsync("/ogc/features/collections/0");
        collection.RootElement.GetProperty("storageCrs").GetString().Should().Be(Crs84);
        collection.RootElement.GetProperty("crs")[0].GetString().Should().Be(Crs84);
        using var collections = await GetJsonAsync("/ogc/features/collections");
        collections.RootElement.GetProperty("collections").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "0")
            .GetProperty("storageCrs").GetString().Should().Be(Crs84);

        // Literal oracle from tests/seed/server.yaml, not a server-derived snapshot.
        foreach (var (query, crs, x, y) in new[]
        {
            ("", Crs84, -122.5, 37.5),
            ($"?crs={Uri.EscapeDataString(Crs84)}", Crs84, -122.5, 37.5),
            ($"?crs={Uri.EscapeDataString(Epsg4326)}", Epsg4326, 37.5, -122.5),
        })
        {
            using var response = await _fixture.Client.GetAsync($"/ogc/features/collections/0/items/1{query}");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Headers.GetValues("Content-Crs").Should().Equal($"<{crs}>");
            using var feature = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            feature.RootElement.GetProperty("id").GetInt64().Should().Be(1);
            var geometry = feature.RootElement.GetProperty("geometry");
            geometry.GetProperty("type").GetString().Should().Be("Point");
            geometry.GetProperty("coordinates").EnumerateArray().Select(value => value.GetDouble())
                .Should().Equal(x, y);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/conformance")]
    [Endpoint("GET /ogc/features/collections/{collectionId}/queryables")]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task FilterDiscovery_AdvertisesImplementedLanguages_AndFiltersSeededValues()
    {
        using var conformance = await GetJsonAsync("/ogc/features/conformance");
        conformance.RootElement.GetProperty("conformsTo").EnumerateArray().Select(value => value.GetString())
            .Should().Contain(new[]
            {
                "http://www.opengis.net/spec/ogcapi-features-3/1.0/conf/queryables",
                "http://www.opengis.net/spec/ogcapi-features-3/1.0/conf/filter",
                "http://www.opengis.net/spec/ogcapi-features-3/1.0/conf/features-filter",
                "http://www.opengis.net/spec/cql2/1.0/conf/basic-cql2",
                "http://www.opengis.net/spec/cql2/1.0/conf/cql2-text",
                "http://www.opengis.net/spec/cql2/1.0/conf/cql2-json",
            });
        using var queryables = await GetJsonAsync("/ogc/features/collections/0/queryables");
        queryables.RootElement.GetProperty("properties").GetProperty("category")
            .GetProperty("type").GetString().Should().Be("string");
        queryables.RootElement.GetProperty("queryables").EnumerateArray()
            .Select(property => property.GetProperty("id").GetString()).Should().Contain("category");
        using var api = await GetJsonAsync("/ogc/features/api");
        api.RootElement.GetProperty("paths").GetProperty("/collections/{collectionId}/items")
            .GetProperty("get").GetProperty("parameters").EnumerateArray()
            .Single(parameter => parameter.GetProperty("name").GetString() == "filter-lang")
            .GetProperty("schema").GetProperty("enum").EnumerateArray()
            .Select(value => value.GetString()).Should().Contain("cql-text");

        foreach (var (language, filter) in new[]
        {
            ("cql2-text", "category = 'test'"),
            ("cql-text", "category = 'test'"),
            ("cql2-json", """{"op":"=","args":[{"property":"category"},"test"]}"""),
        })
        {
            using var page = await GetJsonAsync($"/ogc/features/collections/0/items?filter-lang={language}&filter={Uri.EscapeDataString(filter)}");
            // server.yaml: test rows 1, 3, 5; sample rows 2, 4 must be excluded.
            page.RootElement.GetProperty("numberReturned").GetInt32().Should().Be(3);
            var features = page.RootElement.GetProperty("features").EnumerateArray().ToArray();
            features.Select(feature => feature.GetProperty("id").GetInt64()).Order()
                .Should().Equal(1, 3, 5);
            features.Should().OnlyContain(feature => feature.GetProperty("properties").GetProperty("category").GetString() == "test");
            features.Single(feature => feature.GetProperty("id").GetInt64() == 3)
                .GetProperty("geometry").ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string uri)
    {
        using var response = await _fixture.Client.GetAsync(uri);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body);
    }
}
