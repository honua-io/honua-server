// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.FileImport.Services;
using NetTopologySuite.Features;
using NetTopologySuite.IO;

namespace Honua.Core.Tests.Features.Import;

public sealed class GpxElevationRoundtripTests
{
    [Theory]
    [InlineData("trk", "trkseg", "trkpt")]
    [InlineData("rte", "", "rtept")]
    public async Task ImportExport_TrackAndRouteElevations_PreserveEverySample(string container, string segment, string point)
    {
        var openSegment = segment.Length > 0 ? $"<{segment}>" : "";
        var closeSegment = segment.Length > 0 ? $"</{segment}>" : "";
        var xml = $"<gpx><{container}><name>Profile</name>{openSegment}" +
            $"<{point} lat=\"21.1234567890123\" lon=\"-157.1234567890123\"><ele>30.1234567890123</ele></{point}>" +
            $"<{point} lat=\"22\" lon=\"-156\"><ele>-40.25</ele></{point}>" +
            $"<{point} lat=\"23\" lon=\"-155\"><ele>0</ele></{point}>" +
            $"{closeSegment}</{container}></gpx>";
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var imported = new List<IFeature>();
        await foreach (var feature in GpxFormatReader.ReadStreamingAsync(input, CancellationToken.None))
        {
            imported.Add(feature);
        }

        var source = Assert.Single(imported);
        // GeoJSON is a supported output for GPX-imported features and retains elevation.
        using var exported = new MemoryStream(Encoding.UTF8.GetBytes(new GeoJsonWriter { Dimension = 3 }.Write(new FeatureCollection { source })));
        var reimported = new List<IFeature>();
        await foreach (var feature in new StreamingGeoJsonReader().ReadFeaturesAsync(exported))
        {
            reimported.Add(feature);
        }

        var roundtrip = Assert.Single(reimported);
        Assert.Equal("LineString", roundtrip.Geometry.GeometryType);
        Assert.Equal(3, roundtrip.Geometry.NumPoints);
        Assert.Equal(new[] { -157.1234567890123, -156, -155 }, roundtrip.Geometry.Coordinates.Select(c => c.X));
        Assert.Equal(new[] { 21.1234567890123, 22, 23 }, roundtrip.Geometry.Coordinates.Select(c => c.Y));
        Assert.Equal(new[] { 30.1234567890123, -40.25, 0 }, roundtrip.Geometry.Coordinates.Select(c => c.Z));
        Assert.All(roundtrip.Geometry.Coordinates, c => Assert.True(double.IsNaN(c.M)));
        Assert.Equal("Profile", Assert.IsType<string>(roundtrip.Attributes["name"]));
    }

    [Fact]
    public async Task ReadStreamingAsync_MissingElevation_DoesNotInventOrShiftSamples()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("""
            <gpx><trk><trkseg>
            <trkpt lat="0" lon="0"><ele>30</ele></trkpt>
            <trkpt lat="1" lon="1"><extensions><ext:ele xmlns:ext="urn:example:extension">999</ext:ele></extensions></trkpt>
            <trkpt lat="2" lon="2"><ele>40</ele></trkpt>
            </trkseg></trk></gpx>
            """));
        var features = new List<IFeature>();
        await foreach (var feature in GpxFormatReader.ReadStreamingAsync(input, CancellationToken.None))
        {
            features.Add(feature);
        }

        var geometry = Assert.Single(features).Geometry;
        Assert.Equal(3, geometry.NumPoints);
        Assert.Equal(30, geometry.Coordinates[0].Z);
        Assert.True(double.IsNaN(geometry.Coordinates[1].Z));
        Assert.Equal(40, geometry.Coordinates[2].Z);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public async Task ReadStreamingAsync_InvalidElevation_RejectsInsteadOfDiscardingValue(string elevation)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(
            $"<gpx><rte><rtept lat=\"0\" lon=\"0\"><ele>{elevation}</ele></rtept><rtept lat=\"1\" lon=\"1\"/></rte></gpx>"));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await using var features = GpxFormatReader.ReadStreamingAsync(input, CancellationToken.None).GetAsyncEnumerator();
            await features.MoveNextAsync();
        });
    }

}
