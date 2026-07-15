// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.FileImport.Services;
using NetTopologySuite.Geometries;

namespace Honua.Core.Tests.Features.Import;

// Regression coverage for honua-server#2352: Esri JSON feature sets must parse into features with
// their geometry and attributes rather than being mis-read as GeoJSON.
public sealed class EsriJsonFormatReaderTests
{
    private static async Task<List<NetTopologySuite.Features.IFeature>> ReadAsync(string json)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var features = new List<NetTopologySuite.Features.IFeature>();
        await foreach (var feature in EsriJsonFormatReader.ReadStreamingAsync(stream, CancellationToken.None))
        {
            features.Add(feature);
        }

        return features;
    }

    [Fact]
    public async Task ReadStreamingAsync_PointFeatureSet_ReturnsPointsWithAttributes()
    {
        const string json = """
            {"geometryType":"esriGeometryPoint","spatialReference":{"wkid":4326},"features":[
            {"attributes":{"zone_code":"030","zone_name":"Residential"},"geometry":{"x":-156.30,"y":20.80}},
            {"attributes":{"zone_code":"500","zone_name":"Commercial"},"geometry":{"x":-156.40,"y":20.90}}]}
            """;

        var features = await ReadAsync(json);

        features.Should().HaveCount(2);
        var first = features[0];
        first.Geometry.Should().BeOfType<Point>();
        first.Geometry!.Coordinate.X.Should().Be(-156.30);
        first.Geometry!.Coordinate.Y.Should().Be(20.80);
        first.Geometry!.SRID.Should().Be(4326);
        first.Attributes["zone_code"].Should().Be("030");
        first.Attributes["zone_name"].Should().Be("Residential");

        features[1].Attributes["zone_code"].Should().Be("500");
    }

    [Theory]
    // ArcGIS commonly emits a legacy Esri-only wkid with no latestWkid; these must map to the
    // EPSG code (3857) rather than pass through verbatim and fail import SRID validation.
    [InlineData("{\"spatialReference\":{\"wkid\":102100},\"features\":[]}", 3857)]
    [InlineData("{\"spatialReference\":{\"wkid\":102113},\"features\":[]}", 3857)]
    [InlineData("{\"spatialReference\":{\"wkid\":900913},\"features\":[]}", 3857)]
    // A registered EPSG code passes through unchanged.
    [InlineData("{\"spatialReference\":{\"wkid\":26910},\"features\":[]}", 26910)]
    // latestWkid (modern EPSG) wins over the legacy wkid when both are present.
    [InlineData("{\"spatialReference\":{\"wkid\":102100,\"latestWkid\":3857},\"features\":[]}", 3857)]
    // No spatial reference at all → undetected.
    [InlineData("{\"features\":[]}", null)]
    public async Task TryDetectSridAsync_ResolvesEsriWkid(string json, int? expected)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var srid = await EsriJsonFormatReader.TryDetectSridAsync(stream, CancellationToken.None);
        srid.Should().Be(expected);
    }

    [Fact]
    public async Task ReadStreamingAsync_PolygonRings_ReturnsPolygon()
    {
        const string json = """
            {"geometryType":"esriGeometryPolygon","spatialReference":{"wkid":4326},"features":[
            {"attributes":{"id":1},"geometry":{"rings":[[[0,0],[0,10],[10,10],[10,0],[0,0]]]}}]}
            """;

        var features = await ReadAsync(json);

        features.Should().ContainSingle();
        features[0].Geometry.Should().BeAssignableTo<Polygon>();
        features[0].Geometry!.Area.Should().BeApproximately(100, 0.001);
        features[0].Attributes["id"].Should().Be(1L);
    }

    [Fact]
    public async Task ReadStreamingAsync_PolylinePaths_ReturnsLineString()
    {
        const string json = """
            {"geometryType":"esriGeometryPolyline","spatialReference":{"wkid":4326},"features":[
            {"attributes":{"id":2},"geometry":{"paths":[[[0,0],[1,1],[2,2]]]}}]}
            """;

        var features = await ReadAsync(json);

        features.Should().ContainSingle();
        features[0].Geometry.Should().BeOfType<LineString>();
        features[0].Geometry!.NumPoints.Should().Be(3);
    }
}
