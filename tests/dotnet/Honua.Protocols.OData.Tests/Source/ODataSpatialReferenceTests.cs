// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Protocols.OData;

[Collection("Database")]
[Protocol(TestProtocols.ODataV4)]
public sealed class ODataSpatialReferenceTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private long _pointObjectId;

    public async Task InitializeAsync()
    {
        // All segments are relative literal path fragments (not user input), so none can be
        // rooted and silently drop earlier arguments.
        _fixture.UseSeed(Path.Join("tests", "seed", "spatial-reference.yaml"));
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

    [IntegrationTheory]
    [InlineData("", HttpStatusCode.BadRequest)]
    [InlineData("SRID=3857;", HttpStatusCode.OK)]
    [Operation(Operations.SpatialQuery)]
    [Endpoint("GET /odata/Features({layerId})?$filter=geo.intersects()")]
    public async Task Features_ProjectedGeometryLiteral_RequiresExplicitSrid(string prefix, HttpStatusCode expected)
    {
        var filter = $"geo.intersects(Geometry, geometry'{prefix}POLYGON((-13650000 4540000,-13600000 4540000,-13600000 4560000,-13650000 4560000,-13650000 4540000))')";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({SpatialReferenceTestLayerCatalog.PointLayerId})?$filter={Uri.EscapeDataString(filter)}");
        response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync());
        if (expected == HttpStatusCode.OK)
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("value").EnumerateArray()
                .Select(row => row.GetProperty("ObjectId").GetInt64()).Should().Contain(_pointObjectId);
        }
    }

    [IntegrationTheory]
    [InlineData("/odata/Features({0})?$filter={1}", "999999")]
    [InlineData("/odata/Features({0})?$filter={1}", "12345")]
    [InlineData("/odata/Features({0})?$filter={1}", "0")]
    [InlineData("/odata/Features({0})/$count?$filter={1}", "12345")]
    [InlineData("/odata/Features({0})/$apply?$apply=filter({1})/aggregate($count as n)", "12345")]
    [Operation(Operations.SpatialQuery)]
    [Endpoint("GET /odata/Features({layerId})?$filter=geo.intersects()")]
    public async Task Features_UnknownGeometryLiteralSrid_ReturnsBadRequest(string pathTemplate, string srid)
    {
        var filter = Uri.EscapeDataString($"geo.intersects(Geometry, geography'SRID={srid};POINT(0 0)')");
        var path = string.Format(System.Globalization.CultureInfo.InvariantCulture, pathTemplate, SpatialReferenceTestLayerCatalog.PointLayerId, filter);

        var response = await _fixture.Client.GetAsync(path);

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain(srid);
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
