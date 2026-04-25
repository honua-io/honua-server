// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.Protocols.Stac;

/// <summary>
/// Integration tests for the STAC Items endpoints.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Stac)]
public sealed class StacItemsTests : IAsyncLifetime
{
    private static readonly string[] ExpectedItemExtensions =
    [
        "https://stac-extensions.github.io/eo/v1.1.0/schema.json",
        "https://stac-extensions.github.io/view/v1.0.0/schema.json"
    ];

    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /stac/collections/{collectionId}/items")]
    public async Task GetItems_ReturnsFeatureCollection()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var response = await _fixture.Client.GetAsync(
            $"/stac/collections/{collectionId}/items");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
        json.RootElement.GetProperty("features").EnumerateArray().Should().NotBeEmpty();
        json.RootElement.GetProperty("context").ValueKind.Should().Be(JsonValueKind.Object);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /stac/collections/{collectionId}/items")]
    public async Task GetItems_EachItemHasRequiredStacFields()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var response = await _fixture.Client.GetAsync(
            $"/stac/collections/{collectionId}/items?limit=3");

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        foreach (var item in json.RootElement.GetProperty("features").EnumerateArray())
        {
            item.GetProperty("type").GetString().Should().Be("Feature");
            item.GetProperty("id").GetString().Should().NotBeNullOrEmpty();
            item.GetProperty("stac_version").GetString().Should().Be("1.0.0");
            item.GetProperty("properties").ValueKind.Should().Be(JsonValueKind.Object);
            item.GetProperty("links").EnumerateArray().Should().NotBeEmpty();

            // STAC requires "datetime" in properties (may be null)
            item.GetProperty("properties").TryGetProperty("datetime", out _).Should().BeTrue();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /stac/collections/{collectionId}/items")]
    public async Task GetItems_IncludeDeclaredStacExtensionsWhenConfigured()
    {
        await UpdateLayerMetadataAsync(new CatalogMetadata
        {
            TimeInfo = new LayerTimeInfo { StartTimeField = "timestamp" },
            Stac = new StacCatalogMetadata
            {
                Extensions =
                [
                    "https://stac-extensions.github.io/eo/v1.1.0/schema.json",
                    "https://stac-extensions.github.io/view/v1.0.0/schema.json"
                ]
            }
        });

        var collectionId = WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var response = await _fixture.Client.GetAsync(
            $"/stac/collections/{collectionId}/items?limit=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        var item = json.RootElement.GetProperty("features").EnumerateArray().First();

        item.GetProperty("stac_extensions")
            .EnumerateArray()
            .Select(static element => element.GetString())
            .Should()
            .BeEquivalentTo(ExpectedItemExtensions);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /stac/collections/{collectionId}/items")]
    public async Task GetItems_WithLimit_RespectsLimit()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var response = await _fixture.Client.GetAsync(
            $"/stac/collections/{collectionId}/items?limit=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        var items = json.RootElement.GetProperty("features").EnumerateArray().ToArray();
        items.Length.Should().BeLessThanOrEqualTo(2);

        json.RootElement.GetProperty("context").GetProperty("limit")
            .GetInt32().Should().Be(2);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /stac/collections/{collectionId}/items")]
    public async Task GetItems_NonExistentCollection_Returns404()
    {
        var response = await _fixture.Client.GetAsync("/stac/collections/99999/items");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /stac/collections/{collectionId}/items")]
    public async Task GetItems_InvalidBbox_Returns400()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var response = await _fixture.Client.GetAsync(
            $"/stac/collections/{collectionId}/items?bbox=1,2,3");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /stac/collections/{collectionId}/items")]
    public async Task GetItems_OutOfRangeBbox_Returns400()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var response = await _fixture.Client.GetAsync(
            $"/stac/collections/{collectionId}/items?bbox=200,95,210,100");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /stac/collections/{collectionId}/items")]
    public async Task GetItems_WithDatelineCrossingBbox_ReturnsMatchingItem()
    {
        var featureId = await InsertDatelineFeatureAsync(179.9, 0.0, "Dateline STAC Item");
        var collectionId = WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var response = await _fixture.Client.GetAsync(
            $"/stac/collections/{collectionId}/items?bbox={Uri.EscapeDataString("170,-10,-170,10")}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        var features = json.RootElement.GetProperty("features").EnumerateArray().ToArray();

        features.Should().Contain(feature =>
            feature.GetProperty("id").GetString() == featureId.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
            feature.GetProperty("properties").GetProperty("name").GetString() == "Dateline STAC Item");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /stac/collections/{collectionId}/items")]
    public async Task GetItems_WithThreeDimensionalBbox_Returns400()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var response = await _fixture.Client.GetAsync(
            $"/stac/collections/{collectionId}/items?bbox=170,-10,-170,10,5,6");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /stac/collections/{collectionId}/items")]
    public async Task GetItems_InvalidDatetime_Returns400()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var response = await _fixture.Client.GetAsync(
            $"/stac/collections/{collectionId}/items?datetime=not-a-datetime");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /stac/collections/{collectionId}/items")]
    public async Task GetItems_EncodesDatetimeInPaginationLinks()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var datetime = "2020-01-01T00:00:00+00:00/2020-01-02T00:00:00+00:00";
        var encodedDatetime = Uri.EscapeDataString(datetime);

        var response = await _fixture.Client.GetAsync(
            $"/stac/collections/{collectionId}/items?limit=1&datetime={encodedDatetime}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        var links = json.RootElement.GetProperty("links").EnumerateArray().ToArray();

        var relevantLinks = links
            .Where(link =>
            {
                var rel = link.GetProperty("rel").GetString();
                return rel is "self" or "next";
            })
            .ToArray();

        relevantLinks.Should().NotBeEmpty();
        foreach (var link in relevantLinks)
        {
            link.GetProperty("href").GetString().Should().Contain($"datetime={encodedDatetime}");
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /stac/collections/{collectionId}/items")]
    public async Task GetItems_EncodesBboxInPaginationLinks()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var bbox = "-158.30,21.20,-157.70,21.70";
        var encodedBbox = Uri.EscapeDataString(bbox);

        var response = await _fixture.Client.GetAsync(
            $"/stac/collections/{collectionId}/items?limit=1&bbox={encodedBbox}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        var links = json.RootElement.GetProperty("links").EnumerateArray().ToArray();

        var relevantLinks = links
            .Where(link =>
            {
                var rel = link.GetProperty("rel").GetString();
                return rel is "self" or "next";
            })
            .ToArray();

        relevantLinks.Should().NotBeEmpty();
        foreach (var link in relevantLinks)
        {
            link.GetProperty("href").GetString().Should().Contain($"bbox={encodedBbox}");
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /stac/collections/{collectionId}/items/{itemId}")]
    public async Task GetItem_ById_ReturnsStacItem()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var featureId = await _fixture.InsertFeatureAsync(WebAppFixture.TestLayerId, "STAC Test Item");

        var response = await _fixture.Client.GetAsync(
            $"/stac/collections/{collectionId}/items/{featureId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("type").GetString().Should().Be("Feature");
        json.RootElement.GetProperty("id").GetString().Should().Be(featureId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        json.RootElement.GetProperty("collection").GetString().Should().Be(collectionId);
        json.RootElement.GetProperty("links").EnumerateArray().Should().NotBeEmpty();

        // Should have cross-protocol asset links
        json.RootElement.GetProperty("assets").GetProperty("geojson")
            .GetProperty("href").GetString().Should().Contain("/ogc/features/collections/");
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /stac/collections/{collectionId}/items/{itemId}")]
    public async Task GetItem_ByStringId_ReturnsStacItem()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var featureId = await _fixture.InsertFeatureAsync(WebAppFixture.TestLayerId, "STAC String ID Item");
        await PromoteStacItemIdAsync(featureId, "stac-item-alpha");

        var response = await _fixture.Client.GetAsync(
            $"/stac/collections/{collectionId}/items/stac-item-alpha");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("id").GetString().Should().Be("stac-item-alpha");
        json.RootElement.GetProperty("properties").TryGetProperty("id", out _).Should().BeFalse();
        json.RootElement.GetProperty("assets").GetProperty("geojson")
            .GetProperty("href").GetString().Should().Contain("/ogc/features/collections/");
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /stac/collections/{collectionId}/items/{itemId}")]
    public async Task GetItem_ByStacId_PrefersCanonicalStacIdBeyondIdCollisions()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var canonicalItemId = $"stac-collision-{Guid.NewGuid():N}";

        for (var index = 0; index < 8; index++)
        {
            var collisionFeatureId = await _fixture.InsertFeatureAsync(WebAppFixture.TestLayerId, $"Collision Item {index}");
            await SetFeatureAttributeAsync(collisionFeatureId, "id", canonicalItemId);
        }

        var targetFeatureId = await _fixture.InsertFeatureAsync(WebAppFixture.TestLayerId, "Canonical STAC ID Item");
        await SetFeatureAttributeAsync(targetFeatureId, "stac_id", canonicalItemId);

        var response = await _fixture.Client.GetAsync(
            $"/stac/collections/{collectionId}/items/{canonicalItemId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("id").GetString().Should().Be(canonicalItemId);
        json.RootElement.GetProperty("properties").GetProperty("name").GetString().Should().Be("Canonical STAC ID Item");

        var assetHref = json.RootElement.GetProperty("assets").GetProperty("geojson")
            .GetProperty("href").GetString();
        assetHref.Should().NotBeNullOrWhiteSpace();
        assetHref.Should().EndWith($"/ogc/features/collections/{collectionId}/items/{targetFeatureId}");

        var assetResponse = await _fixture.Client.GetAsync(ToRelativeUri(assetHref!));
        assetResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /stac/collections/{collectionId}/items/{itemId}")]
    public async Task GetItem_ByNumericLookingStacId_ReturnsStacItemBeforeObjectIdFallback()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var featureId = await _fixture.InsertFeatureAsync(WebAppFixture.TestLayerId, "STAC Numeric ID Item");
        var numericStacId = "9000000001";
        await PromoteStacItemIdAsync(featureId, numericStacId);

        var response = await _fixture.Client.GetAsync(
            $"/stac/collections/{collectionId}/items/{numericStacId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("id").GetString().Should().Be(numericStacId);
        var assetHref = json.RootElement.GetProperty("assets").GetProperty("geojson")
            .GetProperty("href").GetString();
        assetHref.Should().NotBeNullOrWhiteSpace();

        var assetResponse = await _fixture.Client.GetAsync(ToRelativeUri(assetHref!));
        assetResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /stac/collections/{collectionId}/items/{itemId}")]
    public async Task GetItem_NotFound_Returns404()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var response = await _fixture.Client.GetAsync(
            $"/stac/collections/{collectionId}/items/999999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private Task UpdateLayerMetadataAsync(CatalogMetadata metadata)
    {
        var updater = _fixture.GetService<ILayerMetadataUpdater>();
        return updater.UpdateLayerMetadataAsync(WebAppFixture.TestLayerId, metadata);
    }

    private async Task<long> InsertDatelineFeatureAsync(double lon, double lat, string name)
    {
        var schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Schema was not initialized.");
        await using var connection = await _fixture.Postgres.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO features (layer_id, geometry, attributes)
            VALUES (
                @layerId,
                ST_SetSRID(ST_MakePoint(@lon, @lat), 4326),
                jsonb_build_object('name', @name)
            )
            RETURNING objectid;
            """;
        command.Parameters.AddWithValue("layerId", WebAppFixture.TestLayerId);
        command.Parameters.AddWithValue("lon", lon);
        command.Parameters.AddWithValue("lat", lat);
        command.Parameters.AddWithValue("name", name);

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task PromoteStacItemIdAsync(long featureId, string itemId)
        => await SetFeatureAttributeAsync(featureId, "id", itemId);

    private static string ToRelativeUri(string href)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out var absoluteUri))
        {
            return href;
        }

        return absoluteUri.PathAndQuery;
    }

    private async Task SetFeatureAttributeAsync(long featureId, string attributeName, string attributeValue)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE features
            SET attributes = attributes || jsonb_build_object(@attributeName, @attributeValue)
            WHERE objectid = @featureId;
            """;
        command.Parameters.Add(new NpgsqlParameter { ParameterName = "@attributeName", Value = attributeName });
        command.Parameters.Add(new NpgsqlParameter { ParameterName = "@attributeValue", Value = attributeValue });
        command.Parameters.Add(new NpgsqlParameter { ParameterName = "@featureId", Value = featureId });
        await command.ExecuteNonQueryAsync();
    }
}
