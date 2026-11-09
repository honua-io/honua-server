// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.Ogc.Common;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols;

[Collection("Database")]
public sealed class DegenerateTileExtentTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData("-156.45 20.88 -156.45 20.88")]
    [InlineData("-156.45 20.0 -156.45 21.0")]
    [InlineData("-157.0 20.88 -156.0 20.88")]
    [InlineData("-157.0 20.0 -156.0 21.0")]
    [Protocol(TestProtocols.Wmts10, TestProtocols.OgcApiTiles)]
    [Operation(Operations.Wmts, Operations.GetMetadata)]
    [Endpoint("GET /ogc/services/{serviceId}/wmts")]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}")]
    [InterfaceOperation(TestProtocols.Wmts10, "GetCapabilities")]
    public async Task Discovery_PointLineOrArea_AdvertisesPositiveContainingBounds(string sourceExtent)
    {
        var source = Coordinates(sourceExtent);
        var (west, south, east, north) = (source[0], source[1], source[2], source[3]);
        var resource = _fixture.GetCurrentV2GraphSnapshot().Index.ResourcesByStorageLayerId[1];
        _fixture.UpdateV2ResourceMetadata(1, spatial: resource.Spatial! with
        {
            Bbox = new MetadataV2Bbox { West = west, South = south, East = east, North = north }
        });
        using var response = await _fixture.Client.GetAsync("/ogc/services/test/wmts?SERVICE=WMTS&REQUEST=GetCapabilities");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        XNamespace wmts = "http://www.opengis.net/wmts/1.0";
        XNamespace ows = "http://www.opengis.net/ows/1.1";
        var document = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var layer = document.Descendants(wmts + "Layer").Single(element =>
            element.Element(ows + "Identifier")!.Value == "1");
        var bounds = layer.Element(ows + "WGS84BoundingBox")!;
        var lower = Coordinates(bounds.Element(ows + "LowerCorner")!.Value);
        var upper = Coordinates(bounds.Element(ows + "UpperCorner")!.Value);
        AssertContainingArea([lower[0], lower[1], upper[0], upper[1]], [west, south, east, north]);
        foreach (var limits in layer.Descendants(wmts + "TileMatrixLimits"))
        {
            int.Parse(limits.Element(wmts + "MaxTileRow")!.Value, CultureInfo.InvariantCulture).Should()
                .BeGreaterThanOrEqualTo(int.Parse(limits.Element(wmts + "MinTileRow")!.Value, CultureInfo.InvariantCulture));
            int.Parse(limits.Element(wmts + "MaxTileCol")!.Value, CultureInfo.InvariantCulture).Should()
                .BeGreaterThanOrEqualTo(int.Parse(limits.Element(wmts + "MinTileCol")!.Value, CultureInfo.InvariantCulture));
        }
        using var collection = await _fixture.Client.GetAsync("/ogc/tiles/collections/1");
        collection.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await collection.Content.ReadAsStringAsync());
        var bbox = json.RootElement.GetProperty("extent").GetProperty("spatial").GetProperty("bbox")[0]
            .EnumerateArray().Select(value => value.GetDouble()).ToArray();
        AssertContainingArea(bbox, [west, south, east, north]);
        _fixture.GetCurrentV2GraphSnapshot().Index.ResourcesByStorageLayerId[1].Spatial!.Bbox.Should()
            .BeEquivalentTo(new MetadataV2Bbox { West = west, South = south, East = east, North = north });
    }

    [UnitTheory]
    [InlineData(0)]
    [InlineData(18)]
    [InlineData(24)]
    public void ExpandDegenerateAxes_WorldBoundary_ContainsPointWithinValidCoordinates(int maxZoom)
    {
        var northeast = OgcTileExtent.ExpandDegenerateAxes((180, 90, 180, 90), maxZoom);
        northeast.MinLon.Should().BeLessThan(northeast.MaxLon);
        northeast.MinLat.Should().BeLessThan(northeast.MaxLat);
        northeast.MinLon.Should().BeGreaterThanOrEqualTo(-180);
        northeast.MinLat.Should().BeGreaterThanOrEqualTo(-90);
        northeast.MaxLon.Should().Be(180);
        northeast.MaxLat.Should().Be(90);
        var southwest = OgcTileExtent.ExpandDegenerateAxes((-180, -90, -180, -90), maxZoom);
        southwest.MinLon.Should().Be(-180);
        southwest.MinLat.Should().Be(-90);
        southwest.MaxLon.Should().BeGreaterThan(southwest.MinLon).And.BeLessThanOrEqualTo(180);
        southwest.MaxLat.Should().BeGreaterThan(southwest.MinLat).And.BeLessThanOrEqualTo(90);
    }

    private static double[] Coordinates(string value) => value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(number => double.Parse(number, CultureInfo.InvariantCulture)).ToArray();

    private static void AssertContainingArea(double[] actual, double[] source)
    {
        actual[2].Should().BeGreaterThan(actual[0]);
        actual[3].Should().BeGreaterThan(actual[1]);
        actual[0].Should().BeLessThanOrEqualTo(source[0]);
        actual[1].Should().BeLessThanOrEqualTo(source[1]);
        actual[2].Should().BeGreaterThanOrEqualTo(source[2]);
        actual[3].Should().BeGreaterThanOrEqualTo(source[3]);
        if (source[2] > source[0])
        {
            actual[0].Should().Be(source[0]);
            actual[2].Should().Be(source[2]);
        }
        if (source[3] > source[1])
        {
            actual[1].Should().Be(source[1]);
            actual[3].Should().Be(source[3]);
        }
    }
}
