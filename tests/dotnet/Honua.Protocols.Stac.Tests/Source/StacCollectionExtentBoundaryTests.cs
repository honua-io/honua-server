// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Stac;

[Collection("Database")]
[Protocol(TestProtocols.Stac)]
public sealed class StacCollectionExtentBoundaryTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData("list", new double[] { -180d, -90d, 180d, 90d })]
    [InlineData("detail", new double[] { -180d, -90d, 180d, 90d })]
    [InlineData("list", new double[] { 0d, 0d, 0d, 0d })]
    [InlineData("detail", new double[] { 0d, 0d, 0d, 0d })]
    [InlineData("list", new double[] { 170d, -20d, -170d, 20d })]
    [InlineData("detail", new double[] { 170d, -20d, -170d, 20d })]
    [InlineData("list", new double[] { 180d, 90d, 180d, 90d })]
    [InlineData("detail", new double[] { 180d, 90d, 180d, 90d })]
    [Operation(Operations.Query)]
    [Operation(Operations.GetById)]
    [Endpoint("GET /stac/collections")]
    [Endpoint("GET /stac/collections/{collectionId}")]
    public async Task GetCollection_WithValidBoundaryExtent_PreservesCoordinates(
        string route, double[] coordinates)
    {
        SetExtent(coordinates);

        var bbox = await ReadExtentAsync(route);

        bbox.Should().Equal(coordinates);
    }

    [IntegrationTheory]
    [InlineData("list", new double[] { -180d, -90d, 181d, 90d })]
    [InlineData("detail", new double[] { -180d, -90d, 181d, 90d })]
    [InlineData("list", new double[] { -180d, -90d, 180d, 91d })]
    [InlineData("detail", new double[] { -180d, -90d, 180d, 91d })]
    [InlineData("list", new double[] { -10d, 20d, 10d, -20d })]
    [InlineData("detail", new double[] { -10d, 20d, 10d, -20d })]
    [Operation(Operations.Query)]
    [Operation(Operations.GetById)]
    [Endpoint("GET /stac/collections")]
    [Endpoint("GET /stac/collections/{collectionId}")]
    public async Task GetCollection_WithInvalidGeographicExtent_UsesWorldFallback(
        string route, double[] coordinates)
    {
        SetExtent(coordinates);

        var bbox = await ReadExtentAsync(route);

        bbox.Should().Equal(-180d, -90d, 180d, 90d);
    }

    [IntegrationTheory]
    [InlineData("list")]
    [InlineData("detail")]
    [Operation(Operations.Query)]
    [Operation(Operations.GetById)]
    [Endpoint("GET /stac/collections")]
    [Endpoint("GET /stac/collections/{collectionId}")]
    public async Task GetCollection_WithoutTemporalFields_UsesOpenInterval(string route)
    {
        _fixture.UpdateV2ResourceMetadata(WebAppFixture.TestLayerId, clearTemporal: true);
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var resourceId = snapshot.Graph.Publications.First(p => p.LayerIndex == WebAppFixture.TestLayerId).ResourceId;
        foreach (var field in snapshot.Graph.Resources.First(r => r.Metadata.Id == resourceId).SchemaFields
                     .Where(f => f.Type is MetadataV2FieldType.Date or MetadataV2FieldType.DateTime or MetadataV2FieldType.Time))
        {
            _fixture.UpdateV2ResourceSchemaField(WebAppFixture.TestLayerId, field with { Type = MetadataV2FieldType.String });
        }

        var collection = await ReadCollectionAsync(route);

        var interval = collection.GetProperty("extent").GetProperty("temporal").GetProperty("interval");
        interval.GetArrayLength().Should().Be(1);
        interval[0].GetArrayLength().Should().Be(2);
        interval[0][0].ValueKind.Should().Be(JsonValueKind.Null);
        interval[0][1].ValueKind.Should().Be(JsonValueKind.Null);
    }

    private void SetExtent(double[] coordinates)
        => _fixture.UpdateV2ResourceMetadata(WebAppFixture.TestLayerId, spatial: new MetadataV2ResourceSpatial
        {
            SpatialReference = MetadataV2SpatialReference.Wgs84,
            StorageCrs = MetadataV2SpatialReference.Wgs84,
            GeometryType = MetadataV2GeometryType.Point,
            PrimaryGeometryField = "shape",
            Bbox = new MetadataV2Bbox
            {
                West = coordinates[0], South = coordinates[1], East = coordinates[2], North = coordinates[3]
            }
        });

    private async Task<double[]> ReadExtentAsync(string route)
    {
        var collection = await ReadCollectionAsync(route);
        return collection.GetProperty("extent").GetProperty("spatial").GetProperty("bbox")[0]
            .EnumerateArray().Select(value => value.GetDouble()).ToArray();
    }

    private async Task<JsonElement> ReadCollectionAsync(string route)
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var url = route == "list" ? "/stac/collections" : $"/stac/collections/{collectionId}";
        using var response = await _fixture.Client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var collection = route == "list"
            ? json.RootElement.GetProperty("collections").EnumerateArray()
                .Single(item => item.GetProperty("id").GetString() == collectionId)
            : json.RootElement;
        return collection.Clone();
    }
}
