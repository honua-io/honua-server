// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Stac;

[Collection("Database")]
[Protocol(Protocols.Stac)]
public sealed class StacProjectedGeometryTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        _fixture.UseSeed(Path.Combine("tests", "seed", "spatial-reference.yaml"));
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /stac/collections/{collectionId}/items/{itemId}")]
    public async Task GetItem_FromProjectedLayer_ReturnsWgs84GeometryAndBbox()
    {
        var schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Schema was not initialized.");
        var featureId = await SpatialReferenceTestData.InsertPointAsync(
            _fixture.Postgres,
            schema,
            SpatialReferenceTestLayerCatalog.PointLayerId,
            lon: -122.4194,
            lat: 37.7749,
            name: "Projected STAC Item");

        var response = await _fixture.Client.GetAsync(
            $"/stac/collections/{SpatialReferenceTestLayerCatalog.PointLayerId}/items/{featureId}");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        var json = JsonDocument.Parse(content);
        var coordinates = json.RootElement
            .GetProperty("geometry")
            .GetProperty("coordinates")
            .EnumerateArray()
            .Select(static coordinate => coordinate.GetDouble())
            .ToArray();
        coordinates[0].Should().BeApproximately(-122.4194, 1e-5);
        coordinates[1].Should().BeApproximately(37.7749, 1e-5);

        var bbox = json.RootElement
            .GetProperty("bbox")
            .EnumerateArray()
            .Select(static coordinate => coordinate.GetDouble())
            .ToArray();
        bbox[0].Should().BeApproximately(-122.4194, 1e-5);
        bbox[1].Should().BeApproximately(37.7749, 1e-5);
        bbox[2].Should().BeApproximately(-122.4194, 1e-5);
        bbox[3].Should().BeApproximately(37.7749, 1e-5);
    }
}
