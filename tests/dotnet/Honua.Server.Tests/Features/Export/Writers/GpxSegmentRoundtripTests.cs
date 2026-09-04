// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text;
using Honua.Core.Features.FileImport.Services;
using Honua.Io.Export;
using Honua.Io.Export.Writers;
using NetTopologySuite.IO;
using Feature = Honua.Core.Features.FeatureStore.Domain.Feature;

namespace Honua.Server.Tests.Features.Export.Writers;

public sealed class GpxSegmentRoundtripTests
{
    [Theory]
    [InlineData("<trkseg><trkpt lon=\"0\" lat=\"0\"/><trkpt lon=\"1\" lat=\"0\"/></trkseg><trkseg><trkpt lon=\"10\" lat=\"0\"/><trkpt lon=\"11\" lat=\"0\"/></trkseg>", "MULTILINESTRING ((0 0, 1 0), (10 0, 11 0))")]
    [InlineData("<trkseg/><trkseg><trkpt lon=\"0\" lat=\"0\"/><trkpt lon=\"1\" lat=\"0\"/></trkseg><trkseg/>", "LINESTRING (0 0, 1 0)")]
    [InlineData("<trkseg><trkpt lon=\"0\" lat=\"0\"/><trkpt lon=\"1\" lat=\"0\"/></trkseg><trkseg><trkpt lon=\"10\" lat=\"0\"/></trkseg>", "GEOMETRYCOLLECTION (LINESTRING (0 0, 1 0), POINT (10 0))")]
    [InlineData("<trkseg><trkpt lon=\"10\" lat=\"0\"/></trkseg>", "POINT (10 0)")]
    public async Task ImportExport_DisconnectedTrackSegments_PreservesGeometryAndAttributes(string segments, string expectedWkt)
    {
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(
            $"<gpx xmlns=\"http://www.topografix.com/GPX/1/1\"><trk><name>Hawaiʻi track</name>{segments}</trk></gpx>"));
        var imported = new List<NetTopologySuite.Features.IFeature>();
        await foreach (var feature in GpxFormatReader.ReadStreamingAsync(input, CancellationToken.None))
        {
            imported.Add(feature);
        }

        var source = Assert.Single(imported);
        await using var output = new MemoryStream();
        var count = await CsvExportWriter.WriteAsync(output, Rows(Feature.Create(1,
            new WKBWriter().Write(source.Geometry),
            ImmutableDictionary<string, object?>.Empty.Add("name", source.Attributes["name"]))),
            [new ExportField("name", ExportFieldType.String, true)], CancellationToken.None);
        Assert.Equal(1, count);
        output.Position = 0;
        var exported = new List<NetTopologySuite.Features.IFeature>();
        await foreach (var feature in CsvFormatReader.ReadStreamingAsync(output, CancellationToken.None))
        {
            exported.Add(feature);
        }

        var roundtrip = Assert.Single(exported);
        var expected = new WKTReader().Read(expectedWkt);
        Assert.True(expected.EqualsExact(roundtrip.Geometry), $"Expected {expected}; actual {roundtrip.Geometry}");
        Assert.Equal(expected.Length, roundtrip.Geometry.Length);
        Assert.Equal(expected.NumPoints, roundtrip.Geometry.NumPoints);
        Assert.All(roundtrip.Geometry.Coordinates, coordinate =>
        {
            Assert.True(double.IsNaN(coordinate.Z));
            Assert.True(double.IsNaN(coordinate.M));
        });
        Assert.Equal("Hawaiʻi track", Assert.IsType<string>(roundtrip.Attributes["name"]));
    }

    private static async IAsyncEnumerable<Feature> Rows(Feature feature)
    {
        yield return feature;
        await Task.CompletedTask;
    }
}
