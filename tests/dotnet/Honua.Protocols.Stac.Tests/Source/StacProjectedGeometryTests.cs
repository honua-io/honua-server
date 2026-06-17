// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Protocols.Stac;

/// <summary>
/// Per-class fixture that configures the spatial-reference seed once for the whole
/// <see cref="StacProjectedGeometryTests"/> class. <see cref="WebAppFixture"/> is sealed, so we
/// wrap (not subclass) it and delegate the async lifecycle to it.
/// </summary>
public sealed class StacProjectedGeometryTestsFixture : IAsyncLifetime
{
    public WebAppFixture App { get; }

    public StacProjectedGeometryTestsFixture()
    {
        App = new WebAppFixture()
            .UseSeed(Path.Combine("tests", "seed", "spatial-reference.yaml"));
    }

    public Task InitializeAsync() => App.InitializeAsync();

    public Task DisposeAsync() => App.DisposeAsync();
}

[Protocol(TestProtocols.Stac)]
[Collection("Database")]
public sealed class StacProjectedGeometryTests : IClassFixture<StacProjectedGeometryTestsFixture>
{
    private readonly WebAppFixture _fixture;

    public StacProjectedGeometryTests(StacProjectedGeometryTestsFixture fixture) => _fixture = fixture.App;

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
