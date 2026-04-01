// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Stac;

/// <summary>
/// Integration tests for the STAC Items endpoints.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Stac)]
public sealed class StacItemsTests : IAsyncLifetime
{
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
    public async Task GetItem_NotFound_Returns404()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var response = await _fixture.Client.GetAsync(
            $"/stac/collections/{collectionId}/items/999999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
