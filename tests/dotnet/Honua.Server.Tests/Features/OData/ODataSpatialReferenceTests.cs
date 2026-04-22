// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.OData;

[Collection("Database")]
[Protocol(Protocols.ODataV4)]
public sealed class ODataSpatialReferenceTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private long _pointObjectId;

    public async Task InitializeAsync()
    {
        _fixture.UseSeed(Path.Combine("tests", "seed", "spatial-reference.yaml"));
        await _fixture.InitializeAsync();

        var schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Schema was not initialized.");
        _pointObjectId = await SpatialReferenceTestData.InsertPointAsync(
            _fixture.Postgres,
            schema,
            SpatialReferenceTestLayerCatalog.PointLayerId,
            -122.4194,
            37.7749,
            "San Francisco");
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.SpatialQuery)]
    [Endpoint("GET /odata/Features({layerId})?$filter=geo.intersects()")]
    public async Task Features_WithGeoIntersects_TransformsFilterToLayerSrid()
    {
        var filter = "geo.intersects(Geometry, geography'POLYGON((-122.5 37.7, -122.3 37.7, -122.3 37.9, -122.5 37.9, -122.5 37.7))')";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({SpatialReferenceTestLayerCatalog.PointLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var values = document.RootElement.GetProperty("value").EnumerateArray().ToArray();

        values.Should().HaveCount(1);
        values[0].GetProperty("ObjectId").GetInt64().Should().Be(_pointObjectId);
    }
}
