// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Stac;

/// <summary>
/// Integration tests for the STAC Collections endpoints.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Stac)]
public sealed class StacCollectionsTests : IAsyncLifetime
{
    private static readonly string[] ExpectedCollectionKeywords = ["imagery", "ops-demo"];
    private static readonly string[] ExpectedCollectionExtensions =
    [
        "https://stac-extensions.github.io/eo/v1.1.0/schema.json",
        "https://stac-extensions.github.io/projection/v1.1.0/schema.json"
    ];

    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /stac/collections")]
    public async Task GetCollections_ReturnsCollectionsList()
    {
        var response = await _fixture.Client.GetAsync("/stac/collections");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType
            .Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("collections").EnumerateArray().Should().NotBeEmpty();
        json.RootElement.GetProperty("links").EnumerateArray().Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /stac/collections")]
    public async Task GetCollections_ReturnsStrongETagAndSupportsConditionalRequest()
    {
        var firstResponse = await _fixture.Client.GetAsync("/stac/collections");

        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        firstResponse.Headers.ETag.Should().NotBeNull();
        firstResponse.Headers.ETag!.IsWeak.Should().BeFalse();

        using var conditionalRequest = new HttpRequestMessage(HttpMethod.Get, "/stac/collections");
        conditionalRequest.Headers.TryAddWithoutValidation("If-None-Match", firstResponse.Headers.ETag.ToString());

        var secondResponse = await _fixture.Client.SendAsync(conditionalRequest);

        secondResponse.StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /stac/collections")]
    public async Task GetCollections_EachHasRequiredStacFields()
    {
        var response = await _fixture.Client.GetAsync("/stac/collections");
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        foreach (var collection in json.RootElement.GetProperty("collections").EnumerateArray())
        {
            collection.GetProperty("type").GetString().Should().Be("Collection");
            collection.GetProperty("id").GetString().Should().NotBeNullOrEmpty();
            collection.GetProperty("stac_version").GetString().Should().Be("1.0.0");
            collection.GetProperty("description").GetString().Should().NotBeNullOrEmpty();
            collection.GetProperty("license").GetString().Should().NotBeNullOrEmpty();
            collection.GetProperty("extent").ValueKind.Should().Be(JsonValueKind.Object);
            collection.GetProperty("links").EnumerateArray().Should().NotBeEmpty();
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /stac/collections/{collectionId}")]
    public async Task GetCollection_ById_ReturnsStacCollection()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var response = await _fixture.Client.GetAsync($"/stac/collections/{collectionId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("type").GetString().Should().Be("Collection");
        json.RootElement.GetProperty("id").GetString().Should().Be(collectionId);
        json.RootElement.GetProperty("stac_version").GetString().Should().Be("1.0.0");
        json.RootElement.GetProperty("extent").GetProperty("spatial").ValueKind
            .Should().Be(JsonValueKind.Object);
        json.RootElement.GetProperty("extent").GetProperty("temporal").ValueKind
            .Should().Be(JsonValueKind.Object);
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /stac/collections/{collectionId}")]
    public async Task GetCollection_ById_ReturnsDeclaredStacMetadata()
    {
        await UpdateLayerMetadataAsync(new CatalogMetadata
        {
            TimeInfo = new LayerTimeInfo { StartTimeField = "timestamp" },
            Stac = new StacCatalogMetadata
            {
                License = "CC-BY-4.0",
                Keywords = ["imagery", "ops-demo"],
                Extensions =
                [
                    "https://stac-extensions.github.io/eo/v1.1.0/schema.json",
                    "https://stac-extensions.github.io/projection/v1.1.0/schema.json"
                ]
            }
        });

        var collectionId = WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var response = await _fixture.Client.GetAsync($"/stac/collections/{collectionId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("license").GetString().Should().Be("CC-BY-4.0");
        json.RootElement.GetProperty("keywords")
            .EnumerateArray()
            .Select(static element => element.GetString())
            .Should()
            .BeEquivalentTo(ExpectedCollectionKeywords);
        json.RootElement.GetProperty("stac_extensions")
            .EnumerateArray()
            .Select(static element => element.GetString())
            .Should()
            .BeEquivalentTo(ExpectedCollectionExtensions);
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /stac/collections/{collectionId}")]
    public async Task GetCollection_ById_ReturnsStrongETagAndSupportsConditionalRequest()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var firstResponse = await _fixture.Client.GetAsync($"/stac/collections/{collectionId}");

        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        firstResponse.Headers.ETag.Should().NotBeNull();
        firstResponse.Headers.ETag!.IsWeak.Should().BeFalse();

        using var conditionalRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/stac/collections/{collectionId}");
        conditionalRequest.Headers.TryAddWithoutValidation("If-None-Match", firstResponse.Headers.ETag.ToString());

        var secondResponse = await _fixture.Client.SendAsync(conditionalRequest);

        secondResponse.StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /stac/collections/{collectionId}")]
    public async Task GetCollection_NotFound_Returns404()
    {
        var response = await _fixture.Client.GetAsync("/stac/collections/99999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /stac/collections/{collectionId}")]
    public async Task GetCollection_HasCrossProtocolLinks()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var response = await _fixture.Client.GetAsync($"/stac/collections/{collectionId}");

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        var links = json.RootElement.GetProperty("links")
            .EnumerateArray()
            .Select(l => new
            {
                Rel = l.GetProperty("rel").GetString(),
                Href = l.GetProperty("href").GetString()
            })
            .ToArray();

        links.Should().Contain(l => l.Rel == "self");
        links.Should().Contain(l => l.Rel == "root");
        links.Should().Contain(l => l.Rel == "items");
        links.Should().Contain(l => l.Rel == "alternate" &&
                                    l.Href!.Contains("/ogc/features/collections/"));
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /stac/collections/{collectionId}")]
    public async Task GetCollection_WithProjectedExtent_UsesTransformFallbackForSpatialBbox()
    {
        var fixture = new WebAppFixture()
            .ReplaceService<ILayerCatalog>(new ProjectedExtentLayerCatalog());

        try
        {
            await fixture.InitializeAsync();

            var response = await fixture.Client.GetAsync($"/stac/collections/{ProjectedExtentLayerCatalog.LayerId}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var bbox = json.RootElement
                .GetProperty("extent")
                .GetProperty("spatial")
                .GetProperty("bbox")[0];

            bbox[0].GetDouble().Should().BeNegative();
            bbox[1].GetDouble().Should().BeGreaterThan(0d);
            bbox[2].GetDouble().Should().BeNegative();
            bbox[2].GetDouble().Should().BeGreaterThan(bbox[0].GetDouble());
            bbox[3].GetDouble().Should().BeGreaterThan(bbox[1].GetDouble());
            bbox[1].GetDouble().Should().NotBe(-90d);
            bbox[3].GetDouble().Should().NotBe(90d);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private Task UpdateLayerMetadataAsync(CatalogMetadata metadata)
    {
        var updater = _fixture.GetService<ILayerMetadataUpdater>();
        return updater.UpdateLayerMetadataAsync(WebAppFixture.TestLayerId, metadata);
    }
}
