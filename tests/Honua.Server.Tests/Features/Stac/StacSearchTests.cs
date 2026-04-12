// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Stac;

/// <summary>
/// Integration tests for the STAC Search endpoints.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Stac)]
public sealed class StacSearchTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("GET /stac/search")]
    public async Task SearchGet_NoFilters_ReturnsItems()
    {
        var response = await _fixture.Client.GetAsync("/stac/search");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
        json.RootElement.GetProperty("features").EnumerateArray().Should().NotBeEmpty();
        json.RootElement.GetProperty("context").ValueKind.Should().Be(JsonValueKind.Object);
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("GET /stac/search")]
    public async Task SearchGet_WithLimit_RespectsLimit()
    {
        var response = await _fixture.Client.GetAsync("/stac/search?limit=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        var features = json.RootElement.GetProperty("features").EnumerateArray().ToArray();
        features.Length.Should().BeLessThanOrEqualTo(2);
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("GET /stac/search")]
    public async Task SearchGet_WithCollections_FiltersResults()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var response = await _fixture.Client.GetAsync(
            $"/stac/search?collections={collectionId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        foreach (var item in json.RootElement.GetProperty("features").EnumerateArray())
        {
            item.GetProperty("collection").GetString().Should().Be(collectionId);
        }
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("POST /stac/search")]
    public async Task SearchPost_WithBody_ReturnsItems()
    {
        var body = JsonSerializer.Serialize(new
        {
            limit = 5,
            collections = new[] { WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture) }
        });

        var response = await _fixture.Client.PostAsync(
            "/stac/search",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
        json.RootElement.GetProperty("features").EnumerateArray().Should().NotBeEmpty();
        json.RootElement.GetProperty("context").GetProperty("limit")
            .GetInt32().Should().Be(5);
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("POST /stac/search")]
    public async Task SearchPost_WithBbox_ReturnsFilteredResults()
    {
        var body = JsonSerializer.Serialize(new
        {
            bbox = new[] { -180.0, -90.0, 180.0, 90.0 },
            limit = 5
        });

        var response = await _fixture.Client.PostAsync(
            "/stac/search",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("GET /stac/search")]
    public async Task SearchGet_WithThreeDimensionalBbox_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync("/stac/search?bbox=170,-10,-170,10,5,6");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("POST /stac/search")]
    public async Task SearchPost_WithThreeDimensionalBbox_ReturnsBadRequest()
    {
        var body = JsonSerializer.Serialize(new
        {
            bbox = new[] { 170.0, -10.0, -170.0, 10.0, 5.0, 6.0 }
        });

        var response = await _fixture.Client.PostAsync(
            "/stac/search",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("POST /stac/search")]
    public async Task SearchPost_AllItemsHaveStacFields()
    {
        var body = JsonSerializer.Serialize(new { limit = 3 });

        var response = await _fixture.Client.PostAsync(
            "/stac/search",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        foreach (var item in json.RootElement.GetProperty("features").EnumerateArray())
        {
            item.GetProperty("type").GetString().Should().Be("Feature");
            item.GetProperty("stac_version").GetString().Should().Be("1.0.0");
            item.GetProperty("properties").TryGetProperty("datetime", out _).Should().BeTrue();
            item.GetProperty("links").EnumerateArray().Should().NotBeEmpty();
        }
    }
}
