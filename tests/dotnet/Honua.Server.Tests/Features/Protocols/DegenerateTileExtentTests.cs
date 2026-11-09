// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.Ogc.Common;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Protocols;

[Collection("Database")]
public sealed class DegenerateTileExtentTests : IAsyncLifetime
{
    private readonly LimitsOptions _limits = new();
    private readonly WebAppFixture _fixture;

    public DegenerateTileExtentTests()
    {
        _fixture = new WebAppFixture().ReplaceService<IOptions<LimitsOptions>>(Options.Create(_limits));
    }
    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData("-156.45 20.88 -156.45 20.88", 16)]
    [InlineData("-156.45 20.0 -156.45 21.0", 18)]
    [InlineData("-157.0 20.88 -156.0 20.88", 20)]
    [InlineData("-157.0 20.0 -156.0 21.0", 18)]
    [Protocol(TestProtocols.Wmts10, TestProtocols.OgcApiTiles)]
    [Operation(Operations.Wmts, Operations.GetMetadata)]
    [Endpoint("GET /ogc/services/{serviceId}/wmts")]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}")]
    [InterfaceOperation(TestProtocols.Wmts10, "GetCapabilities")]
    public async Task Discovery_PointLineOrArea_AdvertisesPositiveContainingBounds(string sourceExtent, int maxZoom)
    {
        _limits.Tiles.MaxTileZoom = maxZoom;
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
        var matrices = document.Root!.Element(wmts + "Contents")!.Elements(wmts + "TileMatrixSet")
            .Single(element => element.Element(ows + "Identifier")!.Value == "WebMercatorQuad").Elements(wmts + "TileMatrix").ToArray();
        matrices[^1].Element(ows + "Identifier")!.Value.Should().Be(maxZoom.ToString(CultureInfo.InvariantCulture));
        var longitudeTileSpan = 360.0 / int.Parse(matrices[^1].Element(wmts + "MatrixWidth")!.Value, CultureInfo.InvariantCulture);
        var layer = document.Descendants(wmts + "Layer").Single(element =>
            element.Element(ows + "Identifier")!.Value == "1");
        var bounds = layer.Element(ows + "WGS84BoundingBox")!;
        var lower = Coordinates(bounds.Element(ows + "LowerCorner")!.Value);
        var upper = Coordinates(bounds.Element(ows + "UpperCorner")!.Value);
        AssertContainingArea([lower[0], lower[1], upper[0], upper[1]], [west, south, east, north], longitudeTileSpan);
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
        AssertContainingArea(bbox, [west, south, east, north], longitudeTileSpan);
        _fixture.GetCurrentV2GraphSnapshot().Index.ResourcesByStorageLayerId[1].Spatial!.Bbox.Should()
            .BeEquivalentTo(new MetadataV2Bbox { West = west, South = south, East = east, North = north });
    }

    private static double[] Coordinates(string value) => value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(number => double.Parse(number, CultureInfo.InvariantCulture)).ToArray();

    private static void AssertContainingArea(double[] actual, double[] source, double minimumSpan)
    {
        actual[2].Should().BeGreaterThan(actual[0]);
        actual[3].Should().BeGreaterThan(actual[1]);
        actual[0].Should().BeLessThanOrEqualTo(source[0]);
        actual[1].Should().BeLessThanOrEqualTo(source[1]);
        actual[2].Should().BeGreaterThanOrEqualTo(source[2]);
        actual[3].Should().BeGreaterThanOrEqualTo(source[3]);
        if (source[2] == source[0])
        {
            (actual[2] - actual[0]).Should().BeApproximately(minimumSpan, 1e-10);
        }
        if (source[3] == source[1])
        {
            (actual[3] - actual[1]).Should().BeApproximately(minimumSpan, 1e-10);
        }
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

public sealed class OgcTileExtentTests
{
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

    [UnitTheory]
    [InlineData(18, 0.001373291015625)]
    [InlineData(24, 0.000021457672119140625)]
    public void ExpandDegenerateAxes_InteriorPoint_UsesOneFinestTileCenteredOnThePoint(int maxZoom, double expectedSpan)
    {
        var extent = OgcTileExtent.ExpandDegenerateAxes((-156.45, 20.88, -156.45, 20.88), maxZoom);
        (extent.MaxLon - extent.MinLon).Should().BeApproximately(expectedSpan, 1e-12);
        (extent.MaxLat - extent.MinLat).Should().BeApproximately(expectedSpan, 1e-12);
        ((extent.MinLon + extent.MaxLon) / 2).Should().BeApproximately(-156.45, 1e-12);
        ((extent.MinLat + extent.MaxLat) / 2).Should().BeApproximately(20.88, 1e-12);
        var line = OgcTileExtent.ExpandDegenerateAxes((-156.45, 20, -156.45, 21), maxZoom);
        line.MinLat.Should().Be(20);
        line.MaxLat.Should().Be(21);
    }

    [UnitTheory]
    [InlineData(-1, 0)]
    [InlineData(25, 24)]
    public void ExpandDegenerateAxes_OutOfRangeZoom_ClampsToSupportedGrid(int input, int supported)
    {
        var source = (MinLon: -156.45, MinLat: 20.88, MaxLon: -156.45, MaxLat: 20.88);
        OgcTileExtent.ExpandDegenerateAxes(source, input).Should().Be(OgcTileExtent.ExpandDegenerateAxes(source, supported));
    }
}
